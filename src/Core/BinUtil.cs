using System.IO.Compression;
using System.Text;

namespace SaveDoctor.Core;

/// <summary>Little helpers that mirror the game's PooledBinaryReader/Writer conventions (.NET BinaryReader semantics).</summary>
public static class BinUtil
{
    public static readonly UTF8Encoding Utf8 = new(false, false);

    public static BinaryReader Reader(byte[] data, int offset = 0, int count = -1)
    {
        if (count < 0) count = data.Length - offset;
        return new BinaryReader(new MemoryStream(data, offset, count, false), Utf8, false);
    }

    public static long Pos(this BinaryReader r) => r.BaseStream.Position;
    public static long Len(this BinaryReader r) => r.BaseStream.Length;
    public static long Left(this BinaryReader r) => r.BaseStream.Length - r.BaseStream.Position;
    public static bool AtEnd(this BinaryReader r) => r.BaseStream.Position >= r.BaseStream.Length;

    /// <summary>Make sure n bytes are available, otherwise raise a ParseFail with a useful position.</summary>
    public static void Need(this BinaryReader r, long n, string what = "data")
    {
        if (n < 0 || r.Left() < n) throw new ParseFail($"Unexpected end of file reading {what} (need {n} bytes, {r.Left()} left)", r.Pos());
    }

    public static string Str(this BinaryReader r)
    {
        long start = r.Pos();
        int len = r.Read7(); // validated length
        if (len < 0 || len > r.Left()) throw new ParseFail($"Invalid string length {len}", start);
        if (len == 0) return "";
        var b = r.ReadBytes(len);
        return Utf8.GetString(b);
    }

    public static int Read7(this BinaryReader r)
    {
        int num = 0, shift = 0;
        while (shift < 35)
        {
            byte b = r.ReadByte();
            num |= (b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0) return num;
        }
        throw new ParseFail("Illegal 7-bit encoded int", r.Pos());
    }

    public static void Write7(this BinaryWriter w, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80) { w.Write((byte)(v | 0x80)); v >>= 7; }
        w.Write((byte)v);
    }

    public static void Str(this BinaryWriter w, string s)
    {
        s ??= "";
        var b = Utf8.GetBytes(s);
        w.Write7(b.Length);
        w.Write(b);
    }

    public static (int X, int Y, int Z) Vec3i(this BinaryReader r) => (r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
    public static (float X, float Y, float Z) Vec3(this BinaryReader r) => (r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    public static (float X, float Y) Vec2(this BinaryReader r) => (r.ReadSingle(), r.ReadSingle());

    public static byte[] Blob(this BinaryReader r, string what = "blob")
    {
        long start = r.Pos();
        int n = r.ReadInt32();
        if (n <= 0) return Array.Empty<byte>();
        if (n > r.Left()) throw new ParseFail($"{what}: length {n} exceeds remaining {r.Left()} bytes", start);
        return r.ReadBytes(n);
    }

    public static void WriteBlob(this BinaryWriter w, byte[]? b)
    {
        b ??= Array.Empty<byte>();
        w.Write(b.Length);
        w.Write(b);
    }

    public static string Hex(byte[] b, int max = 64)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < b.Length && i < max; i++) sb.Append(b[i].ToString("x2"));
        if (b.Length > max) sb.Append('…');
        return sb.ToString();
    }

    public static byte[] FromHex(string s)
    {
        s = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s.Length % 2 != 0) throw new FormatException("Odd number of hex digits");
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    /// <summary>Raw deflate (no zlib header) – what the game's Noemax DeflateStream writes for chunks.</summary>
    public static byte[] Inflate(byte[] data, int offset, int count, out bool complete, out string? error, int limit = 64 * 1024 * 1024)
    {
        using var inp = new MemoryStream(data, offset, count, false);
        using var ds = new DeflateStream(inp, CompressionMode.Decompress);
        using var outp = new MemoryStream();
        var buf = new byte[16384];
        complete = false; error = null;
        try
        {
            while (true)
            {
                int n = ds.Read(buf, 0, buf.Length);
                if (n <= 0) { complete = true; break; }
                outp.Write(buf, 0, n);
                if (outp.Length > limit) { error = "Decompressed data exceeds sanity limit"; break; }
            }
        }
        catch (Exception ex) { error = ex.Message; }
        return outp.ToArray();
    }

    public static byte[] Deflate(byte[] data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var outp = new MemoryStream();
        using (var ds = new DeflateStream(outp, level, true)) ds.Write(data, 0, data.Length);
        return outp.ToArray();
    }

    public static bool SameBytes(byte[] a, byte[] b)
        => a.Length == b.Length && a.AsSpan().SequenceEqual(b);

    /// <summary>First index where two arrays differ (or min length when one is a prefix), -1 if equal.</summary>
    public static long FirstDiff(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return a.Length == b.Length ? -1 : n;
    }

    public static string Size(long n)
    {
        if (n < 1024) return n + " B";
        if (n < 1024 * 1024) return (n / 1024.0).ToString("0.#") + " KB";
        return (n / 1024.0 / 1024.0).ToString("0.##") + " MB";
    }
}
