using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Localsend.Backend.Tls;

namespace Localsend.Backend.Http
{
    internal delegate void HttpUploadProgress(long sent);

    /// <summary>
    /// Small HTTP/1.1 client used by LocalSend so the request stream can be
    /// wrapped by either Schannel or another TLS provider.  It deliberately
    /// uses one request per TCP connection and Content-Length bodies only;
    /// this is the subset required by the LocalSend v1/v2 file API.
    /// </summary>
    internal sealed class SimpleHttpClient
    {
        private readonly ITlsProvider _tls;
        private readonly int _connectTimeoutMs;
        private readonly int _readWriteTimeoutMs;

        public SimpleHttpClient(ITlsProvider tls, int connectTimeoutMs, int readWriteTimeoutMs)
        {
            _tls = tls;
            _connectTimeoutMs = connectTimeoutMs > 0 ? connectTimeoutMs : 15000;
            _readWriteTimeoutMs = readWriteTimeoutMs > 0 ? readWriteTimeoutMs : 60000;
        }

        public SimpleHttpResponse Get(string url, string expectedFingerprint)
        {
            return Execute("GET", url, null, 0, null, null, expectedFingerprint);
        }

        public SimpleHttpResponse PostJson(string url, string json, string expectedFingerprint)
        {
            byte[] body = Encoding.UTF8.GetBytes(json ?? "");
            using (MemoryStream stream = new MemoryStream(body, false))
            {
                return Execute("POST", url, stream, body.Length,
                    "application/json", null, expectedFingerprint);
            }
        }

        public SimpleHttpResponse PostFile(
            string url,
            string path,
            string contentType,
            long length,
            string expectedFingerprint,
            HttpUploadProgress progress)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Execute("POST", url, fs, length, contentType,
                    progress, expectedFingerprint);
            }
        }

        private SimpleHttpResponse Execute(
            string method,
            string url,
            Stream body,
            long contentLength,
            string contentType,
            HttpUploadProgress progress,
            string expectedFingerprint)
        {
            Uri uri = new Uri(url);
            bool secure = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            string host = uri.Host;
            int port = uri.Port;
            if (port <= 0) port = secure ? 443 : 80;

            TcpClient client = new TcpClient();
            TlsSession tlsSession = null;
            Stream stream = null;
            try
            {
                Connect(client, host, port);
                client.NoDelay = true;
                NetworkStream network = client.GetStream();
                network.ReadTimeout = _readWriteTimeoutMs;
                network.WriteTimeout = _readWriteTimeoutMs;
                if (secure)
                {
                    if (_tls == null || !_tls.SupportsStreamTransport)
                        throw new InvalidOperationException("HTTPS peer requires a TLS provider");
                    tlsSession = _tls.Connect(network, host, expectedFingerprint);
                    stream = tlsSession.Stream;
                }
                else stream = network;

                string pathAndQuery = uri.AbsolutePath;
                if (string.IsNullOrEmpty(pathAndQuery)) pathAndQuery = "/";
                if (!string.IsNullOrEmpty(uri.Query)) pathAndQuery += uri.Query;

                StringBuilder header = new StringBuilder();
                header.Append(method).Append(' ').Append(pathAndQuery).Append(" HTTP/1.1\r\n");
                header.Append("Host: ").Append(host).Append("\r\n");
                header.Append("Connection: close\r\n");
                if (!string.IsNullOrEmpty(contentType))
                    header.Append("Content-Type: ").Append(contentType).Append("\r\n");
                header.Append("Content-Length: ").Append(contentLength).Append("\r\n\r\n");
                byte[] head = Encoding.ASCII.GetBytes(header.ToString());
                stream.Write(head, 0, head.Length);

                if (body != null && contentLength > 0)
                {
                    byte[] buffer = new byte[16 * 1024];
                    long remaining = contentLength;
                    long sent = 0;
                    while (remaining > 0)
                    {
                        int ask = (int)Math.Min(buffer.Length, remaining);
                        int n = body.Read(buffer, 0, ask);
                        if (n <= 0) throw new EndOfStreamException("HTTP request body ended early");
                        stream.Write(buffer, 0, n);
                        remaining -= n;
                        sent += n;
                        if (progress != null) progress(sent);
                    }
                }
                stream.Flush();
                return ReadResponse(stream);
            }
            finally
            {
                try { if (tlsSession != null) tlsSession.Dispose(); } catch { }
                try { client.Close(); } catch { }
            }
        }

        private void Connect(TcpClient client, string host, int port)
        {
            IPAddress address = null;
            IPHostEntry hostEntry = Dns.GetHostEntry(host);
            IPAddress[] addresses = hostEntry == null ? new IPAddress[0] : hostEntry.AddressList;
            for (int i = 0; i < addresses.Length; i++)
            {
                if (addresses[i] != null && addresses[i].AddressFamily == AddressFamily.InterNetwork)
                { address = addresses[i]; break; }
            }
            if (address == null && addresses.Length > 0) address = addresses[0];
            if (address == null) throw new IOException("Unable to resolve HTTP host");

            IAsyncResult ar = client.Client.BeginConnect(new IPEndPoint(address, port), null, null);
            if (!ar.AsyncWaitHandle.WaitOne(_connectTimeoutMs, false))
            {
                try { client.Close(); } catch { }
                throw new IOException("TCP connect timeout");
            }
            client.Client.EndConnect(ar);
        }

        private static SimpleHttpResponse ReadResponse(Stream stream)
        {
            byte[] headerBytes = ReadUntilHeaderEnd(stream);
            if (headerBytes == null) throw new IOException("HTTP response header missing");
            string headerText = Encoding.ASCII.GetString(headerBytes, 0, headerBytes.Length);
            string[] lines = SplitCrLf(headerText);
            if (lines.Length == 0) throw new IOException("HTTP response status missing");
            string[] statusParts = lines[0].Split(' ');
            if (statusParts.Length < 2) throw new IOException("HTTP response status invalid");
            int status;
            try { status = int.Parse(statusParts[1]); }
            catch
            {
                throw new IOException("HTTP response status invalid");
            }

            SimpleHttpResponse response = new SimpleHttpResponse();
            response.StatusCode = status;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string name = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                response.Headers[name] = value;
            }

            long length = -1;
            string cl;
            if (response.Headers.TryGetValue("Content-Length", out cl))
            {
                try { length = long.Parse(cl); }
                catch { length = -1; }
            }
            if (length < 0)
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    CopyToEnd(stream, ms);
                    response.Body = ms.ToArray();
                }
            }
            else
            {
                if (length > int.MaxValue) throw new IOException("HTTP response is too large");
                response.Body = new byte[(int)length];
                ReadFully(stream, response.Body, 0, response.Body.Length);
            }
            return response;
        }

        private static byte[] ReadUntilHeaderEnd(Stream stream)
        {
            const int maxHeader = 16 * 1024;
            byte[] buffer = new byte[maxHeader];
            int length = 0;
            while (length < maxHeader)
            {
                int b = stream.ReadByte();
                if (b < 0) return null;
                buffer[length++] = (byte)b;
                if (length >= 4 && buffer[length - 4] == 13 && buffer[length - 3] == 10
                    && buffer[length - 2] == 13 && buffer[length - 1] == 10)
                {
                    byte[] result = new byte[length - 4];
                    Buffer.BlockCopy(buffer, 0, result, 0, result.Length);
                    return result;
                }
            }
            throw new IOException("HTTP response header too large");
        }

        private static void ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = stream.Read(buffer, offset, count);
                if (n <= 0) throw new EndOfStreamException("HTTP response body ended early");
                offset += n;
                count -= n;
            }
        }

        private static void CopyToEnd(Stream stream, MemoryStream destination)
        {
            byte[] buffer = new byte[16 * 1024];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                destination.Write(buffer, 0, n);
        }

        private static string[] SplitCrLf(string text)
        {
            List<string> lines = new List<string>();
            int start = 0;
            for (int i = 0; i + 1 < text.Length; i++)
            {
                if (text[i] == '\r' && text[i + 1] == '\n')
                {
                    lines.Add(text.Substring(start, i - start));
                    i++;
                    start = i + 1;
                }
            }
            if (start <= text.Length) lines.Add(text.Substring(start));
            return lines.ToArray();
        }
    }

    internal sealed class SimpleHttpResponse
    {
        public int StatusCode;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string BodyText
        {
            get { return Encoding.UTF8.GetString(Body ?? new byte[0], 0, Body == null ? 0 : Body.Length); }
        }
    }
}
