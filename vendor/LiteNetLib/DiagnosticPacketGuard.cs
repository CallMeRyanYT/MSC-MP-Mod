namespace LiteNetLib
{
    // Local integration patch, not upstream code. See PATCHES.md.
    internal static class DiagnosticPacketGuard
    {
        internal static bool IsAllowed(byte[] data, int count)
        {
            if (data == null || count < 1 || count > 512 || count > data.Length || (data[0] & 128) != 0)
                return false;
            int type = data[0];
            if (type > 10) return false;
            if (type >= 1 && type <= 7 && (count < 3 || (data[2] & 128) != 0)) return false;
            if ((type == 4 || type == 5) && count != 11) return false;
            if ((type == 6 || type == 7) && count != 3) return false;
            if (type == 8 && count < 13) return false;
            if (type == 9 && count != 9) return false;
            if (type == 10 && count < 9) return false;
            return true;
        }
    }
}
