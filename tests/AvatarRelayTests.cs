using System;
using MSCMP.Integration;

internal static class AvatarRelayTests
{
    private static int assertions;
    private static void Assert(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static AvatarRelayMessage Pose(ulong actor, ulong generation, uint sequence)
    { return new AvatarRelayMessage { Kind = AvatarRelayKind.Pose, PlayerId = actor, ConnectionGeneration = generation, Sequence = sequence, X = 1, Y = 2, Z = 3, Yaw = 359 }; }
    private static AvatarRelayMessage Appearance(ulong actor, ulong generation, uint sequence, CharacterStyle style)
    { return new AvatarRelayMessage { Kind = AvatarRelayKind.Appearance, PlayerId = actor, ConnectionGeneration = generation, Sequence = sequence, Style = style }; }
    private static int Main()
    {
        AvatarRelayMessage decoded;
        byte[] encoded = AvatarRelayCodec.Encode(Pose(2, 4, 5));
        Assert(encoded.Length == 42, "Pose must use exact bounded wire size");
        Assert(AvatarRelayCodec.TryDecode(encoded, out decoded) && decoded.PlayerId == 2 && decoded.ConnectionGeneration == 4 && decoded.Sequence == 5 && decoded.Yaw == 359, "Round trip");
        for (int length = 0; length < encoded.Length; length++)
        { byte[] shortPacket = new byte[length]; Array.Copy(encoded, shortPacket, length); Assert(!AvatarRelayCodec.TryDecode(shortPacket, out decoded), "Reject truncation " + length); }
        byte[] wrong = (byte[])encoded.Clone(); wrong[0] ^= 1;
        Assert(!AvatarRelayCodec.TryDecode(wrong, out decoded), "Reject wrong magic");
        wrong = (byte[])encoded.Clone(); wrong[5] = 99;
        Assert(!AvatarRelayCodec.TryDecode(wrong, out decoded), "Reject unknown kind");
        wrong = (byte[])encoded.Clone(); Array.Copy(BitConverter.GetBytes(Single.NaN), 0, wrong, 26, 4);
        Assert(!AvatarRelayCodec.TryDecode(wrong, out decoded), "Reject NaN");
        wrong = (byte[])encoded.Clone(); Array.Copy(BitConverter.GetBytes(100001f), 0, wrong, 30, 4);
        Assert(!AvatarRelayCodec.TryDecode(wrong, out decoded), "Reject world bound overflow");
        byte[] appearance = AvatarRelayCodec.Encode(Appearance(2, 4, 0, CharacterStyle.Female));
        Assert(appearance.Length == 27, "Appearance has exact bounded shape");
        Assert(AvatarRelayCodec.TryDecode(appearance, out decoded) && decoded.Style == CharacterStyle.Female, "Female appearance round trip");
        Assert(AvatarRelayCodec.TryDecode(AvatarRelayCodec.Encode(Appearance(3, 5, 1, CharacterStyle.Male)), out decoded)
            && decoded.Style == CharacterStyle.Male, "Male appearance round trip");
        wrong = (byte[])appearance.Clone(); wrong[26] = 0;
        Assert(!AvatarRelayCodec.TryDecode(wrong, out decoded), "Unset appearance rejected instead of silently defaulting");
        wrong[26] = 3;
        Assert(!AvatarRelayCodec.TryDecode(wrong, out decoded), "Unknown appearance rejected");
        wrong = (byte[])appearance.Clone(); wrong[4] = 1;
        Assert(!AvatarRelayCodec.TryDecode(wrong, out decoded), "Legacy relay version rejected");
        byte[] extended = new byte[appearance.Length + 1]; Array.Copy(appearance, extended, appearance.Length);
        Assert(!AvatarRelayCodec.TryDecode(extended, out decoded), "Appearance trailing data rejected");
        Assert(AvatarRelayCodec.BindClientAppearance(appearance, 2, 4, 80, out decoded) && decoded.Sequence == 80
            && decoded.PlayerId == 2 && decoded.Style == CharacterStyle.Female, "Appearance binds accepted connection and reliable envelope sequence");
        Assert(!AvatarRelayCodec.BindClientAppearance(appearance, 3, 4, 80, out decoded), "Cannot select another player's appearance");
        Assert(!AvatarRelayCodec.BindClientAppearance(appearance, 2, 5, 80, out decoded), "Cannot claim old connection generation");
        Assert(!AvatarRelayCodec.BindClientAppearance(appearance, 1, 4, 80, out decoded), "Client cannot claim host appearance");
        Assert(!AvatarRelayCodec.BindClientAppearance(encoded, 2, 4, 80, out decoded), "Client cannot inject relayed pose through appearance route");
        Assert(!AvatarRelayCodec.BindClientAppearance(AvatarRelayCodec.Encode(Appearance(2, 4, 999, CharacterStyle.Male)), 2, 4, 80, out decoded), "Client cannot choose appearance ordering sequence");
        AvatarRelayTracker tracker = new AvatarRelayTracker(); bool reset;
        Assert(!tracker.Apply(Pose(2, 4, 5), 1, out reset), "No rendering before appearance baseline");
        Assert(tracker.Apply(Appearance(2, 4, 80, CharacterStyle.Female), 1, out reset) && reset, "First appearance baseline");
        CharacterStyle style;
        Assert(tracker.TryGetAppearance(2, 4, out style) && style == CharacterStyle.Female, "Selected appearance cached before pose");
        Assert(tracker.Apply(Pose(2, 4, 5), 1, out reset) && !reset, "First pose follows appearance");
        Assert(!tracker.Apply(Pose(2, 4, 5), 1, out reset), "Duplicate pose");
        Assert(!tracker.Apply(Pose(2, 4, 4), 1, out reset), "Stale pose");
        Assert(!tracker.Apply(Appearance(2, 4, 79, CharacterStyle.Male), 1, out reset), "Stale appearance cannot revert choice");
        Assert(tracker.Apply(Appearance(2, 4, 81, CharacterStyle.Female), 1, out reset) && !reset, "Periodic baseline does not rebuild unchanged mesh");
        Assert(tracker.Apply(Appearance(2, 4, 82, CharacterStyle.Male), 1, out reset) && reset, "Live selection rebuilds rendered appearance");
        Assert(tracker.TryGetAppearance(2, 4, out style) && style == CharacterStyle.Male, "Live selection remembered");
        Assert(tracker.Apply(new AvatarRelayMessage { Kind = AvatarRelayKind.Remove, PlayerId = 2, ConnectionGeneration = 4 }, 1, out reset), "Remove live peer");
        Assert(!tracker.Apply(Pose(2, 4, 100), 1, out reset), "Tombstone prevents resurrection");
        Assert(!tracker.TryGetAppearance(2, 4, out style), "Removal invalidates cached appearance");
        Assert(!tracker.Apply(Appearance(2, 4, 1000, CharacterStyle.Female), 1, out reset), "Removed connection cannot resurrect through appearance");
        Assert(!tracker.Apply(Pose(2, 5, 1), 1, out reset), "Reconnect pose requires its own appearance baseline");
        Assert(tracker.Apply(Appearance(2, 5, 1, CharacterStyle.Female), 1, out reset) && reset, "Reconnect accepts selected appearance with newer generation");
        Assert(tracker.Apply(Pose(2, 5, 1), 1, out reset), "Reconnect pose after baseline");
        Assert(!tracker.Apply(Pose(2, 4, 200), 1, out reset), "Old generation cannot overwrite reconnect");
        Assert(!tracker.Apply(Appearance(2, 4, 200, CharacterStyle.Male), 1, out reset), "Old generation cannot change reconnected appearance");
        Assert(!tracker.Apply(Pose(1, 1, 1), 1, out reset), "Cannot render self");
        Assert(tracker.Apply(Appearance(3, 6, 1, CharacterStyle.Male), 1, out reset), "Second remote baseline");
        Assert(tracker.Apply(Pose(3, 6, 1), 1, out reset), "Second remote");
        Assert(tracker.Apply(Appearance(4, 7, UInt32.MaxValue, CharacterStyle.Female), 1, out reset), "Third remote baseline");
        Assert(tracker.Apply(Pose(4, 7, UInt32.MaxValue), 1, out reset), "Third remote");
        Assert(!tracker.Apply(Appearance(5, 8, 1, CharacterStyle.Male), 1, out reset), "Fourth remote baseline rejected");
        Assert(!tracker.Apply(Pose(5, 8, 1), 1, out reset), "Fourth remote pose rejected");
        Assert(tracker.Apply(Pose(4, 7, 0), 1, out reset), "Sequence wrap accepted");
        Assert(!tracker.Apply(Pose(4, 7, UInt32.MaxValue), 1, out reset), "Pre-wrap stale rejected");
        Assert(tracker.Apply(Appearance(4, 7, 0, CharacterStyle.Male), 1, out reset), "Appearance sequence wrap accepted independently");
        Assert(!tracker.Apply(Appearance(4, 7, UInt32.MaxValue, CharacterStyle.Female), 1, out reset), "Old wrapped appearance rejected");

        // A joining fourth player receives the host's selected style plus all
        // other players' accepted cached choices, without inventing defaults.
        AvatarRelayTracker lateJoin = new AvatarRelayTracker();
        Assert(lateJoin.Apply(Appearance(1, 7, 100, CharacterStyle.Female), 4, out reset), "Late join receives host selection");
        AvatarRelayMessage[] baseline = tracker.GetAppearances();
        Assert(baseline.Length == 3, "Host cache contains all three accepted choices");
        for (int i = 0; i < baseline.Length; i++)
            if (baseline[i].PlayerId != 4) Assert(lateJoin.Apply(baseline[i], 4, out reset), "Late join applies other player's appearance");
        Assert(lateJoin.TryGetAppearance(1, 7, out style) && style == CharacterStyle.Female, "Late join sees female host");
        Assert(lateJoin.TryGetAppearance(2, 5, out style) && style == CharacterStyle.Female, "Late join sees reconnect choice");
        Assert(lateJoin.TryGetAppearance(3, 6, out style) && style == CharacterStyle.Male, "Late join sees male peer");
        Assert(!lateJoin.TryGetAppearance(4, 7, out style), "Late join does not allocate own avatar");
        AvatarRemovalOutbox outbox = new AvatarRemovalOutbox();
        AvatarRelayMessage removal = new AvatarRelayMessage { Kind = AvatarRelayKind.Remove, PlayerId = 2, ConnectionGeneration = 5 };
        Assert(outbox.Queue(4, 7, removal), "Removal retained before attempting bounded transport enqueue");
        Assert(outbox.Pending(4, 7).Length == 1 && outbox.Pending(4, 7).Length == 1, "Failed send leaves removal available to retry");
        Assert(!lateJoin.Apply(Appearance(5, 9, 1, CharacterStyle.Male), 4, out reset), "Unremoved hidden peer still occupies the remote limit");
        AvatarRelayMessage retry = outbox.Pending(4, 7)[0];
        Assert(lateJoin.Apply(retry, 4, out reset), "Retried removal releases the remote avatar slot");
        outbox.Confirm(4, 7, retry);
        Assert(outbox.Pending(4, 7).Length == 0, "Successfully enqueued removal is no longer retried");
        Assert(lateJoin.Apply(Appearance(5, 9, 1, CharacterStyle.Male), 4, out reset), "New peer visible after removal retry");
        Assert(outbox.Queue(4, 7, removal), "Queue old recipient generation");
        Assert(outbox.Pending(4, 8).Length == 0, "Old pending removal cannot leak to reconnected recipient");
        Assert(outbox.Queue(4, 8, removal) && outbox.Pending(4, 7).Length == 0, "Recipient reconnect replaces old outbox generation");
        outbox.Confirm(4, 7, removal);
        Assert(outbox.Pending(4, 8).Length == 1, "Stale confirmation cannot clear a current retry");
        outbox.RemoveRecipient(4);
        Assert(outbox.Pending(4, 8).Length == 0, "Disconnected recipient releases retry storage");
        Assert(!outbox.Queue(2, 5, removal) && !outbox.Queue(4, 0, removal), "No self-removal or unbound recipient");
        AvatarRemovalOutbox capped = new AvatarRemovalOutbox();
        bool allStored = true;
        for (ulong actor = 10; actor < 138; actor++)
            allStored &= capped.Queue(2, 1, new AvatarRelayMessage { Kind = AvatarRelayKind.Remove, PlayerId = actor, ConnectionGeneration = actor });
        Assert(allStored && capped.Pending(2, 1).Length == 128, "Retry memory bounded to session identity history");
        Assert(!capped.Queue(2, 1, new AvatarRelayMessage { Kind = AvatarRelayKind.Remove, PlayerId = 138, ConnectionGeneration = 138 }), "Retry history overflow fails explicitly");
        Assert(capped.Queue(3, 2, removal) && capped.Queue(4, 3, removal) && !capped.Queue(5, 4, removal), "No more than three retry recipients");
        capped.Clear();
        Assert(capped.Pending(2, 1).Length == 0, "Preview teardown clears critical retries");
        AvatarRelayTracker bounded = new AvatarRelayTracker();
        for (ulong id = 2; id < 130; id++)
            Assert(bounded.Apply(new AvatarRelayMessage { Kind = AvatarRelayKind.Remove, PlayerId = id, ConnectionGeneration = id }, 1, out reset), "Bounded tombstone insert");
        Assert(!bounded.Apply(Appearance(130, 130, 1, CharacterStyle.Male), 1, out reset), "Full history fails closed instead of evicting tombstones");
        Assert(!bounded.Apply(Appearance(2, 2, 1, CharacterStyle.Female), 1, out reset), "Old tombstone remains after history fills");
        Console.WriteLine("Passed " + assertions + " avatar relay assertions.");
        return 0;
    }
}
