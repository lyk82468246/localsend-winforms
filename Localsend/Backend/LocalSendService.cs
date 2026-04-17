using System;
using System.IO;
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
    }
}
