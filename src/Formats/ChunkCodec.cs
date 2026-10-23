using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class LayerRef
{
    public int Index;              // 0..63 (each layer = 4 blocks of height)
    public bool HasLower;          // 1024 byte array present
    public byte LowerSame;         // value when !HasLower
    public bool HasUpper;          // 3072 byte array present
    public int LowerOffset = -1;   // offset of the 1024 bytes inside the inflated buffer
    public int UpperOffset = -1;   // offset of the 3072 bytes
}

public sealed class ChunkInfo
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public long ErrorOffset { get; set; } = -1;
    public uint Version { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public ulong SavedTicks { get; set; }
    public int CompressedSize { get; set; }
    public int InflatedSize { get; set; }
    public int LayerCount { get; set; }
    public int SolidBlocks { get; set; }
    public int Entities { get; set; } = -1;
    public int TileEntities { get; set; } = -1;
    public bool Deep { get; set; }                    // structure verified up to the entity section
    public bool Full { get; set; }                    // every byte of the chunk accounted for
    public string? TailNote { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public List<(int cls, float x, float y, float z)> EntList { get; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public List<(int block, int x, int y, int z)> TeList { get; } = new();
    public Dictionary<int, int>? Census { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public List<LayerRef> Layers { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public int LayersStart { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int LayersEnd { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public byte[]? Inflated { get; set; }
}

/// <summary>Parser for the chunk payload stored inside .7rg region files (Chunk.read in Assembly-CSharp.dll, version 47).</summary>
public static class ChunkCodec
{
    public const int CurrentVersion = 47;
    public static readonly byte[] Magic = { (byte)'t', (byte)'t', (byte)'c', 0 };

    /// <param name="blob">The chunk data exactly as stored in the region file: "ttc\0", uint version, raw-deflate stream.</param>
    public static ChunkInfo Inspect(byte[] blob, bool census, GameData? g = null)
    {
        var ci = new ChunkInfo { CompressedSize = blob.Length };
        try
        {
            if (blob.Length < 12) { ci.Error = "Chunk record is shorter than its header"; return ci; }
            if (!(blob[0] == 't' && blob[1] == 't' && blob[2] == 'c' && blob[3] == 0)) { ci.Error = "Wrong chunk header (no 'ttc' signature)"; ci.ErrorOffset = 0; return ci; }
            ci.Version = BitConverter.ToUInt32(blob, 4);
            if (ci.Version < 32 || ci.Version > 200) { ci.Error = $"Implausible chunk version {ci.Version}"; ci.ErrorOffset = 4; return ci; }
            var raw = BinUtil.Inflate(blob, 8, blob.Length - 8, out bool complete, out string? err);
            ci.InflatedSize = raw.Length;
            ci.Inflated = raw;
            if (!complete)
            {
                ci.Error = "Compressed data is broken: " + (err ?? "unexpected end of stream") + " (inflated " + raw.Length + " bytes before failing)";
                return ci;
            }
            if (raw.Length < 32) { ci.Error = "Decompressed chunk is far too small (" + raw.Length + " bytes)"; return ci; }

            using var r = BinUtil.Reader(raw);
            ci.X = r.ReadInt32(); ci.Y = r.ReadInt32(); ci.Z = r.ReadInt32();
            ci.SavedTicks = r.ReadUInt64();
            if (ci.Version != CurrentVersion)
            {
                // other versions: coordinates + inflate checked, layer format may differ
                ci.Ok = true;
                return ci;
            }
            ci.LayersStart = (int)r.Pos();
            var census_ = census ? new Dictionary<int, int>() : null;
            int solid = 0;
            for (int i = 0; i < 64; i++)
            {
                long at = r.Pos();
                int flag = r.ReadByte();
                if (flag > 1) throw new ParseFail($"Layer {i}: invalid presence flag {flag}", at);
                if (flag == 0) continue;
                var lr = new LayerRef { Index = i };
                at = r.Pos();
                int f2 = r.ReadByte();
                if (f2 > 1) throw new ParseFail($"Layer {i}: invalid lower-bits flag {f2}", at);
                if (f2 == 1) { r.Need(1024, "layer lower bits"); lr.HasLower = true; lr.LowerOffset = (int)r.Pos(); r.BaseStream.Seek(1024, SeekOrigin.Current); }
                else lr.LowerSame = r.ReadByte();
                at = r.Pos();
                int f3 = r.ReadByte();
                if (f3 > 1) throw new ParseFail($"Layer {i}: invalid upper-bits flag {f3}", at);
                if (f3 == 1) { r.Need(3072, "layer upper bits"); lr.HasUpper = true; lr.UpperOffset = (int)r.Pos(); r.BaseStream.Seek(3072, SeekOrigin.Current); }
                ci.Layers.Add(lr);
                ci.LayerCount++;
                if (census_ != null || true)
                {
                    for (int c = 0; c < 1024; c++)
                    {
                        int id = BlockIdAt(raw, lr, c);
                        if (id != 0) { solid++; if (census_ != null) census_[id] = census_.GetValueOrDefault(id) + 1; }
                    }
                }
            }
            ci.LayersEnd = (int)r.Pos();
            ci.SolidBlocks = solid;
            ci.Census = census_;

            // stability channel (1 byte values)
            SkipChannel(r, 1, "stability");
            r.Need(256 + 256 + 32 + 256 + 1536 + 2, "terrain arrays");
            r.BaseStream.Seek(256 + 256 + 32 + 256 + 1536 + 2, SeekOrigin.Current);
            long cdAt = r.Pos();
            int customCount = r.ReadUInt16();
            for (int i = 0; i < customCount; i++)
            {
                r.Str(); r.ReadUInt64(); r.ReadBoolean();
                int dl = r.ReadUInt16();
                r.Need(dl, "custom data");
                r.BaseStream.Seek(dl, SeekOrigin.Current);
            }
            r.Need(768, "normals");
            r.BaseStream.Seek(768, SeekOrigin.Current);
            SkipChannel(r, 1, "density");
            SkipChannel(r, 1, "light");
            SkipChannel(r, 2, "damage");
            SkipChannel(r, 6, "textures");
            SkipChannel(r, 2, "water");
            r.Need(1 + 4, "entity header");
            r.ReadBoolean();
            int ents = r.ReadInt32();
            if (ents < 0 || ents > 20000) throw new ParseFail("Implausible entity count " + ents, r.Pos() - 4);
            ci.Entities = ents;
            ci.Deep = true;
            ci.Ok = true;
            try { TailCheck(r, ci, ents, g); }
            catch (Unverifiable u) { ci.TailNote = u.Message; }
            catch (ParseFail pf) { ci.Ok = false; ci.Error = "Entity / tile-entity data is garbled: " + pf.Message; ci.ErrorOffset = pf.Offset; }
            catch (EndOfStreamException) { ci.Ok = false; ci.Error = "Entity / tile-entity data ends unexpectedly (truncated chunk)"; }
        }
        catch (ParseFail pf) { ci.Error = pf.Message; ci.ErrorOffset = pf.Offset; }
        catch (EndOfStreamException) { ci.Error = "Chunk data ends unexpectedly (truncated)"; }
        catch (Exception ex) { ci.Error = ex.GetType().Name + ": " + ex.Message; }
        return ci;
    }


    sealed class Unverifiable : Exception { public Unverifiable(string m) : base(m) { } }

    static readonly HashSet<int> KnownTileEntityTypes = new() { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 25, 27 };

    /// <summary>Continue after the entity count: walk entities, tile entities and the trailing sections, as far as the formats are known.</summary>
    static void TailCheck(BinaryReader r, ChunkInfo ci, int ents, GameData? g)
    {
        if (g == null) throw new Unverifiable("no game data available");
        for (int i = 0; i < ents; i++)
        {
            try { var ent = Ecd.Read(r, g, false); ci.EntList.Add((ent.EntityClass, ent.PosX, ent.PosY, ent.PosZ)); }
            catch (ParseFail pf) when (pf.Message.Contains("not supported") || pf.Message.StartsWith("Unsupported"))
            { throw new Unverifiable(pf.Message); }
        }
        r.Need(4, "tile entity count");
        int te = r.ReadInt32();
        if (te < 0 || te > 100000) throw new ParseFail("Implausible tile entity count " + te, r.Pos() - 4);
        ci.TileEntities = te;
        for (int i = 0; i < te; i++)
        {
            long at = r.Pos();
            r.Need(4, "tile entity type");
            int type = r.ReadInt32();
            if (!KnownTileEntityTypes.Contains(type)) throw new ParseFail($"Tile entity #{i} has unknown type {type} (removed mod, or garbage)", at);
            if (type != 25) throw new Unverifiable($"tile entity type {type} is not deep-checked");
            r.Need(2 + 12 + 8 + 2 + 4, "composite tile entity header");
            int rv = r.ReadUInt16();
            if (rv < 18 || rv > 40) throw new Unverifiable("composite tile entity version " + rv);
            var lpos = r.Vec3i();
            r.ReadUInt64();
            r.ReadUInt16();
            long m = r.Pos();
            uint size = r.ReadUInt32();
            if (size < 10 || m + size > r.Len()) throw new ParseFail($"Tile entity #{i}: size marker {size} is impossible", m);
            long end = m + size;
            int blockId = r.ReadInt32();
            if (blockId <= 0 || blockId > 65535) throw new ParseFail($"Tile entity #{i}: block id {blockId} is invalid", m + 4);
            ci.TeList.Add((blockId, lpos.X, lpos.Y, lpos.Z));
            PlatformId.Read(r);
            int nf = r.ReadByte();
            for (int f = 0; f < nf; f++)
            {
                r.Need(8, "tile entity feature");
                r.ReadInt32();
                long fm = r.Pos();
                uint fs = r.ReadUInt32();
                if (fs < 4 || fm + fs > end) throw new ParseFail($"Tile entity #{i}: feature size {fs} overruns its record", fm);
                r.BaseStream.Position = fm + fs;
            }
            if (r.Pos() != end) throw new ParseFail($"Tile entity #{i}: payload does not end where its size marker says", at);
        }
        // trailing sections
        r.Need(1, "chunk tail");
        if (r.ReadBoolean()) { r.Need(32, "chunk tail"); r.BaseStream.Seek(32, SeekOrigin.Current); }
        for (int k = 0; k < 3; k++)
        {
            r.Need(1, "volume list");
            int n = r.ReadByte();
            r.Need(n * 4L, "volume list");
            r.BaseStream.Seek(n * 4L, SeekOrigin.Current);
        }
        r.Need(2, "inside devices");
        int dev = r.ReadInt16();
        if (dev < 0) throw new ParseFail("Negative inside-device count", r.Pos() - 2);
        int left = 0;
        for (int i = 0; i < dev; i++)
        {
            if (left == 0)
            {
                r.Need(3, "inside devices");
                r.ReadByte(); r.ReadByte(); left = r.ReadByte();
                if (left == 0) throw new ParseFail("Empty inside-device group", r.Pos() - 1);
            }
            r.Need(1, "inside devices");
            r.ReadByte(); left--;
        }
        r.Need(1 + 2, "trigger data");
        r.ReadBoolean();
        int nt = r.ReadInt16();
        if (nt < 0) throw new ParseFail("Negative trigger count", r.Pos() - 2);
        for (int i = 0; i < nt; i++)
        {
            r.Need(12 + 2, "trigger data");
            r.Vec3i();
            int ver = r.ReadUInt16();
            if (ver >= 2) r.ReadByte();
            for (int k = 0; k < 3; k++) { r.Need(1, "trigger list"); int n = r.ReadByte(); r.Need(n, "trigger list"); r.BaseStream.Seek(n, SeekOrigin.Current); }
            if (ver >= 3) r.ReadBoolean();
            if (ver >= 4) r.ReadBoolean();
            if (ver >= 5) r.ReadBoolean();
        }
        if (r.Left() != 0) throw new ParseFail($"{r.Left()} unexpected bytes at the end of the chunk", r.Pos());
        ci.Full = true;
    }

    static void SkipChannel(BinaryReader r, int bytesPerVal, string name)
    {
        for (int i = 0; i < 64; i++)
        {
            long at = r.Pos();
            r.Need(1, name);
            int flag = r.ReadByte();
            if (flag > 1) throw new ParseFail($"{name} channel layer {i}: invalid flag {flag}", at);
            for (int j = 0; j < bytesPerVal; j++)
            {
                if (flag == 0) { r.Need(1024, name); r.BaseStream.Seek(1024, SeekOrigin.Current); }
                else { r.Need(1, name); r.BaseStream.Seek(1, SeekOrigin.Current); }
            }
        }
    }

    public static int BlockIdAt(byte[] raw, LayerRef lr, int cell)
    {
        int id = lr.HasLower ? raw[lr.LowerOffset + cell] : lr.LowerSame;
        if (lr.HasUpper) id |= raw[lr.UpperOffset + cell * 3] << 8;
        return id & 0xFFFF;
    }

    /// <summary>Replace block type <paramref name="from"/> with <paramref name="to"/> in the chunk. Returns the new region blob or null if nothing changed.</summary>
    public static byte[]? ReplaceBlocks(byte[] blob, ChunkInfo ci, Func<int, int?> map, out int changed)
    {
        changed = 0;
        if (ci.Inflated == null || !ci.Ok || ci.Version != CurrentVersion) return null;
        var raw = (byte[])ci.Inflated.Clone();
        // decode every layer to full arrays so representation changes are possible
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(raw, 0, ci.LayersStart);
        var byIdx = ci.Layers.ToDictionary(l => l.Index);
        for (int i = 0; i < 64; i++)
        {
            if (!byIdx.TryGetValue(i, out var lr)) { w.Write(false); continue; }
            var lower = new byte[1024]; var upper = new byte[3072];
            bool hasUpper = lr.HasUpper;
            for (int c = 0; c < 1024; c++)
            {
                lower[c] = lr.HasLower ? raw[lr.LowerOffset + c] : lr.LowerSame;
                if (hasUpper) { upper[c * 3] = raw[lr.UpperOffset + c * 3]; upper[c * 3 + 1] = raw[lr.UpperOffset + c * 3 + 1]; upper[c * 3 + 2] = raw[lr.UpperOffset + c * 3 + 2]; }
            }
            for (int c = 0; c < 1024; c++)
            {
                int id = lower[c] | (upper[c * 3] << 8);
                var nid = map(id);
                if (nid.HasValue && nid.Value != id)
                {
                    lower[c] = (byte)nid.Value;
                    if (nid.Value > 255 || hasUpper) { hasUpper = true; upper[c * 3] = (byte)(nid.Value >> 8); }
                    changed++;
                }
            }
            bool allSame = true;
            for (int c = 1; c < 1024 && allSame; c++) if (lower[c] != lower[0]) allSame = false;
            bool upperZero = true;
            if (hasUpper) for (int c = 0; c < 3072 && upperZero; c++) if (upper[c] != 0) upperZero = false;
            w.Write(true);
            if (allSame) { w.Write(false); w.Write(lower[0]); } else { w.Write(true); w.Write(lower); }
            if (hasUpper && !upperZero) { w.Write(true); w.Write(upper); } else w.Write(false);
        }
        if (changed == 0) return null;
        w.Write(raw, ci.LayersEnd, raw.Length - ci.LayersEnd);
        w.Flush();
        var comp = BinUtil.Deflate(ms.ToArray(), System.IO.Compression.CompressionLevel.Optimal);
        var outp = new byte[8 + comp.Length];
        Array.Copy(blob, 0, outp, 0, 8);
        Array.Copy(comp, 0, outp, 8, comp.Length);
        return outp;
    }
}
