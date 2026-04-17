using System;
using System.Collections.Generic;
using System.Net;
using Localsend.Backend.Protocol;

namespace Localsend.Backend.Sender
{
    /// <summary>一个已知的对端。按 fingerprint 去重。</summary>
    public sealed class Peer
    {
        public string Fingerprint;
        public string Alias;
        public string DeviceModel;
        public string DeviceType;
        public IPAddress Address;
        public int Port;
        public string Protocol;     // "http" 或 "https"
        public string Version;      // "1.0" / "2.0" / null
        public DateTime LastSeenUtc;

        public string BaseUrl
        {
            get
            {
                string scheme = (Protocol == "https") ? "https" : "http";
                int p = Port > 0 ? Port : Constants.RestPort;
                return scheme + "://" + Address + ":" + p;
            }
        }
    }

    /// <summary>线程安全的对端注册表。</summary>
    public sealed class PeerRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Peer> _peers = new Dictionary<string, Peer>();

        public event EventHandler PeerListChanged;

        public List<Peer> Snapshot()
        {
            lock (_lock) { return new List<Peer>(_peers.Values); }
        }

        internal void Upsert(DeviceInfo info, IPAddress addr)
        {
            if (info == null || string.IsNullOrEmpty(info.Fingerprint)) return;
            bool changed;
            lock (_lock)
            {
                Peer p;
                if (!_peers.TryGetValue(info.Fingerprint, out p))
                {
                    p = new Peer();
                    p.Fingerprint = info.Fingerprint;
                    _peers[info.Fingerprint] = p;
                    changed = true;
                }
                else changed = false;

                p.Alias = info.Alias;
                p.DeviceModel = info.DeviceModel;
                p.DeviceType = info.DeviceType;
                p.Address = addr;
                p.Port = info.Port > 0 ? info.Port : Constants.RestPort;
                p.Protocol = string.IsNullOrEmpty(info.Protocol) ? "http" : info.Protocol;
                p.Version = info.Version;
                p.LastSeenUtc = DateTime.UtcNow;
            }
            if (changed && PeerListChanged != null)
                try { PeerListChanged(this, EventArgs.Empty); } catch { }
        }

        /// <summary>剔除长时间未见的对端。</summary>
        public void ExpireOlderThan(TimeSpan maxAge)
        {
            DateTime cutoff = DateTime.UtcNow - maxAge;
            bool changed = false;
            lock (_lock)
            {
                List<string> drop = new List<string>();
                foreach (KeyValuePair<string, Peer> kv in _peers)
                    if (kv.Value.LastSeenUtc < cutoff) drop.Add(kv.Key);
                for (int i = 0; i < drop.Count; i++) { _peers.Remove(drop[i]); changed = true; }
            }
            if (changed && PeerListChanged != null)
                try { PeerListChanged(this, EventArgs.Empty); } catch { }
        }
    }
}
