using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Localsend.Backend.Protocol;
using Localsend.Backend.Http;
using Localsend.Backend.Tls;
using Localsend.Backend.Util;

namespace Localsend.Backend.Sender
{
    public sealed class SenderFileSpec
    {
        public string LocalPath;
        public string FileName;
        public long Size;
        public string FileType;

        public static SenderFileSpec FromPath(string path)
        {
            SenderFileSpec s = new SenderFileSpec();
            s.LocalPath = path;
            s.FileName = Path.GetFileName(path);
            FileInfo fi = new FileInfo(path);
            s.Size = fi.Length;
            s.FileType = GuessType(s.FileName);
            return s;
        }

        private static string GuessType(string name)
        {
            string ext = Path.GetExtension(name);
            if (!string.IsNullOrEmpty(ext)) ext = ext.ToLower();
            switch (ext)
            {
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".mp4": return "video/mp4";
                case ".avi": return "video/x-msvideo";
                case ".mov": return "video/quicktime";
                case ".mkv": return "video/x-matroska";
                case ".webm": return "video/webm";
                case ".3gp": return "video/3gpp";
                case ".pdf": return "application/pdf";
                case ".txt": return "text/plain";
                case ".md": case ".log": return "text/markdown";
                case ".csv": return "text/csv";
                default: return "application/octet-stream";
            }
        }
    }

    public enum SendStage { Preparing, Uploading, FileDone, JobDone, Rejected, Cancelled, Failed }

    public sealed class SendProgressEventArgs : EventArgs
    {
        public string JobId;
        public string FileId;
        public string FileName;
        public long BytesSent;
        public long TotalBytes;
        public SendStage Stage;
        public string Message;
    }

    internal sealed class SendJob
    {
        public string Id;
        public string SessionId;
        public Peer Peer;
        public SenderFileSpec[] Files;
        public volatile bool Cancel;
    }

    /// <summary>
    /// 向已知对端发送文件。单次调用发起一个后台任务。
    /// 协议：优先使用 LocalSend v2.2 prepare-upload/upload；对旧设备保留 v1。
    /// </summary>
    public sealed class OutboundSender
    {
        private readonly DeviceInfo _self;
        private readonly TlsProviderRouter _tls;
        private readonly bool _fullEncryption;
        private readonly object _lock = new object();
        private readonly Dictionary<string, SendJob> _jobs = new Dictionary<string, SendJob>();

        public event EventHandler<SendProgressEventArgs> Progress;

        internal OutboundSender(DeviceInfo self)
            : this(self, null, false) { }

        internal OutboundSender(DeviceInfo self, TlsProviderRouter tls, bool fullEncryption)
        {
            _self = self;
            _tls = tls;
            _fullEncryption = fullEncryption;
        }

        public string Send(Peer peer, SenderFileSpec[] files)
        {
            if (peer == null) throw new ArgumentNullException("peer");
            if (files == null || files.Length == 0) throw new ArgumentException("files empty");

            SendJob job = new SendJob();
            job.Id = IdGen.NewRandom();
            job.Peer = peer;
            job.Files = files;

            lock (_lock) { _jobs[job.Id] = job; }
            ThreadPool.QueueUserWorkItem(delegate { RunJob(job); });
            return job.Id;
        }

        public void Cancel(string jobId)
        {
            SendJob job = null;
            lock (_lock) { _jobs.TryGetValue(jobId, out job); }
            if (job != null) job.Cancel = true;
        }

        private void RunJob(SendJob job)
        {
            try
            {
                Emit(job, null, null, 0, 0, SendStage.Preparing, null);

                // 1. send-request
                Dictionary<string, string> idToLocalPath = new Dictionary<string, string>();
                Dictionary<string, SenderFileSpec> idToSpec = new Dictionary<string, SenderFileSpec>();
                Dictionary<string, object> filesObj = new Dictionary<string, object>();
                for (int i = 0; i < job.Files.Length; i++)
                {
                    SenderFileSpec f = job.Files[i];
                    string id = IdGen.NewRandom();
                    idToLocalPath[id] = f.LocalPath;
                    idToSpec[id] = f;

                    Dictionary<string, object> entry = new Dictionary<string, object>();
                    entry["id"] = id;
                    entry["fileName"] = f.FileName;
                    entry["size"] = f.Size;
                    entry["fileType"] = f.FileType;
                    entry["preview"] = null;
                    filesObj[id] = entry;
                }

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["info"] = _self.ToJson(true);
                root["files"] = filesObj;
                string reqJson = Json.Stringify(root);

                bool v2 = IsV2Peer(job.Peer);
                string prepareUrl = job.Peer.BaseUrl
                    + (v2 ? Constants.ApiV2 + "/prepare-upload" : Constants.ApiV1 + "/send-request");
                int prepareStatus;
                string tokensJson = PostJson(job.Peer, prepareUrl, reqJson, out prepareStatus);
                if (job.Cancel)
                {
                    TryCancelRemote(job, v2);
                    Emit(job, null, null, 0, 0, SendStage.Cancelled, null);
                    return;
                }
                // v2.2 uses 204 to mean that no transfer is needed (for
                // example, every requested file was already handled).  It is
                // a successful no-op rather than a rejection.
                if (v2 && prepareStatus == 204)
                {
                    Emit(job, null, null, 0, 0, SendStage.JobDone, null);
                    return;
                }

                Dictionary<string, object> tokensObj;
                try
                {
                    Dictionary<string, object> prepared = Json.ParseObject(tokensJson);
                    if (v2)
                    {
                        job.SessionId = JsonHelpers.AsString(prepared, "sessionId");
                        if (string.IsNullOrEmpty(job.SessionId))
                        {
                            Emit(job, null, null, 0, 0, SendStage.Failed, "Peer did not return a sessionId");
                            return;
                        }
                        tokensObj = prepared.ContainsKey("files")
                            ? prepared["files"] as Dictionary<string, object>
                            : null;
                        if (tokensObj == null) tokensObj = new Dictionary<string, object>();
                    }
                    else tokensObj = prepared;
                }
                catch { Emit(job, null, null, 0, 0, SendStage.Failed, "Bad response from peer"); return; }

                if (tokensObj.Count == 0)
                {
                    if (v2 && prepareStatus == 204)
                    {
                        Emit(job, null, null, 0, 0, SendStage.JobDone, null);
                        return;
                    }
                    Emit(job, null, null, 0, 0, SendStage.Rejected, "Peer rejected all files");
                    return;
                }

                // 2. 依次上传每个被接受的文件
                foreach (KeyValuePair<string, object> kv in tokensObj)
                {
                    if (job.Cancel)
                    {
                        TryCancelRemote(job, v2);
                        Emit(job, null, null, 0, 0, SendStage.Cancelled, null);
                        return;
                    }

                    string fileId = kv.Key;
                    string token = kv.Value == null ? null : kv.Value.ToString();
                    if (!idToSpec.ContainsKey(fileId) || string.IsNullOrEmpty(token)) continue;

                    SenderFileSpec spec = idToSpec[fileId];
                    string url = job.Peer.BaseUrl
                               + (v2 ? Constants.ApiV2 + "/upload" : Constants.ApiV1 + "/send")
                               + "?" + (v2 ? "sessionId=" + UrlEncode(job.SessionId) + "&" : "")
                               + "fileId=" + UrlEncode(fileId)
                               + "&token=" + UrlEncode(token);

                    try { UploadFile(job, fileId, spec, url); }
                    catch (Exception ex)
                    {
                        if (job.Cancel)
                        {
                            TryCancelRemote(job, v2);
                            Emit(job, null, null, 0, 0, SendStage.Cancelled, null);
                            return;
                        }
                        Log.Error("Upload failed for " + spec.FileName, ex);
                        Emit(job, fileId, spec.FileName, 0, spec.Size, SendStage.Failed, ex.Message);
                        return;
                    }
                    Emit(job, fileId, spec.FileName, spec.Size, spec.Size, SendStage.FileDone, null);
                }

                Emit(job, null, null, 0, 0, SendStage.JobDone, null);
            }
            catch (Exception ex)
            {
                Log.Error("Job failed", ex);
                Emit(job, null, null, 0, 0, SendStage.Failed, ex.Message);
            }
            finally
            {
                lock (_lock) { _jobs.Remove(job.Id); }
            }
        }

        private void UploadFile(SendJob job, string fileId, SenderFileSpec spec, string url)
        {
            SimpleHttpClient http = CreateHttpClient(job.Peer);
            SimpleHttpResponse resp = http.PostFile(
                url, spec.LocalPath, "application/octet-stream", spec.Size,
                ExpectedFingerprint(job.Peer), delegate(long sent)
                {
                    if (job.Cancel) throw new IOException("Cancelled");
                    Emit(job, fileId, spec.FileName, sent, spec.Size, SendStage.Uploading, null);
                });
            if (resp.StatusCode >= 400)
                throw new IOException("Upload rejected: HTTP " + resp.StatusCode);
        }

        private string PostJson(Peer peer, string url, string json, out int statusCode)
        {
            // The peer URL carries the scheme selected by discovery.  The
            // TLS provider is only supplied for an HTTPS peer, never for a
            // clear-text request.
            SimpleHttpClient http = CreateHttpClient(peer);
            SimpleHttpResponse resp = http.PostJson(url, json, ExpectedFingerprint(peer));
            statusCode = resp.StatusCode;
            if (resp.StatusCode >= 400)
                throw new IOException("HTTP " + resp.StatusCode + " from peer");
            return resp.BodyText;
        }

        private void TryCancelRemote(SendJob job, bool v2)
        {
            if (job == null || job.Peer == null) return;
            if (v2 && string.IsNullOrEmpty(job.SessionId)) return;
            string url = job.Peer.BaseUrl + (v2 ? Constants.ApiV2 + "/cancel?sessionId="
                + UrlEncode(job.SessionId) : Constants.ApiV1 + "/cancel");
            try
            {
                SimpleHttpClient http = CreateHttpClient(job.Peer);
                SimpleHttpResponse response = http.PostJson(
                    url, "", ExpectedFingerprint(job.Peer));
                if (response.StatusCode >= 400)
                    Log.Warn("Remote cancel rejected: HTTP " + response.StatusCode);
            }
            catch (Exception ex)
            { Log.Warn("Remote cancel failed: " + ex.Message); }
        }

        private SimpleHttpClient CreateHttpClient(Peer peer)
        {
            bool secure = peer != null && peer.Protocol == "https";
            if (secure && (!_fullEncryption || _tls == null || !_tls.SupportsHttpsTransport))
                throw new IOException("Peer requires encrypted transport, but this device cannot send encrypted data");
            return new SimpleHttpClient(secure ? _tls.Provider : null, 15000, 60000);
        }

        private static string ExpectedFingerprint(Peer peer)
        {
            if (peer == null || peer.Protocol != "https") return "";
            return peer.Fingerprint ?? "";
        }

        private static string UrlEncode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                         || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~';
                if (safe) sb.Append(c);
                else
                {
                    byte[] enc = Encoding.UTF8.GetBytes(new char[] { c });
                    for (int j = 0; j < enc.Length; j++) sb.Append('%').Append(enc[j].ToString("X2"));
                }
            }
            return sb.ToString();
        }

        private static bool IsV2Peer(Peer peer)
        {
            if (peer == null || string.IsNullOrEmpty(peer.Version)) return false;
            return peer.Version.StartsWith("2", StringComparison.OrdinalIgnoreCase);
        }

        private void Emit(SendJob job, string fileId, string fileName, long bytes, long total, SendStage stage, string msg)
        {
            EventHandler<SendProgressEventArgs> h = Progress;
            if (h == null) return;
            SendProgressEventArgs e = new SendProgressEventArgs();
            e.JobId = job.Id;
            e.FileId = fileId;
            e.FileName = fileName;
            e.BytesSent = bytes;
            e.TotalBytes = total;
            e.Stage = stage;
            e.Message = msg;
            try { h(this, e); } catch { }
        }
    }
}
