using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Localsend.Backend.Protocol;
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
                case ".jpg": case ".jpeg": case ".png": case ".gif": case ".bmp": return "image";
                case ".mp4": case ".avi": case ".mov": case ".mkv": case ".webm": case ".3gp": return "video";
                case ".pdf": return "pdf";
                case ".txt": case ".md": case ".log": case ".csv": return "text";
                default: return "other";
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
        public Peer Peer;
        public SenderFileSpec[] Files;
        public volatile bool Cancel;
    }

    /// <summary>
    /// 向已知对端发送文件。单次调用发起一个后台任务。
    /// 协议：v1 /send-request + /send。对 HTTPS 对端同样使用 v1 路由（LocalSend 向后兼容）。
    /// </summary>
    public sealed class OutboundSender
    {
        private readonly DeviceInfo _self;
        private readonly object _lock = new object();
        private readonly Dictionary<string, SendJob> _jobs = new Dictionary<string, SendJob>();

        public event EventHandler<SendProgressEventArgs> Progress;

        internal OutboundSender(DeviceInfo self)
        {
            _self = self;
            AllowAnyCertPolicy.Install();
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

                string tokensJson = PostJson(job.Peer.BaseUrl + Constants.ApiV1 + "/send-request", reqJson);
                if (job.Cancel) { Emit(job, null, null, 0, 0, SendStage.Cancelled, null); return; }

                Dictionary<string, object> tokensObj;
                try { tokensObj = Json.ParseObject(tokensJson); }
                catch { Emit(job, null, null, 0, 0, SendStage.Failed, "Bad response from peer"); return; }

                if (tokensObj.Count == 0)
                {
                    Emit(job, null, null, 0, 0, SendStage.Rejected, "Peer rejected all files");
                    return;
                }

                // 2. 依次上传每个被接受的文件
                foreach (KeyValuePair<string, object> kv in tokensObj)
                {
                    if (job.Cancel) { Emit(job, null, null, 0, 0, SendStage.Cancelled, null); return; }

                    string fileId = kv.Key;
                    string token = kv.Value == null ? null : kv.Value.ToString();
                    if (!idToSpec.ContainsKey(fileId) || string.IsNullOrEmpty(token)) continue;

                    SenderFileSpec spec = idToSpec[fileId];
                    string url = job.Peer.BaseUrl + Constants.ApiV1 + "/send"
                               + "?fileId=" + UrlEncode(fileId)
                               + "&token=" + UrlEncode(token);

                    try { UploadFile(job, fileId, spec, url); }
                    catch (Exception ex)
                    {
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
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/octet-stream";
            req.ContentLength = spec.Size;
            req.KeepAlive = false;
            req.AllowWriteStreamBuffering = false;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;

            using (FileStream fs = new FileStream(spec.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (Stream reqStream = req.GetRequestStream())
            {
                byte[] buf = new byte[16 * 1024];
                long sent = 0;
                while (true)
                {
                    if (job.Cancel) { try { req.Abort(); } catch { } throw new IOException("Cancelled"); }
                    int n = fs.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    reqStream.Write(buf, 0, n);
                    sent += n;
                    Emit(job, fileId, spec.FileName, sent, spec.Size, SendStage.Uploading, null);
                }
            }

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                if ((int)resp.StatusCode >= 400)
                    throw new IOException("Upload rejected: HTTP " + (int)resp.StatusCode);
            }
        }

        private static string PostJson(string url, string json)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.KeepAlive = false;
            req.Timeout = 15000;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            req.ContentLength = bytes.Length;
            using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                if ((int)resp.StatusCode == 204) return "{}";
                if ((int)resp.StatusCode >= 400)
                    throw new IOException("HTTP " + (int)resp.StatusCode + " from peer");
                using (Stream s = resp.GetResponseStream())
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
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
