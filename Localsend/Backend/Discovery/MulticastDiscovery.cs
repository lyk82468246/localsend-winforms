using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Localsend.Backend.Protocol;
using Localsend.Backend.Util;

namespace Localsend.Backend.Discovery
{
    /// <summary>对端被发现时的事件参数。</summary>
    internal sealed class PeerDiscoveredEventArgs : EventArgs
    {
        public DeviceInfo Peer;
        public IPAddress Address;
    }

    /// <summary>
    /// UDP 多播发现：周期性 announce；收到他人 announce 时回复一条非 announcement 响应。
    ///
    /// 设计：
    /// - 单 socket 绑定在 *:53317 上接收；
    /// - 每次发 announce 前重新枚举本机 IPv4（新增/消失的接口自动加入/退出组），
    ///   然后遍历所有已加入的接口，每个接口各发一份（通过切换 IP_MULTICAST_IF）。
    /// - 不做"主接口固定"，DHCP 换地址、拔插网卡、切 Wi-Fi 都能自收敛。
    /// </summary>
    internal sealed class MulticastDiscovery : IDisposable
    {
        private readonly DeviceInfo _self;
        private readonly IPAddress _group;
        private readonly int _port;
        private UdpClient _client;
        private Thread _rxThread;
        private Timer _announceTimer;
        private volatile bool _running;

        private readonly object _sendLock = new object();
        private readonly List<IPAddress> _joinedIfaces = new List<IPAddress>();

        public event EventHandler<PeerDiscoveredEventArgs> PeerDiscovered;

        public MulticastDiscovery(DeviceInfo self)
        {
            _self = self;
            _group = IPAddress.Parse(Constants.MulticastGroup);
            _port = Constants.MulticastPort;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            try
            {
                _client = new UdpClient(_port);
                Log.Info("UDP bound on *:" + _port);
            }
            catch (Exception ex)
            {
                Log.Warn("Discovery UDP bind failed: " + ex.Message);
                _running = false;
                return;
            }

            try
            {
                _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                Log.Info("Multicast TTL=4 set");
            }
            catch (Exception ex) { Log.Warn("Set MulticastTimeToLive failed: " + ex.Message); }

            // WM6 SChannel 不支持 MulticastLoopback 选项（WSAENOPROTOOPT），
            // 但 loopback 默认开启，这里静默尝试即可。
            try { _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true); }
            catch { }

            // 首次同步接口列表（加入组）
            RefreshInterfaces();

            _rxThread = new Thread(RxLoop);
            _rxThread.IsBackground = true;
            _rxThread.Name = "LS-Discovery-Rx";
            _rxThread.Start();

            try
            {
                SendAnnounce(true);
                _announceTimer = new Timer(delegate { SendAnnounce(true); }, null, 5000, 5000);
            }
            catch (Exception ex) { Log.Warn("Announce init failed: " + ex.Message); }

            Log.Info("Discovery started on " + _group + ":" + _port
                + " self.fp=" + _self.Fingerprint + " self.alias=" + _self.Alias);
        }

        public void Stop()
        {
            _running = false;
            if (_announceTimer != null) { _announceTimer.Dispose(); _announceTimer = null; }
            if (_client != null)
            {
                lock (_sendLock)
                {
                    for (int i = 0; i < _joinedIfaces.Count; i++)
                    {
                        try
                        {
                            _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership,
                                new MulticastOption(_group, _joinedIfaces[i]));
                        }
                        catch { }
                    }
                    _joinedIfaces.Clear();
                }
                try { _client.Close(); } catch { }
                _client = null;
            }
        }

        public void Dispose() { Stop(); }

        /// <summary>
        /// 重新枚举本机可用 IPv4 地址，同步多播组成员关系：
        /// 新出现的接口 AddMembership，已消失的接口 DropMembership。
        /// </summary>
        private void RefreshInterfaces()
        {
            if (_client == null) return;

            List<IPAddress> current = GetLocalIPv4Addresses();
            List<IPAddress> usable = new List<IPAddress>();
            for (int i = 0; i < current.Count; i++)
            {
                IPAddress a = current[i];
                if (IsLoopback(a) || IsLinkLocal(a)) continue;
                usable.Add(a);
            }

            lock (_sendLock)
            {
                // 退出已消失的接口
                List<IPAddress> toDrop = new List<IPAddress>();
                for (int i = 0; i < _joinedIfaces.Count; i++)
                    if (!ContainsAddr(usable, _joinedIfaces[i])) toDrop.Add(_joinedIfaces[i]);

                for (int i = 0; i < toDrop.Count; i++)
                {
                    IPAddress a = toDrop[i];
                    try
                    {
                        _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership,
                            new MulticastOption(_group, a));
                        Log.Info("Dropped multicast on iface " + a);
                    }
                    catch (Exception ex) { Log.Warn("Drop on " + a + " failed: " + ex.Message); }
                    RemoveAddr(_joinedIfaces, a);
                }

                // 加入新出现的接口
                for (int i = 0; i < usable.Count; i++)
                {
                    IPAddress a = usable[i];
                    if (ContainsAddr(_joinedIfaces, a)) continue;
                    try
                    {
                        _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                            new MulticastOption(_group, a));
                        _joinedIfaces.Add(a);
                        Log.Info("Joined multicast " + _group + " on iface " + a);
                    }
                    catch (Exception ex) { Log.Warn("Join on " + a + " failed: " + ex.Message); }
                }
            }
        }

        private void RxLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            Log.Info("Discovery RX loop started");
            while (_running)
            {
                byte[] data;
                try { data = _client.Receive(ref remote); }
                catch (SocketException ex)
                {
                    if (!_running) break;
                    Log.Warn("UDP Receive SocketException: " + ex.ErrorCode + " " + ex.Message);
                    continue;
                }
                catch (ObjectDisposedException) { break; }

                string preview = SafePreview(data);
                Log.Info("RX " + data.Length + "B from " + remote + " : " + preview);

                try { HandlePacket(data, remote); }
                catch (Exception ex) { Log.Warn("Discovery packet parse failed: " + ex.Message + " raw=" + preview); }
            }
            Log.Info("Discovery RX loop exited");
        }

        private void HandlePacket(byte[] data, IPEndPoint from)
        {
            string text = Encoding.UTF8.GetString(data, 0, data.Length);
            AnnounceMessage msg;
            try { msg = AnnounceMessage.FromJson(Json.ParseObject(text)); }
            catch (Exception ex) { Log.Warn("JSON parse failed from " + from + ": " + ex.Message); return; }

            if (msg.Info == null || string.IsNullOrEmpty(msg.Info.Fingerprint))
            {
                Log.Warn("Packet from " + from + " missing fingerprint; dropped");
                return;
            }
            if (msg.Info.Fingerprint == _self.Fingerprint)
            {
                Log.Info("Ignored self-announce from " + from);
                return;
            }

            Log.Info("Peer announce: fp=" + msg.Info.Fingerprint
                + " alias=" + msg.Info.Alias
                + " proto=" + msg.Info.Protocol
                + " port=" + msg.Info.Port
                + " ver=" + msg.Info.Version
                + " announcement=" + msg.Announcement
                + " src=" + from);

            EventHandler<PeerDiscoveredEventArgs> h = PeerDiscovered;
            if (h != null)
            {
                PeerDiscoveredEventArgs e = new PeerDiscoveredEventArgs();
                e.Peer = msg.Info;
                e.Address = from.Address;
                try { h(this, e); } catch (Exception ex) { Log.Warn("PeerDiscovered handler threw: " + ex.Message); }
            }

            // 协议：收到 announcement=true 时回复一条 announcement=false
            if (msg.Announcement)
            {
                AnnounceMessage reply = new AnnounceMessage();
                reply.Info = _self;
                reply.Announcement = false;
                byte[] payload = Encoding.UTF8.GetBytes(Json.Stringify(reply.ToJson()));
                // 单播回复：OS 路由即可，不需要切换 MulticastInterface
                try
                {
                    int n;
                    lock (_sendLock) n = _client.Send(payload, payload.Length, new IPEndPoint(from.Address, _port));
                    Log.Info("TX reply unicast " + n + "B to " + from.Address + ":" + _port);
                }
                catch (Exception ex) { Log.Warn("Announce reply (unicast) failed: " + ex.Message); }
                // 多播回复在每个接口上各发一份
                SendMulticastOnAllInterfaces(payload, "reply");
            }
        }

        private void SendAnnounce(bool announcement)
        {
            if (_client == null) return;

            // 每次发送前重新同步接口（DHCP/网卡变化自收敛）
            try { RefreshInterfaces(); } catch (Exception ex) { Log.Warn("RefreshInterfaces threw: " + ex.Message); }

            AnnounceMessage m = new AnnounceMessage();
            m.Info = _self;
            m.Announcement = announcement;
            string json = Json.Stringify(m.ToJson());
            byte[] payload = Encoding.UTF8.GetBytes(json);

            SendMulticastOnAllInterfaces(payload, "announce(" + announcement + ")");
        }

        /// <summary>
        /// 对每个已加入的接口切换 IP_MULTICAST_IF 后发送一次；没有任何接口时回退到默认接口一次。
        /// </summary>
        private void SendMulticastOnAllInterfaces(byte[] payload, string tag)
        {
            if (_client == null) return;
            IPEndPoint dst = new IPEndPoint(_group, _port);

            lock (_sendLock)
            {
                if (_joinedIfaces.Count == 0)
                {
                    try
                    {
                        int n = _client.Send(payload, payload.Length, dst);
                        Log.Info("TX " + tag + " (default iface) " + n + "B to " + _group + ":" + _port);
                    }
                    catch (Exception ex) { Log.Warn("TX " + tag + " (default) failed: " + ex.Message); }
                    return;
                }

                for (int i = 0; i < _joinedIfaces.Count; i++)
                {
                    IPAddress iface = _joinedIfaces[i];
                    try
                    {
                        _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                            iface.GetAddressBytes());
                        int n = _client.Send(payload, payload.Length, dst);
                        Log.Info("TX " + tag + " via " + iface + " " + n + "B");
                    }
                    catch (Exception ex) { Log.Warn("TX " + tag + " via " + iface + " failed: " + ex.Message); }
                }
            }
        }

        /// <summary>
        /// 绕开多播，直接向目标 IP 发一条 announcement 单播。用于手动探测（AP 隔离、路由器多播策略等）。
        /// </summary>
        public void SendUnicastAnnounce(IPAddress target)
        {
            if (_client == null || target == null) return;
            AnnounceMessage m = new AnnounceMessage();
            m.Info = _self;
            m.Announcement = true;
            byte[] payload = Encoding.UTF8.GetBytes(Json.Stringify(m.ToJson()));
            try
            {
                int n;
                lock (_sendLock) n = _client.Send(payload, payload.Length, new IPEndPoint(target, _port));
                Log.Info("TX unicast probe " + n + "B to " + target + ":" + _port);
            }
            catch (Exception ex) { Log.Warn("Unicast probe send failed: " + ex.Message); }
        }

        // ---- helpers ----

        private static List<IPAddress> GetLocalIPv4Addresses()
        {
            List<IPAddress> r = new List<IPAddress>();
            try
            {
                IPHostEntry he = Dns.GetHostEntry(Dns.GetHostName());
                if (he != null && he.AddressList != null)
                {
                    for (int i = 0; i < he.AddressList.Length; i++)
                    {
                        IPAddress a = he.AddressList[i];
                        if (a != null && a.AddressFamily == AddressFamily.InterNetwork) r.Add(a);
                    }
                }
            }
            catch (Exception ex) { Log.Warn("GetLocalIPv4Addresses failed: " + ex.Message); }
            return r;
        }

        private static bool ContainsAddr(List<IPAddress> list, IPAddress a)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].Equals(a)) return true;
            return false;
        }

        private static void RemoveAddr(List<IPAddress> list, IPAddress a)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].Equals(a)) { list.RemoveAt(i); return; }
        }

        private static bool IsLoopback(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return b.Length == 4 && b[0] == 127;
        }

        private static bool IsLinkLocal(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return b.Length == 4 && b[0] == 169 && b[1] == 254;
        }

        private static string SafePreview(byte[] data)
        {
            int n = data.Length > 240 ? 240 : data.Length;
            try
            {
                string s = Encoding.UTF8.GetString(data, 0, n);
                StringBuilder sb = new StringBuilder(s.Length);
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
                    else if (c < 0x20) sb.Append('?');
                    else sb.Append(c);
                }
                if (data.Length > n) sb.Append("...");
                return sb.ToString();
            }
            catch { return "<non-utf8 " + data.Length + "B>"; }
        }
    }
}
