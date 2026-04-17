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
    /// 接收端与发送端共用一个 socket（加入多播组、允许回送给自身则忽略自身指纹）。
    /// 本类对每一个收发环节打印详细日志，便于真机抓因诊断。
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

            // 1) 列出本机所有 IPv4 地址，方便核对是否在同一子网
            List<IPAddress> locals = GetLocalIPv4Addresses();
            if (locals.Count == 0)
                Log.Warn("No local IPv4 addresses found (DNS lookup returned nothing)");
            else
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < locals.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(locals[i]); }
                Log.Info("Local IPv4 addresses: " + sb);
            }

            // 2) 绑定 UDP 端口
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

            // 3) 设置 TTL / loopback（WM6 默认值不确定，显式设置更稳）
            try
            {
                _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                Log.Info("Multicast TTL=4 set");
            }
            catch (Exception ex) { Log.Warn("Set MulticastTimeToLive failed: " + ex.Message); }

            try
            {
                _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                Log.Info("Multicast loopback enabled");
            }
            catch (Exception ex) { Log.Warn("Set MulticastLoopback failed: " + ex.Message); }

            // 4) 在每个可用 IPv4 接口上加入多播组；失败不致命
            bool anyJoined = false;
            for (int i = 0; i < locals.Count; i++)
            {
                IPAddress a = locals[i];
                if (IsLoopback(a) || IsLinkLocal(a)) continue;
                try
                {
                    _client.JoinMulticastGroup(_group, a);
                    _joinedIfaces.Add(a);
                    anyJoined = true;
                    Log.Info("Joined multicast " + _group + " on iface " + a);
                }
                catch (Exception ex) { Log.Warn("JoinMulticastGroup on " + a + " failed: " + ex.Message); }
            }
            if (!anyJoined)
            {
                // 回退到默认接口
                try
                {
                    _client.JoinMulticastGroup(_group);
                    Log.Info("Joined multicast " + _group + " on default iface");
                }
                catch (Exception ex) { Log.Warn("JoinMulticastGroup (default) failed: " + ex.Message); }
            }

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
                try { _client.DropMulticastGroup(_group); } catch { }
                try { _client.Close(); } catch { }
                _client = null;
            }
            _joinedIfaces.Clear();
        }

        public void Dispose() { Stop(); }

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

                // 每条入站包先打日志，再尝试解析
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
                // 回复使用单播更精准；同时也多播一份兼容各种实现
                try
                {
                    int n = _client.Send(payload, payload.Length, new IPEndPoint(from.Address, _port));
                    Log.Info("TX reply unicast " + n + "B to " + from.Address + ":" + _port);
                }
                catch (Exception ex) { Log.Warn("Announce reply (unicast) failed: " + ex.Message); }
                try
                {
                    int n = _client.Send(payload, payload.Length, new IPEndPoint(_group, _port));
                    Log.Info("TX reply multicast " + n + "B to " + _group + ":" + _port);
                }
                catch (Exception ex) { Log.Warn("Announce reply (multicast) failed: " + ex.Message); }
            }
        }

        private void SendAnnounce(bool announcement)
        {
            if (_client == null) return;
            AnnounceMessage m = new AnnounceMessage();
            m.Info = _self;
            m.Announcement = announcement;
            string json = Json.Stringify(m.ToJson());
            byte[] payload = Encoding.UTF8.GetBytes(json);
            try
            {
                int n = _client.Send(payload, payload.Length, new IPEndPoint(_group, _port));
                Log.Info("TX announce(" + announcement + ") " + n + "B to " + _group + ":" + _port
                    + " payload=" + json);
            }
            catch (Exception ex) { Log.Warn("Announce send failed: " + ex.Message); }
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
                // 替换控制字符以免破坏日志行
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
