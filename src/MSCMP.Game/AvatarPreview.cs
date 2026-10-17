using System;
using MSCMP.Network;
using UnityEngine;

namespace MSCMP.Integration
{
    /// <summary>Optional read-only walking-pose preview; this grants no shared-world authority.</summary>
    internal sealed class AvatarPreview : IDisposable
    {
        private readonly DiagnosticSession session;
        private readonly Guid sessionId;
        private readonly ulong sessionGeneration;
        private readonly ulong ownPlayer;
        private readonly int generation;
        private readonly AvatarRelayTracker tracker = new AvatarRelayTracker();
        private readonly AvatarRemovalOutbox removals = new AvatarRemovalOutbox();
        private LocalPlayerBinding binding;
        private RemoteAvatars avatars;
        private float nextPoseAt;
        private float nextAppearanceAt;
        private CharacterStyle selectedStyle;
        private bool appearanceQueued;
        private bool disposed;
        internal Func<bool> Seated;
        internal bool SendLocalPose = true;
        internal Func<ulong, Vector3?> AcceptedPosition;
        internal void SetWorldPresentation(Func<ulong, bool> seated, Func<ulong, bool> holding)
        { avatars.Seated = seated; avatars.Holding = holding; }
        internal void SetSeatPresentation(Func<ulong, Vector3?> position, Func<ulong, float> yaw)
        { avatars.SeatPosition = position; avatars.SeatYaw = yaw; }
        internal Vector3? CarryPosition(ulong playerId) { return avatars == null ? null : avatars.CarryPosition(playerId); }
        internal bool IsActive { get { return Current(); } }

        private AvatarPreview(DiagnosticSession activeSession, int sceneGeneration, LocalPlayerBinding local, CharacterStyle style)
        {
            session = activeSession; sessionId = session.SessionId; sessionGeneration = session.SessionGeneration;
            ownPlayer = session.LocalPlayerId; generation = sceneGeneration; binding = local;
            selectedStyle = style;
            avatars = new RemoteAvatars(generation, checked((int)ownPlayer));
            session.PoseReceived += OnPose;
            session.ApplicationReceived += OnApplication;
            session.Event += OnEvent;
        }

        internal static bool TryCreate(DiagnosticSession session, int generation, out AvatarPreview preview, out string reason)
        {
            return TryCreate(session, generation, CharacterStyle.Male, out preview, out reason);
        }

        internal static bool TryCreate(DiagnosticSession session, int generation, CharacterStyle selectedStyle, out AvatarPreview preview, out string reason)
        {
            preview = null;
            reason = "Choose Male or Female before enabling avatars.";
            if (!AvatarRelayCodec.IsStyle(selectedStyle)) return false;
            reason = "Start or join an accepted diagnostic session before enabling avatar preview.";
            if (session == null || !session.IsRunning || session.LocalPlayerId == 0 || session.LocalPlayerId > Int32.MaxValue
                || session.SessionId == Guid.Empty || session.SessionGeneration == 0) return false;
            LocalPlayerBinding binding;
            if (!LocalPlayerBinding.TryCreate(generation, out binding, out reason)) return false;
            try
            {
                preview = new AvatarPreview(session, generation, binding, selectedStyle);
                reason = "Original avatar preview enabled; walking poses only, no shared world or item interactions.";
                return true;
            }
            catch (Exception error)
            {
                binding.Dispose();
                reason = "Avatar preview could not initialize: " + error.GetType().Name;
                return false;
            }
        }

        internal void Poll()
        {
            if (!Current()) { Dispose(); return; }
            float now = Time.realtimeSinceStartup;
            if (session.IsHost) FlushRemovals();
            if (now >= nextAppearanceAt)
            {
                // Reannounce a bounded baseline so enabling preview late, a lost
                // outgoing enqueue, or a new accepted connection converges.
                nextAppearanceAt = now + 1.0f;
                if (AnnounceAppearance()) appearanceQueued = true;
            }
            if (SendLocalPose && appearanceQueued && now >= nextPoseAt)
            {
                nextPoseAt = now + 0.1f;
                Vector3 position; float yaw;
                if (binding.TrySample(generation, out position, out yaw) || (Seated != null && Seated() && binding.TrySampleSeated(out position, out yaw))) session.SendPose(position.x, position.y, position.z, yaw);
            }
            avatars.Update(generation, now);
        }

        internal bool SetCharacterStyle(CharacterStyle style)
        {
            if (!Current() || !AvatarRelayCodec.IsStyle(style)) return false;
            if (selectedStyle != style)
            {
                selectedStyle = style;
                nextAppearanceAt = 0;
                appearanceQueued = false;
            }
            return true;
        }

        private bool AnnounceAppearance()
        {
            SessionPeer[] peers = session.GetAcceptedPeers();
            bool any = false;
            for (int i = 0; i < peers.Length; i++)
            {
                AvatarRelayMessage appearance = new AvatarRelayMessage { Kind = AvatarRelayKind.Appearance,
                    PlayerId = ownPlayer, ConnectionGeneration = peers[i].ConnectionGeneration, Style = selectedStyle };
                if (session.SendApplication(peers[i].PlayerId, AvatarRelayCodec.Encode(appearance))) any = true;
            }
            return any;
        }

        private void SendBaselineTo(SessionPeer target)
        {
            // Host's direct pose stream uses each recipient connection's generation.
            AvatarRelayMessage hostAppearance = new AvatarRelayMessage { Kind = AvatarRelayKind.Appearance,
                PlayerId = 1, ConnectionGeneration = target.ConnectionGeneration, Style = selectedStyle };
            session.SendApplication(target.PlayerId, AvatarRelayCodec.Encode(hostAppearance));
            AvatarRelayMessage[] known = tracker.GetAppearances();
            for (int i = 0; i < known.Length; i++)
                if (known[i].PlayerId != target.PlayerId) session.SendApplication(target.PlayerId, AvatarRelayCodec.Encode(known[i]));
        }

        private void FlushRemovals()
        {
            SessionPeer[] peers = session.GetAcceptedPeers();
            for (int i = 0; i < peers.Length; i++)
            {
                AvatarRelayMessage[] pending = removals.Pending(peers[i].PlayerId, peers[i].ConnectionGeneration);
                for (int j = 0; j < pending.Length; j++)
                {
                    if (!session.SendApplication(peers[i].PlayerId, AvatarRelayCodec.Encode(pending[j]))) break;
                    removals.Confirm(peers[i].PlayerId, peers[i].ConnectionGeneration, pending[j]);
                }
            }
        }

        private void OnPose(PoseMessage pose)
        {
            if (!Current() || pose == null || pose.PlayerId == ownPlayer || (!session.IsHost && pose.PlayerId != 1)) return;
            AvatarRelayMessage message = new AvatarRelayMessage { Kind = AvatarRelayKind.Pose, PlayerId = pose.PlayerId,
                ConnectionGeneration = pose.ConnectionGeneration, Sequence = pose.Sequence, X = pose.X, Y = pose.Y, Z = pose.Z, Yaw = pose.Yaw };
            if (session.IsHost && AcceptedPosition != null)
            {
                Vector3? position = AcceptedPosition(pose.PlayerId);
                if (!position.HasValue) return;
                message.X = position.Value.x; message.Y = position.Value.y; message.Z = position.Value.z;
            }
            // Encode validates finite/bounded pose and representable actor ID before touching Unity.
            byte[] payload;
            try { payload = AvatarRelayCodec.Encode(message); }
            catch (ArgumentException) { return; }
            if (!Apply(message)) return;
            if (session.IsHost)
            {
                SessionPeer[] peers = session.GetAcceptedPeers();
                for (int i = 0; i < peers.Length; i++)
                    if (peers[i].PlayerId != pose.PlayerId) session.SendApplication(peers[i].PlayerId, payload);
            }
        }

        private void OnApplication(ApplicationMessage incoming)
        {
            if (!Current() || incoming == null) return;
            AvatarRelayMessage message;
            if (session.IsHost)
            {
                if (!AvatarRelayCodec.BindClientAppearance(incoming.Payload, incoming.PlayerId, incoming.ConnectionGeneration, incoming.Sequence, out message)
                    || !Apply(message)) return;
                SessionPeer[] peers = session.GetAcceptedPeers();
                byte[] relay = AvatarRelayCodec.Encode(message);
                for (int i = 0; i < peers.Length; i++)
                    if (peers[i].PlayerId != message.PlayerId) session.SendApplication(peers[i].PlayerId, relay);
                return;
            }
            // Relays are accepted only over the already-bound host connection.
            if (incoming.PlayerId != 1 || !AvatarRelayCodec.TryDecode(incoming.Payload, out message) || message.PlayerId == ownPlayer) return;
            if (message.PlayerId == 1)
            {
                if (message.Kind != AvatarRelayKind.Appearance || message.ConnectionGeneration != incoming.ConnectionGeneration || message.Sequence != 0) return;
                // Host appearance uses the ordered bound envelope's sequence.
                message.Sequence = incoming.Sequence;
            }
            Apply(message);
        }

        private bool Apply(AvatarRelayMessage message)
        {
            bool reset;
            if (!tracker.Apply(message, ownPlayer, out reset)) return false;
            int actor = checked((int)message.PlayerId);
            if (reset) avatars.Remove(generation, actor);
            if (message.Kind == AvatarRelayKind.Appearance)
                avatars.SetAppearance(generation, actor, message.Style);
            else if (message.Kind == AvatarRelayKind.Pose)
                avatars.ReceivePose(generation, actor, new Vector3(message.X, message.Y, message.Z), message.Yaw, Time.realtimeSinceStartup);
            return true;
        }

        private void OnEvent(SessionEvent e)
        {
            if (disposed || e == null) return;
            if (e.Kind == "Stopped") { Dispose(); return; }
            if (!Current()) return;
            if (e.Kind == "Accepted")
            {
                removals.RemoveRecipient(e.PlayerId);
                nextAppearanceAt = 0;
                if (session.IsHost && e.PlayerId >= 2 && e.PlayerId <= Int32.MaxValue)
                    SendBaselineTo(new SessionPeer { PlayerId = e.PlayerId, ConnectionGeneration = e.ConnectionGeneration });
                return;
            }
            if (e.Kind != "Disconnected" || e.PlayerId == 0 || e.PlayerId > Int32.MaxValue || e.ConnectionGeneration == 0) return;
            removals.RemoveRecipient(e.PlayerId);
            if (!session.IsHost) { Dispose(); return; }
            AvatarRelayMessage removal = new AvatarRelayMessage { Kind = AvatarRelayKind.Remove,
                PlayerId = e.PlayerId, ConnectionGeneration = e.ConnectionGeneration };
            Apply(removal);
            SessionPeer[] peers = session.GetAcceptedPeers();
            for (int i = 0; i < peers.Length; i++)
                if (!removals.Queue(peers[i].PlayerId, peers[i].ConnectionGeneration, removal))
                    session.DisconnectPlayer(peers[i].PlayerId, "Avatar removal retry capacity exhausted; reconnect diagnostics");
            FlushRemovals();
        }

        private bool Current()
        {
            return !disposed && session.IsRunning && session.SessionId == sessionId && session.SessionGeneration == sessionGeneration
                && session.LocalPlayerId == ownPlayer && Application.loadedLevelName == "GAME";
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            session.PoseReceived -= OnPose;
            session.ApplicationReceived -= OnApplication;
            session.Event -= OnEvent;
            removals.Clear();
            if (binding != null) { binding.Dispose(); binding = null; }
            if (avatars != null) { avatars.Dispose(); avatars = null; }
        }
    }
}
