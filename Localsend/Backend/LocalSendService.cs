using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Localsend.Backend.Discovery;
using Localsend.Backend.Http;
using Localsend.Backend.Protocol;
using Localsend.Backend.Receiver;
using Localsend.Backend.Sender;
using Localsend.Backend.Util;

namespace Localsend.Backend
{
    /// <summary>后端复合根：discovery + http server + session + v1 handler + peer registry + sender。</summary>
    public sealed class LocalSendService : IDisposable
    {
        private readonly DeviceInfo _self;
        private readonly MulticastDiscovery _discovery;
        private readonly HttpServer _http;
        private readonly SessionManager _sessions;
        private readonly V1ApiHandler _v1;
        private Timer _sessionTick;
        private Timer _peerExpireTick;

        public PeerRegistry Peers { get; private set; }
        public OutboundSender Sender { get; private set; }

        public string Alias { get { return _self.Alias; } }
        public string Fingerprint { get { return _self.Fingerprint; } }
        public string DownloadDir { get; private set; }

        public LocalSendService(string alias, string downloadDir, IReceivePolicy policy)
            : this(alias, downloadDir, policy, null) { }

        public LocalSendService(string alias, string downloadDir, IReceivePolicy policy, string fingerprint)
        {
            _self = new DeviceInfo();
            _self.Alias = string.IsNullOrEmpty(alias) ? Constants.DefaultAlias : alias;
            _self.DeviceModel = Constants.DeviceModel;
            _self.DeviceType = Constants.DeviceType;
            _self.Fingerprint = string.IsNullOrEmpty(fingerprint) ? IdGen.NewRandom() : fingerprint;
            _self.Version = Constants.ProtocolVersion;
            _self.Port = Constants.RestPort;
            _self.Protocol = Constants.ProtocolScheme;
            _self.Download = false;

            DownloadDir = downloadDir;

            _sessions = new SessionManager();
            _http = new HttpServer(Constants.RestPort);
            _v1 = new V1ApiHandler(_self, _sessions, policy, downloadDir);
            _v1.Register(_http);

            Peers = new PeerRegistry();
            Sender = new OutboundSender(_self);

            _discovery = new MulticastDiscovery(_self);
            _discovery.PeerDiscovered += delegate(object s, PeerDiscoveredEventArgs e)
            {
                Peers.Upsert(e.Peer, e.Address);
            };
        }

        public void Start()
        {
            try { _http.Start(); }
            catch (Exception ex) { Log.Error("HTTP start failed", ex); throw; }

            try { _discovery.Start(); }
            catch (Exception ex) { Log.Warn("Discovery start failed (continuing): " + ex.Message); }

            _sessionTick = new Timer(delegate { _sessions.Tick(); }, null, 30000, 30000);
            _peerExpireTick = new Timer(delegate { Peers.ExpireOlderThan(TimeSpan.FromMinutes(2)); }, null, 30000, 30000);
            Log.Info("LocalSendService started: alias=" + _self.Alias + " fp=" + _self.Fingerprint);
        }

        public void Stop()
        {
            if (_sessionTick != null) { _sessionTick.Dispose(); _sessionTick = null; }
            if (_peerExpireTick != null) { _peerExpireTick.Dispose(); _peerExpireTick = null; }
            _discovery.Stop();
            _http.Stop();
        }

        public void Dispose() { Stop(); }

        /// <summary>
        /// 手动探测目标 IP:port：
        /// 1) 裸 TCP connect（验证 L3/L4 可达，剥离 HTTP 层）
        /// 2) 若 port == RestPort：发一条单播 UDP announce + HTTP GET /api/localsend/v1/info
        /// </summary>
        public void Probe(IPAddress target, int port)
        {
            if (target == null) return;
            Log.Info("Probe start -> " + target + ":" + port);

            ThreadPool.QueueUserWorkItem(delegate { ProbeTcp(target, port); });

            if (port == Constants.RestPort)
            {
                try { _discovery.SendUnicastAnnounce(target); }
                catch (Exception ex) { Log.Warn("Probe unicast announce threw: " + ex.Message); }
                ThreadPool.QueueUserWorkItem(delegate { ProbeHttp(target, port); });
            }
        }

        /// <summary>兼容旧调用点：默认用 LocalSend 端口。</summary>
        public void Probe(IPAddress target) { Probe(target, Constants.RestPort); }

        private static void ProbeTcp(IPAddress target, int port)
        {
            string tag = target + ":" + port;
            System.Net.Sockets.Socket s = null;
            try
            {
                s = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                IAsyncResult ar = s.BeginConnect(new IPEndPoint(target, port), null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(3000, false);
                if (!ok) { Log.Warn("Probe TCP " + tag + " timeout"); try { s.Close(); } catch { } return; }
                s.EndConnect(ar);
                Log.Info("Probe TCP " + tag + " CONNECTED");
            }
            catch (Exception ex) { Log.Warn("Probe TCP " + tag + " failed: " + ex.Message); }
            finally { if (s != null) try { s.Close(); } catch { } }
        }

        private void ProbeHttp(IPAddress target, int port)
        {
            string url = "http://" + target + ":" + port + "/api/localsend/v1/info";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 5000;
                req.KeepAlive = false;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                {
                    string body = sr.ReadToEnd();
                    if (body != null && body.Length > 400) body = body.Substring(0, 400) + "...";
                    Log.Info("Probe HTTP " + url + " -> " + (int)resp.StatusCode + " body=" + body);
                }
            }
            catch (WebException wex)
            {
                string status = wex.Status.ToString();
                string http = "";
                if (wex.Response is HttpWebResponse)
                    http = " http=" + (int)((HttpWebResponse)wex.Response).StatusCode;
                Log.Warn("Probe HTTP " + url + " WebException status=" + status + http + " msg=" + wex.Message);
            }
            catch (Exception ex) { Log.Warn("Probe HTTP " + url + " failed: " + ex.Message); }
        }
    }
}
