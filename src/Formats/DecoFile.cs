using System.Text.Json;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>decoration.7dt: byte version(6), int count, then 17-byte records
/// [u64 packed position][f32 realYPos][u32 BlockValue][u8 DecoState]. Worked on in place as raw bytes (160k+ records).</summary>
public static class DecoIO
{
    public const int Rec = 17;
    public static readonly string[] StateNames = { "Generated (active)", "Generated (inactive)", "Dynamic" };

    public sealed class Row
    {
        public int I { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public float RealY { get; set; }
        public uint Raw { get; set; }
        public int State { get; set; }
        public int Block { get; set; }
        public string Name { get; set; } = "";
        public int Rot { get; set; }
        public int M1 { get; set; }
        public int M2 { get; set; }
        public int M3 { get; set; }
        public bool Child { get; set; }
        public bool Decal { get; set; }
    }

    static readonly Dictionary<string, (long len, long ticks, byte[] data)> cache = new();

    public static byte[] Load(SaveCtx ctx, string rel)
    {
        var fi = new FileInfo(ctx.Abs(rel));
        var key = ctx.Root + "|" + rel;
        lock (cache)
        {
            if (cache.TryGetValue(key, out var c) && c.len == fi.Length && c.ticks == fi.LastWriteTimeUtc.Ticks) return c.data;
        }
        var d = ctx.ReadAll(rel);
        Validate(d);
        lock (cache) cache[key] = (fi.Length, fi.LastWriteTimeUtc.Ticks, d);
        return d;
    }

    public static void Invalidate() { lock (cache) cache.Clear(); }

    public static int Count(byte[] d) => (d.Length - 5) / Rec;

    public static void Validate(byte[] d)
    {
        if (d.Length < 5) throw new ParseFail("File too short");
        if (d[0] != 6) throw new ParseFail("Unsupported version " + d[0]);
        int n = BitConverter.ToInt32(d, 1);
        if (n < 0 || 5L + (long)n * Rec != d.Length) throw new ParseFail($"Header count {n} does not match the file size – repair the file first.");
    }

    public static Row Get(byte[] d, int i, GameData g)
    {
        int o = 5 + i * Rec;
        ulong p = BitConverter.ToUInt64(d, o);
        uint raw = BitConverter.ToUInt32(d, o + 12);
        int id = (int)(raw & 0xFFFF);
        return new Row
        {
            I = i, X = (int)((p >> 32) & 0xFFFF) - 32768, Y = (int)((p >> 16) & 0xFFFF) - 32768, Z = (int)(p & 0xFFFF) - 32768,
            RealY = BitConverter.ToSingle(d, o + 8), Raw = raw, State = d[o + 16], Block = id, Name = g.BlockNames.TryGetValue(id, out var n) ? n : "block#" + id,
            Rot = (int)((raw >> 16) & 31), M3 = (int)((raw >> 21) & 1), M1 = (int)((raw >> 22) & 15), M2 = (int)((raw >> 26) & 15),
            Child = ((raw >> 30) & 1) != 0, Decal = (raw >> 31) != 0,
        };
    }

    public static void Put(byte[] d, int i, Row r)
    {
        int o = 5 + i * Rec;
        ulong p = ((ulong)((r.X + 32768) & 0xFFFF) << 32) | ((ulong)((r.Y + 32768) & 0xFFFF) << 16) | (ulong)((r.Z + 32768) & 0xFFFF);
        BitConverter.GetBytes(p).CopyTo(d, o);
        BitConverter.GetBytes(r.RealY).CopyTo(d, o + 8);
        BitConverter.GetBytes(r.Raw).CopyTo(d, o + 12);
        d[o + 16] = (byte)r.State;
    }

    public static object Summary(byte[] d, GameData g)
    {
        int n = Count(d);
        var per = new Dictionary<int, int[]>();
        for (int i = 0; i < n; i++)
        {
            int o = 5 + i * Rec;
            int id = BitConverter.ToUInt16(d, o + 12);
            if (!per.TryGetValue(id, out var a)) per[id] = a = new int[4];
            a[0]++; int st = d[o + 16]; if (st < 3) a[1 + st]++;
        }
        return new
        {
            total = n,
            blocks = per.OrderByDescending(kv => kv.Value[0]).Select(kv => new { id = kv.Key, name = g.BlockNames.TryGetValue(kv.Key, out var nm) ? nm : "block#" + kv.Key, count = kv.Value[0], active = kv.Value[1], inactive = kv.Value[2], dynamic = kv.Value[3] }),
        };
    }

    public static object Page(byte[] d, GameData g, int offset, int limit, string? block, int state)
    {
        int n = Count(d);
        HashSet<int>? ids = null; int idExact = -1;
        if (!string.IsNullOrWhiteSpace(block))
        {
            if (int.TryParse(block, out var bid)) idExact = bid;
            else ids = g.BlockNames.Where(kv => kv.Value.Contains(block, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToHashSet();
        }
        var rows = new List<Row>(); int matched = 0;
        for (int i = 0; i < n; i++)
        {
            int o = 5 + i * Rec;
            int id = BitConverter.ToUInt16(d, o + 12);
            if (idExact >= 0 && id != idExact) continue;
            if (ids != null && !ids.Contains(id)) continue;
            if (state >= 0 && d[o + 16] != state) continue;
            if (matched >= offset && rows.Count < limit) rows.Add(Get(d, i, g));
            matched++;
        }
        return new { total = n, matched, offset, rows };
    }

    public sealed class Apply
    {
        public List<Row> Updates { get; set; } = new();
        public List<int> Deletes { get; set; } = new();
        public string? Bulk { get; set; }      // deleteBlock | replaceBlock | setState
        public int Block { get; set; }
        public int To { get; set; }
        public int State { get; set; }
        public int FromState { get; set; } = -1;
    }

    public static (byte[] data, string message) Run(byte[] src, Apply a)
    {
        Validate(src);
        var d = (byte[])src.Clone();
        int n = Count(d);
        string msg = "";
        foreach (var u in a.Updates) if (u.I >= 0 && u.I < n) Put(d, u.I, u);
        var del = new HashSet<int>(a.Deletes.Where(i => i >= 0 && i < n));
        int touched = 0;
        if (a.Bulk != null)
        {
            for (int i = 0; i < n; i++)
            {
                int o = 5 + i * Rec;
                int id = BitConverter.ToUInt16(d, o + 12);
                if (id != a.Block) continue;
                if (a.FromState >= 0 && d[o + 16] != a.FromState) continue;
                if (a.Bulk == "deleteBlock") { del.Add(i); touched++; }
                else if (a.Bulk == "replaceBlock")
                {
                    uint raw = BitConverter.ToUInt32(d, o + 12);
                    raw = (raw & ~0xFFFFu) | (uint)(a.To & 0xFFFF);
                    BitConverter.GetBytes(raw).CopyTo(d, o + 12); touched++;
                }
                else if (a.Bulk == "setState") { d[o + 16] = (byte)a.State; touched++; }
            }
            msg = a.Bulk switch { "deleteBlock" => $"Marked {touched:N0} decorations for deletion", "replaceBlock" => $"Changed the block type of {touched:N0} decorations", _ => $"Set the state of {touched:N0} decorations" };
        }
        byte[] outp;
        if (del.Count == 0) outp = d;
        else
        {
            outp = new byte[5 + (n - del.Count) * Rec];
            outp[0] = 6;
            int w = 0;
            for (int i = 0; i < n; i++) { if (del.Contains(i)) continue; Buffer.BlockCopy(d, 5 + i * Rec, outp, 5 + w * Rec, Rec); w++; }
            BitConverter.GetBytes(w).CopyTo(outp, 1);
        }
        msg = (msg.Length > 0 ? msg + "; " : "") + $"{a.Updates.Count} edited, {del.Count:N0} removed, {Count(outp):N0} decorations remain";
        return (outp, msg);
    }
}

/// <summary>multiblocks.7dt: byte version(6), int count, 17-byte records [vec3i pos][u32 BlockValue][u8 tracking flags].</summary>
public sealed class MultiBlockRec
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public uint Raw { get; set; }
    public int Flags { get; set; }   // 1 POI multiblock, 2 cross-chunk, 4 oversized, 8 terrain aligned

    public static List<MultiBlockRec> Parse(byte[] d)
    {
        if (d.Length < 5 || d[0] != 6) throw new ParseFail("Unsupported or damaged multiblock file");
        int n = BitConverter.ToInt32(d, 1);
        if (n < 0 || 5L + (long)n * 17 != d.Length) throw new ParseFail("Record count does not match the file size – repair the file first.");
        var l = new List<MultiBlockRec>(n);
        for (int i = 0; i < n; i++)
        {
            int o = 5 + i * 17;
            l.Add(new MultiBlockRec { X = BitConverter.ToInt32(d, o), Y = BitConverter.ToInt32(d, o + 4), Z = BitConverter.ToInt32(d, o + 8), Raw = BitConverter.ToUInt32(d, o + 12), Flags = d[o + 16] });
        }
        return l;
    }

    public static byte[] Build(List<MultiBlockRec> l)
    {
        var d = new byte[5 + l.Count * 17]; d[0] = 6; BitConverter.GetBytes(l.Count).CopyTo(d, 1);
        for (int i = 0; i < l.Count; i++)
        {
            int o = 5 + i * 17; var r = l[i];
            BitConverter.GetBytes(r.X).CopyTo(d, o); BitConverter.GetBytes(r.Y).CopyTo(d, o + 4); BitConverter.GetBytes(r.Z).CopyTo(d, o + 8);
            BitConverter.GetBytes(r.Raw).CopyTo(d, o + 12); d[o + 16] = (byte)r.Flags;
        }
        return d;
    }
}
