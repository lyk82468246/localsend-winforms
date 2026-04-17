using System;
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
                // 构造时直接绑定到指定端口；不要再调 Bind（重复绑定会抛 ADDRNOTAVAIL）。
                _client = new UdpClient(_port);
            }
            catch (Exception ex)
            {
                Log.Warn("Discovery UDP bind failed: " + ex.Message);
                _running = false;
                return;
            }

            try { _client.JoinMulticastGroup(_group); }
            catch (Exception ex)
            {
                // 模拟器未连网 / 无合适接口：不致命，退化为仅服务端 HTTP 可用
                Log.Warn("JoinMulticastGroup failed: " + ex.Message);
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

            Log.Info("Discovery started on " + _group + ":" + _port);
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
        }

        public void Dispose() { Stop(); }

        private void RxLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try { data = _client.Receive(ref remote); }
                catch (SocketException) { if (_running) continue; else break; }
                catch (ObjectDisposedException) { break; }

                try { HandlePacket(data, remote); }
                catch (Exception ex) { Log.Warn("Discovery packet parse failed: " + ex.Message); }
            }
        }

        private void HandlePacket(byte[] data, IPEndPoint from)
        {
            string text = Encoding.UTF8.GetString(data, 0, data.Length);
            AnnounceMessage msg = AnnounceMessage.FromJson(Json.ParseObject(text));

            if (msg.Info == null || string.IsNullOrEmpty(msg.Info.Fingerprint)) return;
            if (msg.Info.Fingerprint == _self.Fingerprint) return; // self

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
                try { _client.Send(payload, payload.Length, new IPEndPoint(_group, _port)); }
                catch (Exception ex) { Log.Warn("Announce reply failed: " + ex.Message); }
            }
        }

        private void SendAnnounce(bool announcement)
        {
            if (_client == null) return;
            AnnounceMessage m = new AnnounceMessage();
            m.Info = _self;
            m.Announcement = announcement;
            byte[] payload = Encoding.UTF8.GetBytes(Json.Stringify(m.ToJson()));
            try { _client.Send(payload, payload.Length, new IPEndPoint(_group, _port)); }
            catch (Exception ex) { Log.Warn("Announce send failed: " + ex.Message); }
        }
    }
}
