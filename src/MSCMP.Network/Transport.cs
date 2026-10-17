using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;

namespace MSCMP.Network
{
    public enum TransportEventKind { Connected, Disconnected, Data, Error }
    public sealed class TransportEvent
    {
        public TransportEventKind Kind;
        public int Connection;
        public byte[] Data;
        public string Detail;
    }

    public interface INetworkTransport : IDisposable
    {
        bool IsRunning { get; }
        int LocalPort { get; }
        int PeerCount { get; }
        int QueueDepth { get; }
        long SentBytes { get; }
        long ReceivedBytes { get; }
        void Start(int port, int maxPeers);
        void Connect(string address, int port);
        bool Send(int connection, byte[] data, bool sequenced);
        void Disconnect(int connection);
        TransportEvent[] Poll();
        void Stop();
    }

    // The library workers only copy bounded bytes into a bounded queue. All protocol
    // interpretation and application callbacks happen in Poll on the caller's thread.
    public sealed class LiteNetTransport : INetworkTransport, INetEventListener
    {
        public const int MaximumQueuedEvents = 256;
        public const int MaximumReliableQueue = 128;
        private readonly object sync = new object();
        private readonly Queue<TransportEvent> events = new Queue<TransportEvent>();
        private readonly Dictionary<int, NetPeer> peers = new Dictionary<int, NetPeer>();
        private readonly Dictionary<NetPeer, int> identifiers = new Dictionary<NetPeer, int>();
        private readonly Dictionary<int, int> sendsByPeer = new Dictionary<int, int>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private NetManager manager;
        private volatile bool running;
        private bool overflow;
        private int nextConnection;
        private int sentThisWindow;
        private long sendWindow;
        private long sentBytes, receivedBytes;

        public bool IsRunning { get { return running; } }
        public int LocalPort { get { return running ? manager.LocalPort : 0; } }
        public int PeerCount { get { lock (sync) return peers.Count; } }
        public int QueueDepth { get { lock (sync) return events.Count; } }
        public long SentBytes { get { lock (sync) return sentBytes; } }
        public long ReceivedBytes { get { lock (sync) return receivedBytes; } }

        public void Start(int port, int maxPeers)
        {
            if (running) throw new InvalidOperationException("Transport is already running.");
            if (port < 0 || port > 65535 || maxPeers < 1 || maxPeers > 3) throw new ArgumentOutOfRangeException("port/maxPeers");
            manager = new NetManager(this, maxPeers, "MSCMP-DIAGNOSTICS-2");
            manager.UnsyncedEvents = true; manager.DisconnectTimeout = 5000;
            manager.MaxConnectAttempts = 5; manager.ReconnectDelay = 500;
            manager.UpdateTime = 15; manager.PingInterval = 1000;
            lock (sync) { events.Clear(); peers.Clear(); identifiers.Clear(); sendsByPeer.Clear(); overflow = false; sentBytes = receivedBytes = 0; sentThisWindow = 0; }
            running = true;
            if (!manager.Start(port)) { running = false; manager.Stop(); throw new InvalidOperationException("Cannot open UDP port."); }
        }

        public void Connect(string address, int port)
        {
            IPAddress parsed;
            if (!running) throw new InvalidOperationException("Transport is not running.");
            if (port < 1 || port > 65535 || !IPAddress.TryParse(address, out parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ArgumentException("Enter a numeric IPv4 address and port 1-65535.");
            NetPeer outgoing = manager.Connect(address, port);
            lock (sync)
            {
                if (outgoing != null && !identifiers.ContainsKey(outgoing))
                {
                    if (nextConnection == Int32.MaxValue)
                    { Add(new TransportEvent { Kind = TransportEventKind.Error, Detail = "Transport connection identity exhausted; recreate transport" }); return; }
                    identifiers.Add(outgoing, ++nextConnection);
                }
            }
        }

        public bool Send(int connection, byte[] data, bool sequenced)
        {
            if (data == null || data.Length < ProtocolCodec.HeaderBytes || data.Length > ProtocolCodec.MaximumMessageBytes)
                throw new ArgumentException("Invalid outgoing message length.");
            NetPeer peer;
            lock (sync)
            {
                if (!running || !peers.TryGetValue(connection, out peer)) return false;
                long now = clock.ElapsedMilliseconds;
                if (now - sendWindow >= 1000) { sendWindow = now; sentThisWindow = 0; sendsByPeer.Clear(); }
                int peerSends; sendsByPeer.TryGetValue(connection, out peerSends);
                if (sentThisWindow >= 384 || peerSends >= 128 || peer.PacketsCountInReliableOrderedQueue >= MaximumReliableQueue) return false;
                sendsByPeer[connection] = peerSends + 1;
                sentThisWindow++; sentBytes += data.Length;
            }
            peer.Send(data, sequenced ? SendOptions.Sequenced : SendOptions.ReliableOrdered);
            return true;
        }

        public void Disconnect(int connection)
        {
            NetPeer peer; lock (sync) { if (!running || !peers.TryGetValue(connection, out peer)) return; }
            manager.DisconnectPeer(peer);
        }

        public TransportEvent[] Poll()
        {
            TransportEvent[] result; bool failed;
            lock (sync) { failed = overflow; result = events.ToArray(); events.Clear(); }
            if (failed) { Stop(); return new TransportEvent[] { new TransportEvent { Kind = TransportEventKind.Error, Detail = "Inbound queue overflow; session stopped" } }; }
            return result;
        }

        public void Stop()
        {
            running = false;
            if (manager != null) manager.Stop();
            lock (sync) { events.Clear(); peers.Clear(); identifiers.Clear(); sendsByPeer.Clear(); overflow = false; }
        }
        public void Dispose() { Stop(); }
        private void Add(TransportEvent e)
        {
            lock (sync)
            {
                if (!running || overflow) return;
                if (events.Count >= MaximumQueuedEvents) { overflow = true; return; }
                events.Enqueue(e);
            }
        }
        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            lock (sync)
            {
                if (!running) return;
                int id;
                if (!identifiers.TryGetValue(peer, out id))
                {
                    if (nextConnection == Int32.MaxValue)
                    { Add(new TransportEvent { Kind = TransportEventKind.Error, Detail = "Transport connection identity exhausted; recreate transport" }); return; }
                    id = ++nextConnection; identifiers.Add(peer, id);
                }
                if (peers.ContainsKey(id)) return;
                peers.Add(id, peer);
                Add(new TransportEvent { Kind = TransportEventKind.Connected, Connection = id });
            }
        }
        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            lock (sync)
            {
                int id;
                if (!identifiers.TryGetValue(peer, out id)) return;
                identifiers.Remove(peer);
                sendsByPeer.Remove(id);
                if (!peers.Remove(id))
                { Add(new TransportEvent { Kind = TransportEventKind.Error, Detail = "Connection failed: " + info.Reason }); return; }
                Add(new TransportEvent { Kind = TransportEventKind.Disconnected, Connection = id, Detail = info.Reason.ToString() });
            }
        }
        void INetEventListener.OnNetworkReceive(NetPeer peer, NetDataReader reader)
        {
            lock (sync)
            {
                int id; if (!running || !identifiers.TryGetValue(peer, out id)) return;
                if (reader.AvailableBytes > ProtocolCodec.MaximumMessageBytes || reader.AvailableBytes < ProtocolCodec.HeaderBytes)
                { Add(new TransportEvent { Kind = TransportEventKind.Error, Connection = id, Detail = "Invalid incoming message length" }); return; }
                receivedBytes += reader.AvailableBytes;
                if (events.Count >= MaximumQueuedEvents) { overflow = true; return; }
                Add(new TransportEvent { Kind = TransportEventKind.Data, Connection = id, Data = reader.GetRemainingBytes() });
            }
        }
        void INetEventListener.OnNetworkError(NetEndPoint endPoint, int error)
        { Add(new TransportEvent { Kind = TransportEventKind.Error, Detail = "UDP socket error " + error }); }
        void INetEventListener.OnNetworkReceiveUnconnected(NetEndPoint endPoint, NetDataReader reader, UnconnectedMessageType type) { }
        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    }
}
