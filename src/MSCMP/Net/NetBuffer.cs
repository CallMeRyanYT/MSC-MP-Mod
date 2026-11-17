using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace MSCMP.Net
{
    [StructLayout(LayoutKind.Explicit)]
    internal struct FloatBits
    {
        [FieldOffset(0)] public float F;
        [FieldOffset(0)] public uint U;
    }

    /// <summary>Little-endian binary writer that reuses its buffer.</summary>
    internal sealed class NetWriter
    {
        byte[] buf;
        int len;

        public NetWriter(int capacity = 256) { buf = new byte[capacity]; }

        public int Length { get { return len; } }
        public byte[] Buffer { get { return buf; } }

        public NetWriter Reset() { len = 0; return this; }

        void Ensure(int extra)
        {
            int need = len + extra;
            if (need <= buf.Length) return;
            int size = buf.Length * 2;
            while (size < need) size *= 2;
            byte[] nb = new byte[size];
            System.Buffer.BlockCopy(buf, 0, nb, 0, len);
            buf = nb;
        }

        public byte[] ToArray()
        {
            byte[] r = new byte[len];
            System.Buffer.BlockCopy(buf, 0, r, 0, len);
            return r;
        }

        public void Byte(byte v) { Ensure(1); buf[len++] = v; }
        public void Bool(bool v) { Byte(v ? (byte)1 : (byte)0); }
        public void SByte(sbyte v) { Byte(unchecked((byte)v)); }

        public void UShort(ushort v) { Ensure(2); buf[len++] = (byte)v; buf[len++] = (byte)(v >> 8); }
        public void Short(short v) { UShort(unchecked((ushort)v)); }

        public void UInt(uint v)
        {
            Ensure(4);
            buf[len++] = (byte)v; buf[len++] = (byte)(v >> 8); buf[len++] = (byte)(v >> 16); buf[len++] = (byte)(v >> 24);
        }
        public void Int(int v) { UInt(unchecked((uint)v)); }

        public void ULong(ulong v) { UInt((uint)v); UInt((uint)(v >> 32)); }
        public void Long(long v) { ULong(unchecked((ulong)v)); }

        public void Float(float v) { FloatBits b = new FloatBits(); b.F = v; UInt(b.U); }

        /// <summary>Float packed into 16 bits over [-range, range].</summary>
        public void Half(float v, float range)
        {
            float n = Mathf.Clamp(v / range, -1f, 1f);
            Short((short)Mathf.RoundToInt(n * 32767f));
        }

        /// <summary>Unsigned 0..1 value in one byte.</summary>
        public void Unit8(float v) { Byte((byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f)); }

        public void Signed8(float v) { SByte((sbyte)Mathf.RoundToInt(Mathf.Clamp(v, -1f, 1f) * 127f)); }

        public void String(string s)
        {
            if (s == null) s = string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            int n = Math.Min(bytes.Length, ushort.MaxValue);
            UShort((ushort)n);
            Ensure(n);
            System.Buffer.BlockCopy(bytes, 0, buf, len, n);
            len += n;
        }

        public void Bytes(byte[] data, int offset, int count)
        {
            Int(count);
            Ensure(count);
            System.Buffer.BlockCopy(data, offset, buf, len, count);
            len += count;
        }

        public void Vector3(Vector3 v) { Float(v.x); Float(v.y); Float(v.z); }

        public void Quaternion(Quaternion q) { Float(q.x); Float(q.y); Float(q.z); Float(q.w); }

        /// <summary>Smallest-three quaternion compression (7 bytes).</summary>
        public void QuaternionPacked(Quaternion q)
        {
            float[] c = { q.x, q.y, q.z, q.w };
            int largest = 0;
            float max = Mathf.Abs(c[0]);
            for (int i = 1; i < 4; i++)
            {
                float a = Mathf.Abs(c[i]);
                if (a > max) { max = a; largest = i; }
            }
            float sign = c[largest] < 0 ? -1f : 1f;
            Byte((byte)largest);
            const float scale = 32767f / 0.70710678f;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                float v = Mathf.Clamp(c[i] * sign, -0.70710678f, 0.70710678f);
                Short((short)Mathf.RoundToInt(v * scale));
            }
        }
    }

    /// <summary>Bounds-checked little-endian reader.</summary>
    internal sealed class NetReader
    {
        byte[] buf;
        int pos;
        int end;

        public NetReader() { }
        public NetReader(byte[] data, int offset, int count) { Set(data, offset, count); }

        public void Set(byte[] data, int offset, int count)
        {
            buf = data;
            pos = offset;
            end = offset + count;
        }

        public int Remaining { get { return end - pos; } }
        public int Position { get { return pos; } }

        void Need(int n)
        {
            if (pos + n > end) throw new FormatException("Message truncated");
        }

        public byte Byte() { Need(1); return buf[pos++]; }
        public bool Bool() { return Byte() != 0; }
        public sbyte SByte() { return unchecked((sbyte)Byte()); }

        public ushort UShort()
        {
            Need(2);
            ushort v = (ushort)(buf[pos] | (buf[pos + 1] << 8));
            pos += 2;
            return v;
        }
        public short Short() { return unchecked((short)UShort()); }

        public uint UInt()
        {
            Need(4);
            uint v = (uint)(buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24));
            pos += 4;
            return v;
        }
        public int Int() { return unchecked((int)UInt()); }

        public ulong ULong() { ulong lo = UInt(); ulong hi = UInt(); return lo | (hi << 32); }
        public long Long() { return unchecked((long)ULong()); }

        public float Float()
        {
            FloatBits b = new FloatBits();
            b.U = UInt();
            float f = b.F;
            if (float.IsNaN(f) || float.IsInfinity(f)) throw new FormatException("Invalid float");
            return f;
        }

        public float Half(float range) { return Short() / 32767f * range; }
        public float Unit8() { return Byte() / 255f; }
        public float Signed8() { return SByte() / 127f; }

        public string String()
        {
            int n = UShort();
            Need(n);
            string s = Encoding.UTF8.GetString(buf, pos, n);
            pos += n;
            return s;
        }

        public byte[] Bytes(int maxLength)
        {
            int n = Int();
            if (n < 0 || n > maxLength) throw new FormatException("Invalid byte block length " + n);
            Need(n);
            byte[] r = new byte[n];
            System.Buffer.BlockCopy(buf, pos, r, 0, n);
            pos += n;
            return r;
        }

        public Vector3 Vector3() { return new Vector3(Float(), Float(), Float()); }

        public Quaternion Quaternion()
        {
            Quaternion q = new Quaternion(Float(), Float(), Float(), Float());
            return Normalize(q);
        }

        public Quaternion QuaternionPacked()
        {
            int largest = Byte();
            if (largest > 3) throw new FormatException("Invalid quaternion index");
            const float scale = 0.70710678f / 32767f;
            float[] c = new float[4];
            float sum = 0f;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                c[i] = Short() * scale;
                sum += c[i] * c[i];
            }
            c[largest] = Mathf.Sqrt(Mathf.Max(0f, 1f - sum));
            return Normalize(new Quaternion(c[0], c[1], c[2], c[3]));
        }

        static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 1e-6f) return UnityEngine.Quaternion.identity;
            return new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }
    }
}
