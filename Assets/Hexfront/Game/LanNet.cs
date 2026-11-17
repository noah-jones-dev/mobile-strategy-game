using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Hexfront
{
    /// <summary>Message kinds exchanged between host and client.</summary>
    public enum Msg : byte
    {
        Hello = 1,      // client -> host: protocol version + player name
        Welcome,        // host -> client: player index, match settings and (if started) the full state
        Lobby,          // host -> client: text describing the lobby settings
        Orders,         // client -> host: the client's locked-in orders for the round
        OpponentLocked, // host -> client: the host player is ready
        Round,          // host -> client: both order sets + the state hash to verify against
        ResyncRequest,  // client -> host: "my state hash differs, send the full state"
        Ping,           // both ways: keep-alive
        Reject,         // host -> client: connection refused (text reason)
    }

    public sealed class NetMessage
    {
        public Msg Type;
        public byte[] Payload;
    }

    /// <summary>
    /// A TCP connection that sends and receives length-prefixed messages. Reading happens on a
    /// background thread; the game polls TryReceive from the main thread, so no Unity API is
    /// ever touched off the main thread.
    /// </summary>
    public sealed class NetConnection
    {
        public const int ProtocolVersion = 1;
        const int MaxMessage = 16 * 1024 * 1024;

        readonly TcpClient _tcp;
        readonly NetworkStream _stream;
        readonly ConcurrentQueue<NetMessage> _inbox = new ConcurrentQueue<NetMessage>();
        readonly object _sendLock = new object();
        volatile bool _closed;
        int _lastReceiveTick;

        public bool IsAlive => !_closed;
        public string RemoteAddress { get; }
        public float SecondsSinceReceive => unchecked(Environment.TickCount - Volatile.Read(ref _lastReceiveTick)) / 1000f;

        public NetConnection(TcpClient tcp)
        {
            _tcp = tcp;
            _tcp.NoDelay = true;
            _stream = tcp.GetStream();
            _lastReceiveTick = Environment.TickCount;
            try { RemoteAddress = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address.ToString(); } catch { RemoteAddress = "?"; }
            var thread = new Thread(ReadLoop) { IsBackground = true, Name = "HexfrontNetRead" };
            thread.Start();
        }

        void ReadLoop()
        {
            var header = new byte[5];
            try
            {
                while (!_closed)
                {
                    ReadExact(header, 5);
                    int length = BitConverter.ToInt32(header, 0);
                    if (length < 0 || length > MaxMessage) throw new IOException("Bad message length");
                    var payload = new byte[length];
                    if (length > 0) ReadExact(payload, length);
                    Volatile.Write(ref _lastReceiveTick, Environment.TickCount);
                    _inbox.Enqueue(new NetMessage { Type = (Msg)header[4], Payload = payload });
                }
            }
            catch (Exception) { /* socket closed or broken: fall through */ }
            Close();
        }

        void ReadExact(byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new IOException("Connection closed");
                offset += read;
            }
        }

        public bool Send(Msg type, byte[] payload = null)
        {
            if (_closed) return false;
            payload = payload ?? Array.Empty<byte>();
            var packet = new byte[5 + payload.Length];
            BitConverter.GetBytes(payload.Length).CopyTo(packet, 0);
            packet[4] = (byte)type;
            Buffer.BlockCopy(payload, 0, packet, 5, payload.Length);
            try
            {
                lock (_sendLock) { _stream.Write(packet, 0, packet.Length); _stream.Flush(); }
                return true;
            }
            catch (Exception) { Close(); return false; }
        }

        public bool TryReceive(out NetMessage message) => _inbox.TryDequeue(out message);

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            try { _tcp.Close(); } catch { }
        }
    }

    /// <summary>The host side: listens for the second player and relays messages to the game.</summary>
    public sealed class NetHost
    {
        public const int DefaultPort = 47811;

        TcpListener _listener;
        readonly ConcurrentQueue<TcpClient> _incoming = new ConcurrentQueue<TcpClient>();
        readonly List<NetConnection> _pending = new List<NetConnection>();
        NetConnection _client;
        volatile bool _running;
        int _lastPingTick;

        public int Port { get; private set; }
        public string ClientName { get; private set; } = "";
        public bool HasClient => _client != null && _client.IsAlive;

        /// <summary>Builds the Welcome payload for a (re)joining client.</summary>
        public Func<byte[]> WelcomeProvider;
        public Action<string> ClientJoined;
        public Action ClientLeft;
        public Action<byte[]> OrdersReceived;
        public Action ResyncRequested;

        public bool Start()
        {
            Stop();
            foreach (int port in new[] { DefaultPort, 0 }) // preferred port, else any free one
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Any, port);
                    _listener.Start();
                    Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                    break;
                }
                catch (Exception) { _listener = null; }
            }
            if (_listener == null) return false;
            _running = true;
            var listener = _listener;
            var thread = new Thread(() =>
            {
                while (_running)
                {
                    try { _incoming.Enqueue(listener.AcceptTcpClient()); }
                    catch (Exception) { if (!_running) break; Thread.Sleep(100); }
                }
            }) { IsBackground = true, Name = "HexfrontAccept" };
            thread.Start();
            return true;
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _client?.Close(); _client = null;
            foreach (var p in _pending) p.Close();
            _pending.Clear();
        }

        public void Send(Msg type, byte[] payload = null) => _client?.Send(type, payload);

        /// <summary>Call every frame from the main thread.</summary>
        public void Poll()
        {
            while (_incoming.TryDequeue(out var tcp))
            {
                try { _pending.Add(new NetConnection(tcp)); } catch (Exception) { }
            }

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var c = _pending[i];
                if (!c.IsAlive || c.SecondsSinceReceive > 10f) { c.Close(); _pending.RemoveAt(i); continue; }
                if (!c.TryReceive(out var m)) continue;
                _pending.RemoveAt(i);
                if (m.Type != Msg.Hello) { c.Close(); continue; }
                string name; int version;
                try
                {
                    using (var r = new BinaryReader(new MemoryStream(m.Payload))) { version = r.ReadInt32(); name = r.ReadString(); }
                }
                catch (Exception) { c.Close(); continue; }
                if (version != NetConnection.ProtocolVersion)
                {
                    c.Send(Msg.Reject, Encoding.UTF8.GetBytes("Game versions do not match"));
                    c.Close();
                    continue;
                }
                // A new Hello replaces the old connection: that is how a dropped player reconnects.
                _client?.Close();
                _client = c;
                ClientName = name;
                if (WelcomeProvider != null) c.Send(Msg.Welcome, WelcomeProvider());
                ClientJoined?.Invoke(name);
            }

            if (_client != null)
            {
                while (_client.TryReceive(out var m))
                {
                    if (m.Type == Msg.Orders) OrdersReceived?.Invoke(m.Payload);
                    else if (m.Type == Msg.ResyncRequest) ResyncRequested?.Invoke();
                }
                if (unchecked(Environment.TickCount - _lastPingTick) > 2000)
                {
                    _lastPingTick = Environment.TickCount;
                    _client.Send(Msg.Ping);
                }
                if (!_client.IsAlive || _client.SecondsSinceReceive > 10f)
                {
                    _client.Close();
                    _client = null;
                    ClientLeft?.Invoke();
                }
            }
        }
    }

    /// <summary>The joining side. Reconnects by itself if the link drops mid-match.</summary>
    public sealed class NetClient
    {
        string _host, _name;
        int _port;
        NetConnection _conn;
        volatile NetConnection _fresh;
        volatile bool _connecting, _failed;
        bool _wasConnected, _stopped = true;
        int _lastPingTick, _lastAttemptTick;

        public bool IsConnected => _conn != null && _conn.IsAlive;
        public bool AutoReconnect;
        public string HostAddress => _host;

        public Action Connected;
        public Action Disconnected;
        public Action ConnectFailed;
        public Action<NetMessage> Received;

        public void Connect(string host, int port, string playerName)
        {
            _host = host; _port = port; _name = playerName; _stopped = false;
            BeginConnect();
        }

        void BeginConnect()
        {
            if (_connecting) return;
            _connecting = true; _failed = false;
            _lastAttemptTick = Environment.TickCount;
            string host = _host; int port = _port; string name = _name;
            var thread = new Thread(() =>
            {
                try
                {
                    var tcp = new TcpClient();
                    var ar = tcp.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(4000)) { tcp.Close(); throw new TimeoutException(); }
                    tcp.EndConnect(ar);
                    var conn = new NetConnection(tcp);
                    using (var ms = new MemoryStream())
                    using (var w = new BinaryWriter(ms))
                    {
                        w.Write(NetConnection.ProtocolVersion); w.Write(name ?? "Player");
                        w.Flush();
                        conn.Send(Msg.Hello, ms.ToArray());
                    }
                    _fresh = conn;
                }
                catch (Exception) { _failed = true; }
                _connecting = false;
            }) { IsBackground = true, Name = "HexfrontConnect" };
            thread.Start();
        }

        public void Stop()
        {
            _stopped = true; AutoReconnect = false;
            _conn?.Close(); _conn = null;
            _fresh?.Close(); _fresh = null;
            _wasConnected = false;
        }

        public void Send(Msg type, byte[] payload = null) => _conn?.Send(type, payload);

        /// <summary>Call every frame from the main thread.</summary>
        public void Poll()
        {
            if (_stopped) return;
            var fresh = _fresh;
            if (fresh != null)
            {
                _fresh = null;
                _conn?.Close();
                _conn = fresh;
                _wasConnected = true;
                Connected?.Invoke();
            }
            if (_failed)
            {
                _failed = false;
                if (!AutoReconnect) ConnectFailed?.Invoke();
            }

            if (_conn != null)
            {
                while (_conn != null && _conn.TryReceive(out var m))
                    if (m.Type != Msg.Ping) Received?.Invoke(m);
                if (_conn == null) return; // a handler stopped us
                if (unchecked(Environment.TickCount - _lastPingTick) > 2000)
                {
                    _lastPingTick = Environment.TickCount;
                    _conn.Send(Msg.Ping);
                }
                if (!_conn.IsAlive || _conn.SecondsSinceReceive > 10f)
                {
                    _conn.Close();
                    _conn = null;
                    if (_wasConnected) { _wasConnected = false; Disconnected?.Invoke(); }
                }
            }
            else if (AutoReconnect && !_connecting && unchecked(Environment.TickCount - _lastAttemptTick) > 2000)
            {
                BeginConnect();
            }
        }
    }

    public sealed class LanLobby
    {
        public string Name, Info, Address;
        public int Port;
        public int LastSeenTick;
    }

    /// <summary>
    /// Finds games on the local network with UDP broadcast: the host shouts a small "beacon"
    /// once a second; anyone listening on the same Wi-Fi hears it. Nothing leaves the LAN.
    /// </summary>
    public sealed class LanDiscovery
    {
        const int DiscoveryPort = 47810;
        const string Magic = "HEXFRONT1";

        volatile bool _running;
        UdpClient _udp;
        readonly ConcurrentQueue<LanLobby> _heard = new ConcurrentQueue<LanLobby>();
        public readonly List<LanLobby> Lobbies = new List<LanLobby>();

        /// <summary>Host: announce the lobby. <paramref name="beacon"/> returns "port|name|info" each second.</summary>
        public void StartBroadcast(Func<string> beacon)
        {
            Stop();
            _running = true;
            var thread = new Thread(() =>
            {
                UdpClient udp = null;
                try
                {
                    udp = new UdpClient { EnableBroadcast = true };
                    _udp = udp;
                    while (_running)
                    {
                        byte[] data = Encoding.UTF8.GetBytes(Magic + "|" + beacon());
                        foreach (var address in BroadcastAddresses())
                        {
                            try { udp.Send(data, data.Length, new IPEndPoint(address, DiscoveryPort)); } catch (Exception) { }
                        }
                        Thread.Sleep(1000);
                    }
                }
                catch (Exception) { }
                finally { try { udp?.Close(); } catch { } }
            }) { IsBackground = true, Name = "HexfrontBeacon" };
            thread.Start();
        }

        /// <summary>Client: start collecting beacons. Read <see cref="Lobbies"/> after calling Poll().</summary>
        public void StartListening()
        {
            Stop();
            _running = true;
            Lobbies.Clear();
            var thread = new Thread(() =>
            {
                UdpClient udp = null;
                try
                {
                    udp = new UdpClient();
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                    _udp = udp;
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    while (_running)
                    {
                        byte[] data = udp.Receive(ref remote);
                        var parts = Encoding.UTF8.GetString(data).Split('|');
                        if (parts.Length < 4 || parts[0] != Magic || !int.TryParse(parts[1], out int port)) continue;
                        _heard.Enqueue(new LanLobby { Port = port, Name = parts[2], Info = parts[3], Address = remote.Address.ToString(), LastSeenTick = Environment.TickCount });
                    }
                }
                catch (Exception) { }
                finally { try { udp?.Close(); } catch { } }
            }) { IsBackground = true, Name = "HexfrontListen" };
            thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _udp?.Close(); } catch { }
            _udp = null;
        }

        /// <summary>Main thread: merge what was heard and drop lobbies that went quiet.</summary>
        public bool Poll()
        {
            bool changed = false;
            while (_heard.TryDequeue(out var l))
            {
                var existing = Lobbies.Find(x => x.Address == l.Address && x.Port == l.Port);
                if (existing == null) { Lobbies.Add(l); changed = true; }
                else
                {
                    if (existing.Name != l.Name || existing.Info != l.Info) changed = true;
                    existing.Name = l.Name; existing.Info = l.Info; existing.LastSeenTick = l.LastSeenTick;
                }
            }
            int now = Environment.TickCount;
            changed |= Lobbies.RemoveAll(x => unchecked(now - x.LastSeenTick) > 4500) > 0;
            return changed;
        }

        /// <summary>The global broadcast address plus each network adapter's own broadcast address.</summary>
        static List<IPAddress> BroadcastAddresses()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            foreach (var local in LocalAddresses(out var masks))
            {
                byte[] ip = local.GetAddressBytes();
                masks.TryGetValue(local, out byte[] mask);
                if (mask == null || mask.Length != 4) mask = new byte[] { 255, 255, 255, 0 }; // assume a home network
                var b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                var address = new IPAddress(b);
                if (!list.Contains(address)) list.Add(address);
            }
            return list;
        }

        public static List<IPAddress> LocalAddresses() => LocalAddresses(out _);

        static List<IPAddress> LocalAddresses(out Dictionary<IPAddress, byte[]> masks)
        {
            var list = new List<IPAddress>();
            masks = new Dictionary<IPAddress, byte[]>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                        if (!list.Contains(ua.Address)) list.Add(ua.Address);
                        try { if (ua.IPv4Mask != null) masks[ua.Address] = ua.IPv4Mask.GetAddressBytes(); } catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            if (list.Count == 0)
            {
                // Fallback for platforms where adapter enumeration is unavailable: ask the OS which
                // local address it would route from. (A UDP "connect" sends no packets.)
                try
                {
                    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        socket.Connect("10.255.255.255", 65530);
                        if (socket.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address)) list.Add(ep.Address);
                    }
                }
                catch (Exception) { }
            }
            return list;
        }
    }
}
