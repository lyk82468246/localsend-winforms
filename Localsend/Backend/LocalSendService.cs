using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Collections.Generic;
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
        private readonly bool _encryptionEnabled;
        private readonly EncryptionCapabilityReport _encryption;
        private readonly object _registerLock = new object();
        private readonly System.Collections.Generic.Dictionary<string, DateTime> _lastRegisterUtc
            = new System.Collections.Generic.Dictionary<string, DateTime>();
        private Timer _sessionTick;
        private Timer _peerExpireTick;
        private Timer _discoveryFallbackTimer;
        private volatile bool _discoveryConfirmed;
        private volatile bool _subnetScanStarted;
        private volatile bool _stopped;
        private int _registerInFlight;

        public PeerRegistry Peers { get; private set; }
        public OutboundSender Sender { get; private set; }

        public string Alias { get { return _self.Alias; } }
        public string Fingerprint { get { return _self.Fingerprint; } }
        public string Protocol { get { return _self.Protocol; } }
        public EncryptionCapabilityReport Encryption { get { return _encryption; } }
        public bool CanSendEncrypted { get { return _fullEncryption; } }
        public bool EncryptionEnabled { get { return _encryptionEnabled; } }
        public bool CanToggleEncryption
        {
            get
            {
                return _tls != null
                    && _tls.SupportsHttpsTransport
                    && _encryption.Level == EncryptionCapabilityLevel.Full;
            }
        }
        public string DownloadDir { get; private set; }

        public LocalSendService(string alias, string downloadDir, IReceivePolicy policy)
            : this(alias, downloadDir, policy, null, null) { }

        public LocalSendService(string alias, string downloadDir, IReceivePolicy policy, string fingerprint)
            : this(alias, downloadDir, policy, fingerprint, null) { }

        public LocalSendService(string alias, string downloadDir, IReceivePolicy policy,
            string fingerprint, TlsProviderRouter tls)
            : this(alias, downloadDir, policy, fingerprint, tls, true) { }

        /// <summary>
        /// Creates the service with the user's preferred encryption mode.
        /// A full provider honors the preference.  A receive-only provider
        /// stays on HTTPS so it can still accept official encrypted sends,
        /// but it does not claim outbound encrypted capability.
        /// </summary>
        public LocalSendService(string alias, string downloadDir, IReceivePolicy policy,
            string fingerprint, TlsProviderRouter tls, bool encryptionEnabled)
        {
            _tls = tls;
            _encryption = tls != null
                ? tls.Report
                : EncryptionCapabilityReport.NotProbed();
            bool providerReady = tls != null && tls.SupportsHttpsTransport
                && tls.Report.Level != EncryptionCapabilityLevel.Unavailable;
            bool fullProvider = providerReady
                && tls.Report.Level == EncryptionCapabilityLevel.Full;
            _httpsEnabled = fullProvider ? encryptionEnabled : providerReady;
            _encryptionEnabled = _httpsEnabled;
            _fullEncryption = fullProvider && encryptionEnabled;

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
                && !tls.SupportsHttpsTransport)
                Log.Warn("TLS provider reported " + tls.Report.Level
                    + " but has no HTTP transport adapter; staying on HTTP");
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
            _stopped = false;
            try { _http.Start(); }
            catch (Exception ex) { Log.Error("HTTP start failed", ex); throw; }

            try { _discovery.Start(); }
            catch (Exception ex) { Log.Warn("Discovery start failed (continuing): " + ex.Message); }

            _sessionTick = new Timer(delegate { _sessions.Tick(); }, null, 30000, 30000);
            _peerExpireTick = new Timer(delegate { Peers.ExpireOlderThan(TimeSpan.FromMinutes(2)); }, null, 30000, 30000);
            // Multicast is only a hint.  Give the initial announce/response
            // burst a moment, then use the official-style /24 HTTP fallback
            // if no peer has completed a register exchange.
            try
            {
                _discoveryFallbackTimer = new Timer(delegate { RunSubnetFallbackScan(); },
                    null, 3500, 5000);
            }
            catch (Exception ex) { Log.Warn("Discovery fallback timer init failed: " + ex.Message); }
            Log.Info("LocalSendService started: alias=" + _self.Alias + " fp=" + _self.Fingerprint
                + " protocol=" + _self.Protocol + " tls=" + _encryption.Level);
        }

        public void Stop()
        {
            _stopped = true;
            if (_sessionTick != null) { _sessionTick.Dispose(); _sessionTick = null; }
            if (_peerExpireTick != null) { _peerExpireTick.Dispose(); _peerExpireTick = null; }
            if (_discoveryFallbackTimer != null)
            {
                try { _discoveryFallbackTimer.Dispose(); } catch { }
                _discoveryFallbackTimer = null;
            }
            _discovery.Stop();
            _http.Stop();
        }

        public void Dispose() { Stop(); }

        /// <summary>
        /// 手动探测目标 IP:port。默认端口直接走协议请求：单播
        /// announce + HTTP(S) v2 register。不能先做裸 TCP connect，
        /// 因为 Positron 的 accept 在返回前就完成 TLS 握手，裸 connect
        /// 会在服务端制造 EOF/WSAECONNRESET，并与真正的 TLS 请求竞争。
        /// 非标准端口仍保留 TCP 连通性诊断。
        /// </summary>
        public void Probe(IPAddress target, int port)
        {
            if (target == null) return;
            Log.Info("Probe start -> " + target + ":" + port);

            if (port == Constants.RestPort)
            {
                try { _discovery.SendUnicastAnnounce(target); }
                catch (Exception ex) { Log.Warn("Probe unicast announce threw: " + ex.Message); }
                ThreadPool.QueueUserWorkItem(delegate { ProbeHttp(target, port); });
            }
            else
                ThreadPool.QueueUserWorkItem(delegate { ProbeTcp(target, port); });
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
            ProbeHttp(target, port, false);
        }

        private void ProbeHttp(IPAddress target, int port, bool quiet)
        {
            string scheme = _httpsEnabled ? "https" : "http";
            string url = scheme + "://" + target + ":" + port + Constants.ApiV2 + "/register";
            try
            {
                SimpleHttpClient http = new SimpleHttpClient(
                    _httpsEnabled ? _tls.Provider : null,
                    quiet ? 800 : 5000, quiet ? 2500 : 15000);
                SimpleHttpResponse resp = http.PostJson(url, Json.Stringify(_self.ToJson(true)), "");
                // A v1 peer may not expose the v2 route.  Keep the staged
                // scan useful for older official clients without changing the
                // v2-first behavior for current peers.
                if (resp.StatusCode == 404 || resp.StatusCode == 405 || resp.StatusCode == 501)
                {
                    url = scheme + "://" + target + ":" + port + Constants.ApiV1 + "/register";
                    resp = http.PostJson(url, Json.Stringify(_self.ToJson(false)), "");
                }
                if (resp.StatusCode >= 200 && resp.StatusCode < 300)
                {
                    if (_httpsEnabled && string.IsNullOrEmpty(resp.PeerFingerprint))
                    {
                        if (!quiet) Log.Warn("Probe HTTPS register returned no peer certificate fingerprint: " + url);
                        return;
                    }
                    try
                    {
                        DeviceInfo info = DeviceInfo.FromJson(Json.ParseObject(resp.BodyText));
                        if (_httpsEnabled && !string.IsNullOrEmpty(resp.PeerFingerprint))
                            info.Fingerprint = resp.PeerFingerprint;
                        if (info != null && string.Equals(info.Fingerprint, _self.Fingerprint,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            if (!quiet) Log.Info("Probe register reached this device; ignored self");
                        }
                        else
                        {
                            Peers.Upsert(info, target);
                            _discoveryConfirmed = true;
                        }
                    }
                    catch (Exception parseEx)
                    { if (!quiet) Log.Warn("Probe register response parse failed: " + parseEx.Message); }
                }
                if (!quiet)
                {
                    string body = resp.BodyText;
                    if (body != null && body.Length > 400) body = body.Substring(0, 400) + "...";
                    Log.Info("Probe HTTP register " + url + " -> " + resp.StatusCode + " body=" + body);
                }
                else if (resp.StatusCode >= 200 && resp.StatusCode < 300)
                    Log.Info("Discovery fallback register succeeded: " + target);
            }
            catch (Exception ex) { if (!quiet) Log.Warn("Probe HTTP " + url + " failed: " + ex.Message); }
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
            Interlocked.Increment(ref _registerInFlight);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { RegisterPeer(peer, address); }
                finally { Interlocked.Decrement(ref _registerInFlight); }
            });
        }

        private void RegisterPeer(DeviceInfo peer, IPAddress address)
        {
            bool secure = string.Equals(peer.Protocol, "https", StringComparison.OrdinalIgnoreCase);
            if (secure && (!_fullEncryption || _tls == null || !_tls.SupportsHttpsTransport))
            {
                Log.Info("Discovery register skipped for HTTPS peer " + peer.Alias
                    + ": local runtime cannot send full TLS");
                return;
            }

            int port = peer.Port > 0 ? peer.Port : Constants.RestPort;
            string scheme = secure ? "https" : "http";
            bool legacy = peer.Version != null && peer.Version.StartsWith("1.");
            string api = legacy ? Constants.ApiV1 : Constants.ApiV2;
            string url = scheme + "://" + address + ":" + port + api + "/register";
            try
            {
                SimpleHttpClient http = new SimpleHttpClient(
                    secure ? _tls.Provider : null, 5000, 15000);
                // UDP discovery metadata is not authenticated.  The first
                // HTTPS register therefore uses TOFU: complete TLS without a
                // pin and learn the peer identity from the certificate on the
                // response.  A known fingerprint is enforced by the normal
                // file-transfer path after registration.
                string expected = "";
                SimpleHttpResponse resp = http.PostJson(
                    url, Json.Stringify(_self.ToJson(!legacy)), expected);
                if (resp.StatusCode >= 200 && resp.StatusCode < 300)
                {
                    if (secure && string.IsNullOrEmpty(resp.PeerFingerprint))
                    {
                        Log.Warn("Discovery HTTPS register returned no peer certificate fingerprint: " + url);
                        return;
                    }
                    DeviceInfo reply = DeviceInfo.FromJson(Json.ParseObject(resp.BodyText));
                    if (secure && !string.IsNullOrEmpty(resp.PeerFingerprint))
                        reply.Fingerprint = resp.PeerFingerprint;
                    if (reply != null && !string.IsNullOrEmpty(reply.Fingerprint)
                        && !string.Equals(reply.Fingerprint, _self.Fingerprint,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        Peers.Upsert(reply, address);
                        _discoveryConfirmed = true;
                    }
                    Log.Info("Discovery register succeeded: " + peer.Alias + " -> " + resp.StatusCode);
                }
                else
                    Log.Warn("Discovery register " + url + " -> HTTP " + resp.StatusCode);
            }
            catch (Exception ex)
            { Log.Warn("Discovery register " + url + " failed: " + ex.Message); }
        }

        /// <summary>
        /// HTTP fallback for networks where multicast is filtered or the
        /// Windows Mobile UDP stack only delivers packets to the sender's own
        /// interface.  This mirrors LocalSend's staged discovery: enumerate
        /// local IPv4 interfaces, scan each /24, and POST /register with the
        /// current transport.  HTTPS scans use TOFU exactly like announce
        /// driven registration; the TLS certificate fingerprint is learned
        /// from the response and becomes the peer key.
        /// </summary>
        private void RunSubnetFallbackScan()
        {
            // Let an announce-driven register finish before competing for the
            // same Positron native endpoint.  The timer remains periodic, so
            // a failed register will still fall back to the subnet scan.
            if (_stopped || _discoveryConfirmed || _subnetScanStarted
                || _registerInFlight > 0) return;
            if (_httpsEnabled && !_fullEncryption)
            {
                Log.Info("Discovery fallback HTTP scan skipped: local TLS runtime is receive-only");
                _subnetScanStarted = true;
                Timer receiveOnlyTimer = _discoveryFallbackTimer;
                _discoveryFallbackTimer = null;
                if (receiveOnlyTimer != null) try { receiveOnlyTimer.Dispose(); } catch { }
                return;
            }

            List<IPAddress> local = MulticastDiscovery.GetLocalIPv4AddressesSnapshot();
            List<IPAddress> targets = new List<IPAddress>();
            for (int i = 0; i < local.Count; i++)
            {
                IPAddress iface = local[i];
                if (iface == null || iface.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                    || IsLoopback(iface) || IsLinkLocal(iface)) continue;
                byte[] b = iface.GetAddressBytes();
                if (b.Length != 4) continue;
                for (int host = 1; host <= 254; host++)
                {
                    if (host == b[3]) continue;
                    IPAddress target = new IPAddress(new byte[] { b[0], b[1], b[2], (byte)host });
                    if (ContainsAddress(local, target)) continue;
                    if (!ContainsAddress(targets, target)) targets.Add(target);
                }
            }

            if (targets.Count == 0)
            {
                Log.Info("Discovery fallback HTTP scan skipped: no usable IPv4 interface");
                return;
            }

            _subnetScanStarted = true;
            Timer scanTimer = _discoveryFallbackTimer;
            _discoveryFallbackTimer = null;
            if (scanTimer != null) try { scanTimer.Dispose(); } catch { }
            Log.Info("Discovery fallback HTTP scan started: targets=" + targets.Count
                + " scheme=" + (_httpsEnabled ? "https" : "http"));
            SubnetScanState state = new SubnetScanState();
            state.Targets = targets;
            // Positron serializes native endpoint operations behind its ABI
            // gate.  A single worker avoids six managed threads waiting on
            // one native lock on WM6; desktop transports can use a small
            // bounded fan-out.
            int workers = targets.Count < 6 ? targets.Count : 6;
            if (_httpsEnabled && _tls != null && _tls.Provider != null
                && string.Equals(_tls.Provider.Name, "positron", StringComparison.OrdinalIgnoreCase))
                workers = 1;
            for (int i = 0; i < workers; i++)
                ThreadPool.QueueUserWorkItem(delegate(object s) { SubnetScanWorker((SubnetScanState)s); }, state);
        }

        private void SubnetScanWorker(SubnetScanState state)
        {
            while (!_stopped && !_discoveryConfirmed)
            {
                IPAddress target = null;
                lock (state.Gate)
                {
                    if (state.NextIndex >= state.Targets.Count) return;
                    target = state.Targets[state.NextIndex++];
                }
                ProbeHttp(target, Constants.RestPort, true);
            }
        }

        private sealed class SubnetScanState
        {
            public readonly object Gate = new object();
            public List<IPAddress> Targets;
            public int NextIndex;
        }

        private static bool ContainsAddress(List<IPAddress> list, IPAddress value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Equals(value)) return true;
            return false;
        }

        private static bool IsLoopback(IPAddress value)
        {
            byte[] b = value.GetAddressBytes();
            return b.Length == 4 && b[0] == 127;
        }

        private static bool IsLinkLocal(IPAddress value)
        {
            byte[] b = value.GetAddressBytes();
            return b.Length == 4 && b[0] == 169 && b[1] == 254;
        }
    }
}
