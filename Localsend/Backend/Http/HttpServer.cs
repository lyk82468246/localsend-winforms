using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Localsend.Backend.Util;

namespace Localsend.Backend.Http
{
    /// <summary>HTTP 请求上下文。请求体以流的方式提供，不提前缓冲（大文件上传友好）。</summary>
    internal sealed class HttpContext
    {
        public string Method;
        public string RawPath;    // 含 query
        public string Path;       // 去掉 query
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public long ContentLength = -1;
        public Stream Body;        // 限长度的请求体流
        public IPEndPoint Remote;

        internal Stream _out;      // 响应写入目标
        private bool _sent;

        public void Send(int status, string contentType, byte[] body)
        {
            if (_sent) throw new InvalidOperationException("Response already sent");
            _sent = true;
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
            if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body == null ? 0 : body.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            _out.Write(head, 0, head.Length);
            if (body != null && body.Length > 0) _out.Write(body, 0, body.Length);
            _out.Flush();
        }

        public void SendText(int status, string text)
        { Send(status, "text/plain; charset=utf-8", text == null ? null : Encoding.UTF8.GetBytes(text)); }

        public void SendJson(int status, string jsonText)
        { Send(status, "application/json", jsonText == null ? null : Encoding.UTF8.GetBytes(jsonText)); }

        public void SendEmpty(int status) { Send(status, null, null); }

        private static string ReasonPhrase(int s)
        {
            switch (s)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 413: return "Payload Too Large";
                case 500: return "Internal Server Error";
                default: return "OK";
            }
        }
    }

    /// <summary>路由处理委托。</summary>
    internal delegate void HttpHandler(HttpContext ctx);

    /// <summary>
    /// 基于 TcpListener 手写的极简 HTTP/1.1 服务器。仅支持 Content-Length 请求体。
    /// 每连接使用一个 ThreadPool 工作项。
    /// </summary>
    internal sealed class HttpServer : IDisposable
    {
        private readonly int _port;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private readonly object _routesLock = new object();
        private readonly List<Route> _routes = new List<Route>();

        private sealed class Route { public string Method; public string Path; public HttpHandler Handler; }

        public HttpServer(int port) { _port = port; }

        public void Map(string method, string path, HttpHandler handler)
        {
            lock (_routesLock)
            {
                Route r = new Route();
                r.Method = method.ToUpper();
                r.Path = path;
                r.Handler = handler;
                _routes.Add(r);
            }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "LS-Http-Accept";
            _acceptThread.Start();
            Log.Info("HTTP server listening on :" + _port);
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
        }

        public void Dispose() { Stop(); }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (SocketException) { if (_running) continue; else break; }
                catch (InvalidOperationException) { break; }

                TcpClient c = client;
                try { Log.Info("HTTP accept from " + c.Client.RemoteEndPoint); } catch { }
                ThreadPool.QueueUserWorkItem(delegate { HandleClient(c); });
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                NetworkStream ns = client.GetStream();
                HttpContext ctx = ReadRequest(ns, (IPEndPoint)client.Client.RemoteEndPoint);
                if (ctx == null) return;

                Log.Info("HTTP " + ctx.Method + " " + ctx.RawPath + " from " + ctx.Remote
                    + " CL=" + ctx.ContentLength);

                HttpHandler handler = FindRoute(ctx.Method, ctx.Path);
                if (handler == null) { Log.Warn("HTTP 404 " + ctx.Method + " " + ctx.Path); ctx.SendText(404, "Not Found"); return; }

                try { handler(ctx); }
                catch (Exception ex)
                {
                    Log.Error("Handler threw", ex);
                    try { ctx.SendText(500, "Internal Server Error"); } catch { }
                }
            }
            catch (Exception ex) { Log.Warn("Connection error: " + ex.Message); }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private HttpHandler FindRoute(string method, string path)
        {
            lock (_routesLock)
            {
                for (int i = 0; i < _routes.Count; i++)
                {
                    Route r = _routes[i];
                    if (r.Method == method && r.Path == path) return r.Handler;
                }
            }
            return null;
        }

        // --- 请求解析 ---

        private static HttpContext ReadRequest(NetworkStream ns, IPEndPoint remote)
        {
            // 读到 \r\n\r\n 为止
            byte[] headerBuf = ReadUntilHeaderEnd(ns);
            if (headerBuf == null) return null;

            string headerText = Encoding.ASCII.GetString(headerBuf, 0, headerBuf.Length);
            string[] lines = SplitCrLf(headerText);
            if (lines.Length < 1) return null;

            string[] reqLine = lines[0].Split(' ');
            if (reqLine.Length < 3) return null;

            HttpContext ctx = new HttpContext();
            ctx.Method = reqLine[0].ToUpper();
            ctx.RawPath = reqLine[1];
            ctx.Remote = remote;
            ctx._out = ns;

            int q = ctx.RawPath.IndexOf('?');
            if (q >= 0)
            {
                ctx.Path = ctx.RawPath.Substring(0, q);
                ParseQuery(ctx.RawPath.Substring(q + 1), ctx.Query);
            }
            else ctx.Path = ctx.RawPath;

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string val = line.Substring(colon + 1).Trim();
                ctx.Headers[name] = val;
            }

            string cl;
            if (ctx.Headers.TryGetValue("Content-Length", out cl))
            {
                long n;
                if (Parse.TryLong(cl, out n)) ctx.ContentLength = n;
            }

            if (ctx.ContentLength > 0)
                ctx.Body = new LengthLimitedStream(ns, ctx.ContentLength);
            else
                ctx.Body = new LengthLimitedStream(ns, 0);

            return ctx;
        }

        private static byte[] ReadUntilHeaderEnd(NetworkStream ns)
        {
            const int maxHeader = 16 * 1024;
            byte[] buf = new byte[maxHeader];
            int len = 0;
            while (len < maxHeader)
            {
                int b = ns.ReadByte();
                if (b < 0) return null;
                buf[len++] = (byte)b;
                if (len >= 4
                    && buf[len - 4] == (byte)'\r' && buf[len - 3] == (byte)'\n'
                    && buf[len - 2] == (byte)'\r' && buf[len - 1] == (byte)'\n')
                {
                    byte[] r = new byte[len - 4];
                    Buffer.BlockCopy(buf, 0, r, 0, len - 4);
                    return r;
                }
            }
            return null; // 头部过大
        }

        private static void ParseQuery(string qs, Dictionary<string, string> into)
        {
            if (string.IsNullOrEmpty(qs)) return;
            string[] parts = qs.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Length == 0) continue;
                int eq = p.IndexOf('=');
                string k, v;
                if (eq < 0) { k = p; v = ""; }
                else { k = p.Substring(0, eq); v = p.Substring(eq + 1); }
                into[UrlDecode(k)] = UrlDecode(v);
            }
        }

        private static string UrlDecode(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '+') sb.Append(' ');
                else if (c == '%' && i + 2 < s.Length)
                {
                    int hi = HexVal(s[i + 1]), lo = HexVal(s[i + 2]);
                    if (hi >= 0 && lo >= 0) { sb.Append((char)((hi << 4) | lo)); i += 2; }
                    else sb.Append(c);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string[] SplitCrLf(string s)
        {
            List<string> lines = new List<string>();
            int start = 0;
            for (int i = 0; i < s.Length - 1; i++)
            {
                if (s[i] == '\r' && s[i + 1] == '\n')
                {
                    lines.Add(s.Substring(start, i - start));
                    i++;
                    start = i + 1;
                }
            }
            if (start <= s.Length) lines.Add(s.Substring(start));
            return lines.ToArray();
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }

    /// <summary>限定可读字节数的包装流（从基础 NetworkStream 读取精确 Content-Length）。</summary>
    internal sealed class LengthLimitedStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public LengthLimitedStream(Stream inner, long length) { _inner = inner; _remaining = length; }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _remaining; } }
        public override long Position { get { return 0; } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
        public override void SetLength(long v) { throw new NotSupportedException(); }
        public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            int toRead = (int)Math.Min(count, _remaining);
            int n = _inner.Read(buffer, offset, toRead);
            if (n > 0) _remaining -= n;
            return n;
        }
    }
}
