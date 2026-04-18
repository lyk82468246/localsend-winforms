using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Localsend.Backend.Http;
using Localsend.Backend.Protocol;
using Localsend.Backend.Sender;
using Localsend.Backend.Util;

namespace Localsend.Backend.Receiver
{
    /// <summary>注册 v1 REST 端点到 HttpServer。</summary>
    internal sealed class V1ApiHandler
    {
        private readonly DeviceInfo _self;
        private readonly SessionManager _sessions;
        private readonly IReceivePolicy _policy;
        private readonly string _downloadDir;
        private readonly PeerRegistry _peers;

        public V1ApiHandler(DeviceInfo self, SessionManager sessions, IReceivePolicy policy, string downloadDir, PeerRegistry peers)
        {
            _self = self;
            _sessions = sessions;
            _policy = policy ?? new AutoAcceptPolicy();
            _downloadDir = downloadDir;
            _peers = peers;
            if (!Directory.Exists(_downloadDir)) Directory.CreateDirectory(_downloadDir);
        }

        public void Register(HttpServer server)
        {
            server.Map("GET",  Constants.ApiV1 + "/info",         Info);
            server.Map("POST", Constants.ApiV1 + "/send-request", SendRequestEndpoint);
            server.Map("POST", Constants.ApiV1 + "/send",         SendEndpoint);
            server.Map("POST", Constants.ApiV1 + "/cancel",       CancelEndpoint);
            // HTTP 注册兜底：对端多播收不到我们时，会直接 POST 过来告诉我们它自己；
            // 我们回复自己的 info 作为应答。v1 / v2 两条路径都挂。
            server.Map("POST", Constants.ApiV1 + "/register",     RegisterEndpoint);
            server.Map("POST", Constants.ApiV2 + "/register",     RegisterEndpoint);
            // 兼容 v2 一些客户端可能先 GET /info：也挂到 v2 路径（只暴露设备信息）
            server.Map("GET",  Constants.ApiV2 + "/info",         Info);
        }

        private void Info(HttpContext ctx)
        {
            // 只暴露 v1 字段，与 announce 一致，避免被认作 v2 对端。
            Dictionary<string, object> o = _self.ToJson(false);
            ctx.SendJson(200, Json.Stringify(o));
        }

        private void RegisterEndpoint(HttpContext ctx)
        {
            string body = ReadAllText(ctx.Body);
            Dictionary<string, object> root;
            try { root = Json.ParseObject(body); }
            catch { ctx.SendText(400, "Bad JSON"); return; }

            DeviceInfo info;
            try { info = DeviceInfo.FromJson(root); }
            catch { ctx.SendText(400, "Bad register payload"); return; }

            if (info == null || string.IsNullOrEmpty(info.Fingerprint))
            {
                Log.Warn("Register from " + ctx.Remote + " missing fingerprint; body=" + body);
                ctx.SendText(400, "Missing fingerprint");
                return;
            }

            IPAddress src = ctx.Remote != null ? ctx.Remote.Address : IPAddress.Any;
            if (_peers != null) _peers.Upsert(info, src);
            Log.Info("Register: fp=" + info.Fingerprint + " alias=" + info.Alias
                + " proto=" + info.Protocol + " port=" + info.Port + " src=" + src);

            // 回复我们自己的 v1 info（与 announce 字段一致）
            ctx.SendJson(200, Json.Stringify(_self.ToJson(false)));
        }

        private void SendRequestEndpoint(HttpContext ctx)
        {
            string body = ReadAllText(ctx.Body);
            Dictionary<string, object> root;
            try { root = Json.ParseObject(body); }
            catch { ctx.SendText(400, "Bad JSON"); return; }

            SendRequest req;
            try { req = SendRequest.FromJson(root); }
            catch { ctx.SendText(400, "Bad send-request"); return; }

            if (req.Files == null || req.Files.Count == 0) { ctx.SendText(400, "No files"); return; }

            Dictionary<string, string> tokens = _sessions.TryBegin(req.Info, req.Files, _policy);
            if (tokens == null) { ctx.SendText(409, "Busy"); return; }
            if (tokens.Count == 0) { ctx.SendEmpty(403); return; } // 全部拒绝

            Dictionary<string, object> reply = new Dictionary<string, object>();
            foreach (KeyValuePair<string, string> kv in tokens) reply[kv.Key] = kv.Value;
            ctx.SendJson(200, Json.Stringify(reply));
        }

        private void SendEndpoint(HttpContext ctx)
        {
            string fileId, token;
            if (!ctx.Query.TryGetValue("fileId", out fileId) || !ctx.Query.TryGetValue("token", out token))
            { ctx.SendText(400, "Missing fileId/token"); return; }

            FileDto meta = _sessions.ValidateUpload(fileId, token);
            if (meta == null) { ctx.SendEmpty(403); return; }
            if (ctx.ContentLength < 0) { ctx.SendText(411, "Length Required"); return; }

            string safeName = SanitizeFileName(meta.FileName);
            string fullPath = UniquePath(Path.Combine(_downloadDir, safeName));

            try
            {
                using (FileStream fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buf = new byte[16 * 1024];
                    long remaining = ctx.ContentLength;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buf.Length, remaining);
                        int n = ctx.Body.Read(buf, 0, toRead);
                        if (n <= 0) throw new IOException("Stream ended early");
                        fs.Write(buf, 0, n);
                        remaining -= n;
                    }
                }
                _sessions.MarkCompleted(fileId);
                ctx.SendEmpty(200);
                Log.Info("Received: " + fullPath);
            }
            catch (Exception ex)
            {
                Log.Error("Upload failed", ex);
                try { File.Delete(fullPath); } catch { }
                ctx.SendText(500, "Upload failed");
            }
        }

        private void CancelEndpoint(HttpContext ctx)
        {
            _sessions.Cancel();
            ctx.SendEmpty(200);
        }

        private static string ReadAllText(Stream s)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[4096];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return Encoding.UTF8.GetString(ms.ToArray(), 0, (int)ms.Length);
            }
        }

        // WinCE 上 Path.GetInvalidFileNameChars 不存在；手写常见非法字符集。
        private static readonly char[] _invalidNameChars = new char[]
        {
            '\\', '/', ':', '*', '?', '"', '<', '>', '|',
            '\0','\x01','\x02','\x03','\x04','\x05','\x06','\x07',
            '\x08','\x09','\x0A','\x0B','\x0C','\x0D','\x0E','\x0F',
            '\x10','\x11','\x12','\x13','\x14','\x15','\x16','\x17',
            '\x18','\x19','\x1A','\x1B','\x1C','\x1D','\x1E','\x1F'
        };

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            char[] bad = _invalidNameChars;
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool isBad = false;
                for (int j = 0; j < bad.Length; j++) if (bad[j] == c) { isBad = true; break; }
                sb.Append(isBad ? '_' : c);
            }
            string cleaned = sb.ToString().Trim();
            if (cleaned.Length == 0) cleaned = "unnamed";
            return cleaned;
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; i < 10000; i++)
            {
                string candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return path + "." + IdGen.NewRandom();
        }
    }
}
