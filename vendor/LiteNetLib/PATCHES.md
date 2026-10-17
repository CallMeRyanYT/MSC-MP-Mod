# Vendored LiteNetLib diagnostic profile

Upstream: [RevenantX/LiteNetLib 0.7.7.2](https://github.com/RevenantX/LiteNetLib/tree/5ba2470394092ac5924b1ea45da588cfa5a1e948), commit `5ba2470394092ac5924b1ea45da588cfa5a1e948`, MIT. The complete license is in `LICENSE.txt`. `dependencies.lock.json` records upstream and resulting file hashes.

The upstream project explicitly targets .NET Framework 3.5. These source files compile with the installed .NET Framework 3.5 C# compiler. No package restore, native dependency, game assembly redistribution, or modern runtime compatibility shim is required.

This is a deliberately restricted diagnostic profile, not an unrestricted distribution of upstream LiteNetLib:

- Omitted the optional reflection serializer `Utils/NetSerializer.cs` because it uses C# 4 optional parameters; the mod uses its own explicit bounded protocol codec. Omitted upstream `Properties/AssemblyInfo.cs` because the library is compiled into the mod assembly. Project/sample/build files are not compiled or distributed as dependencies.
- Added `DiagnosticPacketGuard.cs` and a call at the beginning of `NetManager.DataReceived`, before packet allocation/parsing. It permits at most 512 raw bytes, checks control packet lengths/sequence range, and rejects fragmented, merged, discovery, NAT, and MTU packets. Application payloads are bounded separately at 480 bytes. This avoids entering the old library's unbounded fragment assembly and merged-packet paths.
- Set `NetPeer._finishMtu = true` so the initial 508-byte UDP MTU stays fixed and no larger outgoing MTU probes are emitted. Ordinary 480-byte application messages fit without fragmentation.
- `SequencedChannel.AddToQueue` recycles older not-yet-sent sequenced packets before enqueueing the newest. Its outgoing queue therefore contains at most one packet even if a worker stops making progress. This policy is appropriate for disposable diagnostic poses; it must not be used for transactions.
- All `NetPeerCollection` access uses the same collection monitor already held by upstream's logic iteration. A repeated localhost timeout/disconnect test exposed a real `NullReferenceException` from concurrent `RemoveAt` and unguarded remote/local removal; consistent collection locking prevents that race. The wrapper also tracks outgoing connection attempts and ignores duplicate disconnect callbacks after removal.

The reliable retransmission, ordering, acknowledgement and sequence algorithms are upstream. The wrapper restricts connections to three remote peers, dispatches worker callbacks into a bounded queue, and caps application send rate and reliable backlog. This version has one reliable ordered stream; bulk snapshot channels are not implemented or claimed. Replacing this profile with a later compatible version requires re-running the protocol, impairment and real-game gates.

This old library is selected for a bounded private LAN diagnostic experiment. It has not undergone a comprehensive security audit and is not a public Internet transport. The application session generation prevents stale messages from mutating application state; old underlying transport sequences can still interfere with liveness during an endpoint-reuse/reconnect race. A stall must end/restart the diagnostic session; do not advertise seamless reconnect or production gameplay based on these tests.

- v0.2.1 removes Console.ForegroundColor writes from the fallback logger because the shipped Unity Mono profile lacks the setter. Console.WriteLine and custom logger routing remain unchanged.
