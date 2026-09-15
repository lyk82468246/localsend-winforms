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
        public bool Announce;
    }

    /// <summary>
    /// UDP 多播发现：周期性 announce；收到他人 announce 时通知服务层，
    /// 由服务层按 v2.2 规范向来源的 HTTP /register 端点注册，并保留 UDP 回复。
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
        private Timer _bindRetryTimer;
        private volatile bool _running;
        private bool _disposed;

        private readonly object _lifecycleLock = new object();
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
            lock (_lifecycleLock)
            {
                if (_disposed || _running) return;
                _running = true;
            }

            if (!TryBindWithRetry())
            {
                lock (_lifecycleLock) { _running = false; }
                Log.Warn("Discovery UDP bind failed; will retry in the background");
                ScheduleBindRetry();
                return;
            }
            StopBindRetryTimer();

            UdpClient client = _client;
            if (client == null)
            {
                lock (_lifecycleLock) { _running = false; }
                ScheduleBindRetry();
                return;
            }

            try
            {
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                Log.Info("Multicast TTL=4 set");
            }
            catch (Exception ex) { Log.Warn("Set MulticastTimeToLive failed: " + ex.Message); }

            // WM6 SChannel 不支持 MulticastLoopback 选项（WSAENOPROTOOPT），
            // 但 loopback 默认开启，这里静默尝试即可。
            try { client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true); }
            catch { }

            // 首次同步接口列表（加入组）。接口枚举在 WinCE 上可能因
            // DNS/网卡栈暂时不可用而失败；这不应阻止后面的重试定时器。
            try { RefreshInterfaces(); }
            catch (Exception ex) { Log.Warn("Initial RefreshInterfaces threw: " + ex.Message); }

            Thread rx = new Thread(RxLoop);
            rx.IsBackground = true;
            rx.Name = "LS-Discovery-Rx";
            lock (_lifecycleLock)
            {
                if (_disposed || !_running)
                {
                    try { client.Close(); } catch { }
                    return;
                }
                _rxThread = rx;
                try { rx.Start(); }
                catch
                {
                    _rxThread = null;
                    throw;
                }
            }

            try { SendAnnounce(true); }
            catch (Exception ex) { Log.Warn("Initial announce failed: " + ex.Message); }
            try
            {
                Timer timer = new Timer(delegate { SendAnnounce(true); }, null, 5000, 5000);
                lock (_lifecycleLock)
                {
                    if (_disposed || !_running)
                    {
                        try { timer.Dispose(); } catch { }
                    }
                    else _announceTimer = timer;
                }
            }
            catch (Exception ex) { Log.Warn("Announce timer init failed: " + ex.Message); }

            Log.Info("Discovery started on " + _group + ":" + _port
                + " self.fp=" + _self.Fingerprint + " self.alias=" + _self.Alias);
        }

        public void Stop()
        {
            Timer announceTimer;
            Timer bindRetryTimer;
            Thread rxThread;
            lock (_lifecycleLock)
            {
                // A discovery instance belongs to one LocalSendService
                // lifetime.  Mark it permanently stopped before cancelling
                // the retry timer so a callback already in flight cannot
                // resurrect the old socket after a service restart.
                _disposed = true;
                _running = false;
                announceTimer = _announceTimer;
                _announceTimer = null;
                bindRetryTimer = _bindRetryTimer;
                _bindRetryTimer = null;
                rxThread = _rxThread;
                _rxThread = null;
            }
            if (announceTimer != null) { try { announceTimer.Dispose(); } catch { } }
            if (bindRetryTimer != null) { try { bindRetryTimer.Dispose(); } catch { } }

            UdpClient client = null;
            lock (_sendLock)
            {
                client = _client;
                _client = null;
                if (client != null)
                {
                    for (int i = 0; i < _joinedIfaces.Count; i++)
                    {
                        try
                        {
                            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership,
                                new MulticastOption(_group, _joinedIfaces[i]));
                        }
                        catch { }
                    }
                }
                _joinedIfaces.Clear();
            }
            if (client != null)
            {
                try { client.Close(); } catch { }
            }

            if (rxThread != null && rxThread != Thread.CurrentThread)
            {
                try { rxThread.Join(2000); } catch { }
            }
        }

        public void Dispose()
        {
            lock (_lifecycleLock) { _disposed = true; }
            Stop();
        }

        private bool TryBindWithRetry()
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (!_running || _disposed) return false;
                UdpClient candidate = null;
                lock (_sendLock)
                {
                    if (_client != null) return true;
                }
                try
                {
                    candidate = new UdpClient();
                    try
                    {
                        candidate.Client.SetSocketOption(SocketOptionLevel.Socket,
                            SocketOptionName.ReuseAddress, 1);
                    }
                    catch (Exception ex) { Log.Warn("UDP ReuseAddress unavailable: " + ex.Message); }
                    try
                    {
                        candidate.Client.SetSocketOption(SocketOptionLevel.Socket,
                            SocketOptionName.Broadcast, 1);
                    }
                    catch { }
                    candidate.Client.Bind(new IPEndPoint(IPAddress.Any, _port));

                    lock (_sendLock)
                    {
                        if (!_running || _disposed)
                        {
                            try { candidate.Close(); } catch { }
                            return false;
                        }
                        _client = candidate;
                        candidate = null;
                    }
                    Log.Info("UDP bound on *:" + _port + " (attempt " + attempt + ")");
                    return true;
                }
                catch (Exception ex)
                {
                    if (candidate != null) try { candidate.Close(); } catch { }
                    Log.Warn("Discovery UDP bind attempt " + attempt + " failed: " + ex.Message);
                    if (attempt < 3)
                    {
                        try { Thread.Sleep(250); } catch { }
                    }
                }
            }
            return false;
        }

        private void ScheduleBindRetry()
        {
            lock (_lifecycleLock)
            {
                if (_disposed || _bindRetryTimer != null) return;
                _bindRetryTimer = new Timer(delegate { RetryBind(); }, null, 2000, 3000);
            }
        }

        private void StopBindRetryTimer()
        {
            Timer timer;
            lock (_lifecycleLock)
            {
                timer = _bindRetryTimer;
                _bindRetryTimer = null;
            }
            if (timer != null) try { timer.Dispose(); } catch { }
        }

        private void RetryBind()
        {
            lock (_lifecycleLock)
            {
                if (_disposed || _running) return;
            }
            Start();
        }

        /// <summary>
        /// 重新枚举本机可用 IPv4 地址，同步多播组成员关系：
        /// 新出现的接口 AddMembership，已消失的接口 DropMembership。
        /// </summary>
        private void RefreshInterfaces()
        {
            UdpClient client = _client;
            if (client == null) return;

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
                client = _client;
                if (client == null) return;

                // 退出已消失的接口
                List<IPAddress> toDrop = new List<IPAddress>();
                for (int i = 0; i < _joinedIfaces.Count; i++)
                    if (!ContainsAddr(usable, _joinedIfaces[i])) toDrop.Add(_joinedIfaces[i]);

                for (int i = 0; i < toDrop.Count; i++)
                {
                    IPAddress a = toDrop[i];
                    try
                    {
                        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership,
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
                        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                            new MulticastOption(_group, a));
                        _joinedIfaces.Add(a);
                        Log.Info("Joined multicast " + _group + " on iface " + a);
                    }
                    catch (Exception ex) { Log.Warn("Join on " + a + " failed: " + ex.Message); }
                }

                // Some WinCE stacks do not return an address from
                // Dns.GetHostEntry even though the interface is usable.  A
                // wildcard membership lets the OS select the default
                // interface and keeps receive discovery alive in that case.
                if (_joinedIfaces.Count == 0)
                {
                    try
                    {
                        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                            new MulticastOption(_group, IPAddress.Any));
                        _joinedIfaces.Add(IPAddress.Any);
                        Log.Info("Joined multicast " + _group + " on default interface");
                    }
                    catch (Exception ex) { Log.Warn("Join on default interface failed: " + ex.Message); }
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
                UdpClient client = _client;
                if (client == null) break;
                try { data = client.Receive(ref remote); }
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
                e.Announce = msg.Announcement;
                try { h(this, e); } catch (Exception ex) { Log.Warn("PeerDiscovered handler threw: " + ex.Message); }
            }

            // 协议：收到 announce=true 时回复一条 announce=false。
            // 这是 HTTP 注册失败时的兼容回退路径；v2.2 的首选注册由服务层完成。
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
                    lock (_sendLock)
                    {
                        if (_client == null) return;
                        n = _client.Send(payload, payload.Length, new IPEndPoint(from.Address, _port));
                    }
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
            IPEndPoint dst = new IPEndPoint(_group, _port);

            lock (_sendLock)
            {
                UdpClient client = _client;
                if (client == null) return;
                bool sent = false;
                if (_joinedIfaces.Count == 0)
                {
                    try
                    {
                        int n = client.Send(payload, payload.Length, dst);
                        Log.Info("TX " + tag + " (default iface) " + n + "B to " + _group + ":" + _port);
                        sent = true;
                    }
                    catch (Exception ex) { Log.Warn("TX " + tag + " (default) failed: " + ex.Message); }
                }
                else
                {
                    for (int i = 0; i < _joinedIfaces.Count; i++)
                    {
                        IPAddress iface = _joinedIfaces[i];
                        try
                        {
                            int n;
                            if (iface.Equals(IPAddress.Any))
                                n = client.Send(payload, payload.Length, dst);
                            else
                            {
                                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                                    iface.GetAddressBytes());
                                n = client.Send(payload, payload.Length, dst);
                            }
                            Log.Info("TX " + tag + " via " + iface + " " + n + "B");
                            sent = true;
                        }
                        catch (Exception ex) { Log.Warn("TX " + tag + " via " + iface + " failed: " + ex.Message); }
                    }
                }

                // A few access points filter 224/4 while allowing local
                // broadcast.  Keep the standards-compliant multicast above,
                // then send one best-effort broadcast copy as a compatibility
                // fallback.  Duplicate packets are de-duplicated by the
                // fingerprint-keyed PeerRegistry.
                try
                {
                    int n = client.Send(payload, payload.Length,
                        new IPEndPoint(IPAddress.Broadcast, _port));
                    Log.Info("TX " + tag + " broadcast " + n + "B");
                    sent = true;
                }
                catch (Exception ex) { Log.Info("TX " + tag + " broadcast unavailable: " + ex.Message); }

                if (!sent)
                {
                    try
                    {
                        int n = client.Send(payload, payload.Length, dst);
                        Log.Info("TX " + tag + " final default " + n + "B");
                    }
                    catch (Exception ex) { Log.Warn("TX " + tag + " final default failed: " + ex.Message); }
                }
            }
        }

        /// <summary>
        /// 绕开多播，直接向目标 IP 发一条 announcement 单播。用于手动探测（AP 隔离、路由器多播策略等）。
        /// </summary>
        public void SendUnicastAnnounce(IPAddress target)
        {
            if (target == null) return;
            AnnounceMessage m = new AnnounceMessage();
            m.Info = _self;
            m.Announcement = true;
            byte[] payload = Encoding.UTF8.GetBytes(Json.Stringify(m.ToJson()));
            UdpClient probe = null;
            bool temporary = false;
            try
            {
                int n;
                lock (_sendLock)
                {
                    probe = _client;
                    if (probe == null)
                    {
                        probe = new UdpClient();
                        temporary = true;
                    }
                    n = probe.Send(payload, payload.Length, new IPEndPoint(target, _port));
                }
                Log.Info("TX unicast probe " + n + "B to " + target + ":" + _port);
            }
            catch (Exception ex) { Log.Warn("Unicast probe send failed: " + ex.Message); }
            finally
            {
                if (temporary && probe != null) try { probe.Close(); } catch { }
            }
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
            if (r.Count == 0)
            {
                try
                {
#pragma warning disable 0618
                    IPHostEntry he = Dns.GetHostByName(Dns.GetHostName());
#pragma warning restore 0618
                    if (he != null && he.AddressList != null)
                    {
                        for (int i = 0; i < he.AddressList.Length; i++)
                        {
                            IPAddress a = he.AddressList[i];
                            if (a != null && a.AddressFamily == AddressFamily.InterNetwork) r.Add(a);
                        }
                    }
                }
                catch (Exception ex) { Log.Warn("Legacy IPv4 lookup failed: " + ex.Message); }
            }
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
