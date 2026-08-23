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
using Localsend.Backend.Tls;
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
        private readonly TlsProviderRouter _tls;
        private readonly bool _httpsEnabled;
        private readonly bool _fullEncryption;
        private readonly EncryptionCapabilityReport _encryption;
        private readonly object _registerLock = new object();
        private readonly System.Collections.Generic.Dictionary<string, DateTime> _lastRegisterUtc
            = new System.Collections.Generic.Dictionary<string, DateTime>();
        private Timer _sessionTick;
        private Timer _peerExpireTick;

        public PeerRegistry Peers { get; private set; }
        public OutboundSender Sender { get; private set; }

        public string Alias { get { return _self.Alias; } }
        public string Fingerprint { get { return _self.Fingerprint; } }
        public string Protocol { get { return _self.Protocol; } }
        public EncryptionCapabilityReport Encryption { get { return _encryption; } }
        public bool CanSendEncrypted { get { return _fullEncryption; } }
        public string DownloadDir { get; private set; }

        public LocalSendService(string alias, string downloadDir, IReceivePolicy policy)
            : this(alias, downloadDir, policy, null, null) { }

        public LocalSendService(string alias, string downloadDir, IReceivePolicy policy, string fingerprint)
            : this(alias, downloadDir, policy, fingerprint, null) { }

        public LocalSendService(string alias, string downloadDir, IReceivePolicy policy,
            string fingerprint, TlsProviderRouter tls)
        {
            _tls = tls;
            _encryption = tls != null
                ? tls.Report
                : EncryptionCapabilityReport.NotProbed();
            bool providerReady = tls != null && tls.SupportsStreamTransport
                && tls.Report.Level != EncryptionCapabilityLevel.Unavailable;
            _httpsEnabled = providerReady;
            _fullEncryption = providerReady
                && tls.Report.Level == EncryptionCapabilityLevel.Full;

            _self = new DeviceInfo();
            _self.Alias = string.IsNullOrEmpty(alias) ? Constants.DefaultAlias : alias;
            bool isCe = false;
            if (tls != null && tls.RuntimeEnvironment != null)
                isCe = tls.RuntimeEnvironment.IsWindowsCe;
            else
            {
                try { isCe = Environment.OSVersion.Platform == PlatformID.WinCE; } catch { }
            }
            _self.DeviceModel = isCe ? Constants.DeviceModel : "Windows desktop";
            _self.DeviceType = isCe ? Constants.DeviceType : "desktop";
            string identityFingerprint = "";
            if (tls != null)
            {
                try { identityFingerprint = tls.IdentityFingerprint; } catch { }
            }
            _self.Fingerprint = string.IsNullOrEmpty(identityFingerprint)
                ? (string.IsNullOrEmpty(fingerprint) ? IdGen.NewRandom() : fingerprint)
                : identityFingerprint;
            _self.Version = Constants.ProtocolVersion;
            _self.Port = Constants.RestPort;
            _self.Protocol = _httpsEnabled ? "https" : Constants.ProtocolScheme;
            _self.Download = false;

            DownloadDir = downloadDir;

            Peers = new PeerRegistry();
            _sessions = new SessionManager();
            _http = new HttpServer(Constants.RestPort,
                _httpsEnabled ? tls.Provider : null, _httpsEnabled);
            if (tls != null && tls.Report.Level != EncryptionCapabilityLevel.Unavailable
                && !tls.SupportsStreamTransport)
                Log.Warn("TLS provider reported " + tls.Report.Level
                    + " but has no HTTP stream adapter; staying on HTTP");
            _v1 = new V1ApiHandler(_self, _sessions, policy, downloadDir, Peers);
            _v1.Register(_http);

            Sender = new OutboundSender(_self, tls, _fullEncryption);

            _discovery = new MulticastDiscovery(_self);
            _discovery.PeerDiscovered += delegate(object s, PeerDiscoveredEventArgs e)
            {
                Peers.Upsert(e.Peer, e.Address);
                if (e.Announce)
                    QueueRegister(e.Peer, e.Address);
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
            Log.Info("LocalSendService started: alias=" + _self.Alias + " fp=" + _self.Fingerprint
                + " protocol=" + _self.Protocol + " tls=" + _encryption.Level);
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
        /// 2) 若 port == RestPort：发一条单播 UDP announce + HTTP v2 register
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
            string scheme = _httpsEnabled ? "https" : "http";
            string url = scheme + "://" + target + ":" + port + Constants.ApiV2 + "/register";
            try
            {
                SimpleHttpClient http = new SimpleHttpClient(
                    _httpsEnabled ? _tls.Provider : null, 5000, 15000);
                SimpleHttpResponse resp = http.PostJson(url, Json.Stringify(_self.ToJson(true)), "");
                if (resp.StatusCode >= 200 && resp.StatusCode < 300)
                {
                    try
                    {
                        DeviceInfo info = DeviceInfo.FromJson(Json.ParseObject(resp.BodyText));
                        Peers.Upsert(info, target);
                    }
                    catch (Exception parseEx)
                    { Log.Warn("Probe register response parse failed: " + parseEx.Message); }
                }
                string body = resp.BodyText;
                if (body != null && body.Length > 400) body = body.Substring(0, 400) + "...";
                Log.Info("Probe HTTP register " + url + " -> " + resp.StatusCode + " body=" + body);
            }
            catch (Exception ex) { Log.Warn("Probe HTTP " + url + " failed: " + ex.Message); }
        }

        /// <summary>
        /// v2.2 discovery is two-way: an announce=true packet is followed by
        /// POST /api/localsend/v2/register to the sender's advertised scheme.
        /// The UDP response remains enabled as a fallback for peers that do
        /// not expose the register route or for a receive-only TLS runtime.
        /// </summary>
        private void QueueRegister(DeviceInfo peer, IPAddress address)
        {
            if (peer == null || address == null || string.IsNullOrEmpty(peer.Fingerprint)) return;
            string key = peer.Fingerprint + "@" + address;
            lock (_registerLock)
            {
                DateTime last;
                if (_lastRegisterUtc.TryGetValue(key, out last)
                    && (DateTime.UtcNow - last).TotalSeconds < 20) return;
                _lastRegisterUtc[key] = DateTime.UtcNow;
            }
            ThreadPool.QueueUserWorkItem(delegate { RegisterPeer(peer, address); });
        }

        private void RegisterPeer(DeviceInfo peer, IPAddress address)
        {
            bool secure = string.Equals(peer.Protocol, "https", StringComparison.OrdinalIgnoreCase);
            if (secure && (!_fullEncryption || _tls == null || !_tls.SupportsStreamTransport))
            {
                Log.Info("Discovery register skipped for HTTPS peer " + peer.Alias
                    + ": local runtime cannot send full TLS");
                return;
            }

            int port = peer.Port > 0 ? peer.Port : Constants.RestPort;
            string scheme = secure ? "https" : "http";
            string url = scheme + "://" + address + ":" + port + Constants.ApiV2 + "/register";
            try
            {
                SimpleHttpClient http = new SimpleHttpClient(
                    secure ? _tls.Provider : null, 5000, 15000);
                string expected = secure ? (peer.Fingerprint ?? "") : "";
                SimpleHttpResponse resp = http.PostJson(
                    url, Json.Stringify(_self.ToJson(true)), expected);
                if (resp.StatusCode >= 200 && resp.StatusCode < 300)
                {
                    DeviceInfo reply = DeviceInfo.FromJson(Json.ParseObject(resp.BodyText));
                    Peers.Upsert(reply, address);
                    Log.Info("Discovery register succeeded: " + peer.Alias + " -> " + resp.StatusCode);
                }
                else
                    Log.Warn("Discovery register " + url + " -> HTTP " + resp.StatusCode);
            }
            catch (Exception ex)
            { Log.Warn("Discovery register " + url + " failed: " + ex.Message); }
        }
    }
}
