using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;

// Exercise the actual legacy socket with concurrent receives and sends to three
// destinations. The old SocketAddress cache race silently delivered one peer's
// reliable packet to another peer, which then acknowledged the wrong sequence.
internal sealed class TransportFanoutTests : INetEventListener
{
    private readonly object sync = new object();
    private readonly List<NetPeer> peers = new List<NetPeer>();
    private readonly int expected;
    private int received;
    private string failure;
    private TransportFanoutTests(int destination) { expected = destination; }
    private int PeerCount { get { lock (sync) return peers.Count; } }
    private NetPeer Peer(int index) { lock (sync) return peers[index]; }
    private int Received { get { lock (sync) return received; } }
    private string Failure { get { lock (sync) return failure; } }
    public void OnPeerConnected(NetPeer peer) { lock (sync) peers.Add(peer); }
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info) { }
    public void OnNetworkError(NetEndPoint endpoint, int error) { lock (sync) failure = "Socket error " + error; }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    public void OnNetworkReceiveUnconnected(NetEndPoint endpoint, NetDataReader reader, UnconnectedMessageType kind) { }
    public void OnNetworkReceive(NetPeer peer, NetDataReader reader)
    {
        byte[] bytes = reader.GetRemainingBytes();
        lock (sync)
        {
            if (bytes.Length < 100 || bytes.Length > 382 || bytes[0] != expected)
                failure = "A packet reached the wrong destination or changed length.";
            else if (BitConverter.ToInt32(bytes, 1) != received + 1)
                failure = "Reliable fanout lost, duplicated or reordered a payload.";
            else
                for (int i = 5; i < bytes.Length; i++)
                    if (bytes[i] != expected) { failure = "Reliable fanout changed payload bytes."; break; }
            received++;
        }
    }

    public static int Main()
    {
        const int batches = 700, perBatch = 48; // crosses the 32768 transport sequence wrap
        TransportFanoutTests hostListener = new TransportFanoutTests(0);
        NetManager host = new NetManager(hostListener, 3, "MSCMP-FANOUT-REGRESSION");
        NetManager[] clients = new NetManager[3];
        TransportFanoutTests[] listeners = new TransportFanoutTests[3];
        int assertions = 0;
        try
        {
            host.UnsyncedEvents = true;
            if (!host.Start(0)) throw new Exception("Cannot start fanout host.");
            for (int i = 0; i < clients.Length; i++)
            {
                listeners[i] = new TransportFanoutTests(i + 1);
                clients[i] = new NetManager(listeners[i], "MSCMP-FANOUT-REGRESSION");
                clients[i].UnsyncedEvents = true;
                if (!clients[i].Start(0)) throw new Exception("Cannot start fanout client.");
                clients[i].Connect("127.0.0.1", host.LocalPort);
                Stopwatch connect = Stopwatch.StartNew();
                while (hostListener.PeerCount != i + 1 || listeners[i].PeerCount != 1)
                {
                    if (connect.ElapsedMilliseconds > 5000) throw new Exception("Fanout connection timed out.");
                    Thread.Sleep(1);
                }
            }
            for (int batch = 0; batch < batches; batch++)
            {
                for (int packet = 0; packet < perBatch; packet++)
                    for (int destination = 0; destination < clients.Length; destination++)
                    {
                        byte[] bytes = new byte[100 + packet * 6];
                        for (int j = 0; j < bytes.Length; j++) bytes[j] = (byte)(destination + 1);
                        Buffer.BlockCopy(BitConverter.GetBytes(batch * perBatch + packet + 1), 0, bytes, 1, 4);
                        hostListener.Peer(destination).Send(bytes, SendOptions.ReliableOrdered);
                    }
                Stopwatch delivery = Stopwatch.StartNew();
                while (true)
                {
                    bool complete = true;
                    for (int i = 0; i < clients.Length; i++)
                    {
                        if (listeners[i].Failure != null) throw new Exception(listeners[i].Failure);
                        if (listeners[i].Received != (batch + 1) * perBatch) complete = false;
                    }
                    if (complete) break;
                    if (delivery.ElapsedMilliseconds > 5000) throw new Exception("Fanout delivery timed out.");
                    Thread.Sleep(1);
                }
                assertions += clients.Length;
            }
            Console.WriteLine("Transport fanout passed: " + assertions + " assertions; 100800 addressed payloads across sequence wrap on CLR " + Environment.Version + ".");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            host.Stop();
            foreach (NetManager client in clients) if (client != null) client.Stop();
        }
    }
}
