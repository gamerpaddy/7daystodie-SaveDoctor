using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>One byte-plane channel (stability, density, light, damage, textures, water): 64 layers of 4 height units, <c>Bpv</c> planes each.</summary>
public sealed class ChanDoc
{
    public int Bpv;
    public byte[][] Planes;          // [bpv][65536], index = y*256 + z*16 + x
    public bool[] Same = new bool[64]; // layer was stored in "one value" form

    public ChanDoc(int bpv)
    {
        Bpv = bpv;
        Planes = new byte[bpv][];
        for (int i = 0; i < bpv; i++) Planes[i] = new byte[65536];
    }

    public static ChanDoc Read(BinaryReader r, int bpv, string name)
    {
        var c = new ChanDoc(bpv);
        for (int l = 0; l < 64; l++)
        {
            long at = r.Pos();
            r.Need(1, name);
            int flag = r.ReadByte();
            if (flag > 1) throw new ParseFail($"{name} channel layer {l}: invalid flag {flag}", at);
            c.Same[l] = flag == 1;
            for (int j = 0; j < bpv; j++)
            {
                if (flag == 0) { r.Need(1024, name); r.Read(c.Planes[j], l * 1024, 1024); }
                else { r.Need(1, name); byte v = r.ReadByte(); Array.Fill(c.Planes[j], v, l * 1024, 1024); }
            }
        }
        return c;
    }

    public void Write(BinaryWriter w)
    {
        for (int l = 0; l < 64; l++)
        {
            bool uniform = true;
            if (Same[l])
                for (int j = 0; j < Bpv && uniform; j++)
                {
                    var p = Planes[j]; byte v = p[l * 1024];
                    for (int i = 1; i < 1024; i++) if (p[l * 1024 + i] != v) { uniform = false; break; }
                }
            if (Same[l] && uniform)
            {
                w.Write((byte)1);
                for (int j = 0; j < Bpv; j++) w.Write(Planes[j][l * 1024]);
            }
            else
            {
                w.Write((byte)0);
                for (int j = 0; j < Bpv; j++) w.Write(Planes[j], l * 1024, 1024);
            }
        }
    }

    public ChanDoc Clone()
    {
        var c = new ChanDoc(Bpv);
        for (int j = 0; j < Bpv; j++) Array.Copy(Planes[j], c.Planes[j], 65536);
        Array.Copy(Same, c.Same, 64);
        return c;
    }
}

public sealed class TeFeat
{
    public int Hash;
    public byte[] Payload = Array.Empty<byte>();
}

/// <summary>A tile entity record of a chunk. Composite ones (type 25) are split into header / features; other types are kept as opaque bytes.</summary>
public sealed class TeRec
{
    public int Type;
    public bool HasPos;               // X/Y/Z known (composite and simple legacy types)
    public int X, Y, Z;               // chunk-local position
    public int BlockId;               // composite only
    public byte[] Raw = Array.Empty<byte>();   // the record exactly as stored (starting with the type int)
    // composite pieces
    public byte[]? Head;              // type .. up to (excluding) the size marker
    public byte[]? BodyPre;           // after the marker: block id + owner + feature count
    public List<TeFeat>? Feats;
    public bool Dirty;

    public static TeRec ReadOne(BinaryReader r, byte[] raw, int i)
    {
            long recStart = r.Pos();
            int type = r.ReadInt32();
            var rec = new TeRec { Type = type };
            if (type == 18 || type == 20 || type == 21)
            {
                // legacy fixed-layout types: base (ushort 19, pos, ulong) + local version (ushort 18) + payload
                int bv = r.ReadUInt16();
                var p3 = r.Vec3i();
                r.ReadUInt64();
                int lv = r.ReadUInt16();
                if (bv != 19 || lv != 18) throw new ParseFail($"tile entity type {type} has unexpected versions {bv}/{lv}", recStart);
                if (type == 18) r.BaseStream.Seek(27, SeekOrigin.Current);
                else if (type == 20) r.BaseStream.Seek(12, SeekOrigin.Current);
                else PlatformId.Read(r);
                rec.HasPos = true; rec.X = p3.X; rec.Y = p3.Y; rec.Z = p3.Z;
                rec.Raw = new byte[r.Pos() - recStart];
                Array.Copy(raw, recStart, rec.Raw, 0, rec.Raw.Length);
                return rec;
            }
            if (type != 25) throw new ParseFail($"tile entity type {type} cannot be split without its format", recStart);
            int rv = r.ReadUInt16();
            if (rv < 18 || rv > 40) throw new ParseFail("composite tile entity version " + rv, recStart);
            var lp = r.Vec3i(); rec.X = lp.X; rec.Y = lp.Y; rec.Z = lp.Z;
            r.ReadUInt64(); r.ReadUInt16();
            long m = r.Pos();
            uint size = r.ReadUInt32();
            if (size < 10 || m + size > raw.Length) throw new ParseFail($"Tile entity #{i}: size marker {size} is impossible", m);
            long end = m + size;
            rec.HasPos = true;
            rec.Head = new byte[m - recStart];
            Array.Copy(raw, recStart, rec.Head, 0, rec.Head.Length);
            int bodyStart = (int)r.Pos();
            rec.BlockId = r.ReadInt32();
            PlatformId.Read(r);
            int nf = r.ReadByte();
            rec.BodyPre = new byte[r.Pos() - bodyStart];
            Array.Copy(raw, bodyStart, rec.BodyPre, 0, rec.BodyPre.Length);
            rec.Feats = new();
            for (int f = 0; f < nf; f++)
            {
                int hash = r.ReadInt32();
                long fm = r.Pos();
                uint fs = r.ReadUInt32();
                if (fs < 4 || fm + fs > end) throw new ParseFail($"Tile entity #{i}: feature size {fs} overruns its record", fm);
                var pl = new byte[fs - 4];
                Array.Copy(raw, fm + 4, pl, 0, pl.Length);
                rec.Feats.Add(new TeFeat { Hash = hash, Payload = pl });
                r.BaseStream.Position = fm + fs;
            }
            if (r.Pos() != end) throw new ParseFail($"Tile entity #{i}: payload does not end where its size marker says", recStart);
            rec.Raw = new byte[end - recStart];
            Array.Copy(raw, recStart, rec.Raw, 0, rec.Raw.Length);
        return rec;
    }

    /// <summary>A copy of this record placed at another chunk-local position (and, for composite records, bound to another block id).</summary>
    public TeRec Moved(int lx, int ly, int lz, int? newBlockId)
    {
        var raw = (byte[])Encode().Clone();
        BitConverter.GetBytes(lx).CopyTo(raw, 6); BitConverter.GetBytes(ly).CopyTo(raw, 10); BitConverter.GetBytes(lz).CopyTo(raw, 14);
        if (newBlockId.HasValue && Composite) BitConverter.GetBytes(newBlockId.Value).CopyTo(raw, Head!.Length + 4);
        using var r = BinUtil.Reader(raw);
        return ReadOne(r, raw, 0);
    }

    /// <summary>The owner stored in a composite tile entity (null when none).</summary>
    public PlatformId? GetOwner()
    {
        if (!Composite) return null;
        using var r = BinUtil.Reader(BodyPre!);
        r.ReadInt32();
        return PlatformId.Read(r);
    }

    public void SetOwner(PlatformId? p)
    {
        if (!Composite) throw new InvalidOperationException("Only composite tile entities carry an owner.");
        byte nf = BodyPre![^1];
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(BlockId); PlatformId.Write(w, p); w.Write(nf); w.Flush();
        BodyPre = ms.ToArray(); Dirty = true;
    }

    public bool Composite => Head != null;

    public byte[] Encode()
    {
        if (!Dirty || !Composite) return Raw;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Head!);
        int size = 4 + BodyPre!.Length;
        foreach (var f in Feats!) size += 8 + f.Payload.Length;
        w.Write((uint)size);
        w.Write(BodyPre);
        foreach (var f in Feats!)
        {
            w.Write(f.Hash);
            w.Write((uint)(4 + f.Payload.Length));
            w.Write(f.Payload);
        }
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>
/// Fully decoded chunk (version 47). Blocks are an array of raw block values, channels are byte planes.
/// Everything the game stores that SaveDoctor does not model (biomes, normals, entities, trailing volume lists …) is carried as raw bytes,
/// so Encode(Parse(x)) reproduces the same data.
/// </summary>
public sealed class ChunkDoc
{
    public const int Height = 256;

    public uint Version;
    public int X, Y, Z;               // chunk coordinates stored inside the chunk
    public ulong Ticks;

    public uint[] Blocks = new uint[65536];   // y*256 + z*16 + x
    public bool[] LayerPresent = new bool[64];
    public bool[] LayerLower = new bool[64];  // lower bytes stored as 1024 byte array
    public bool[] LayerUpper = new bool[64];  // upper 3 bytes stored
    public ChanDoc Stability = new(1), Density = new(1), Light = new(1), Damage = new(2), Textures = new(6), Water = new(2);
    public byte[] Mid = Array.Empty<byte>();  // heightmap(256) + terrain stuff + biomes + custom data + normals

    // tail (everything after the water channel)
    public byte[] TailRaw = Array.Empty<byte>();
    public bool NeedsLight;
    public bool TailParsed;          // entities / tile entities split up
    public int EntCount;
    public List<(int cls, float x, float y, float z)> Ents = new();
    public byte[] EntBytes = Array.Empty<byte>();
    public List<TeRec> Tes = new();
    public byte[] Rest = Array.Empty<byte>();
    public string? TailNote;

    public int Idx(int x, int y, int z) => y * 256 + z * 16 + x;

    // ---------- parse ----------
    public static ChunkDoc Parse(byte[] blob, GameData? g)
    {
        if (blob.Length < 12 || !(blob[0] == 't' && blob[1] == 't' && blob[2] == 'c' && blob[3] == 0)) throw new ParseFail("Not a chunk record", 0);
        uint ver = BitConverter.ToUInt32(blob, 4);
        if (ver != ChunkCodec.CurrentVersion) throw new ParseFail($"Chunk version {ver} is not supported for editing (only {ChunkCodec.CurrentVersion})", 4);
        var raw = BinUtil.Inflate(blob, 8, blob.Length - 8, out bool complete, out string? err);
        if (!complete) throw new ParseFail("Chunk data is broken: " + err, 8);
        return ParseInflated(raw, ver, g);
    }

    public static ChunkDoc ParseInflated(byte[] raw, uint ver, GameData? g)
    {
        var d = new ChunkDoc { Version = ver };
        using var r = BinUtil.Reader(raw);
        d.X = r.ReadInt32(); d.Y = r.ReadInt32(); d.Z = r.ReadInt32();
        d.Ticks = r.ReadUInt64();
        var lower = new byte[1024]; var upper = new byte[3072];
        for (int i = 0; i < 64; i++)
        {
            long at = r.Pos();
            int flag = r.ReadByte();
            if (flag > 1) throw new ParseFail($"Layer {i}: invalid presence flag {flag}", at);
            if (flag == 0) continue;
            d.LayerPresent[i] = true;
            at = r.Pos();
            int f2 = r.ReadByte();
            if (f2 > 1) throw new ParseFail($"Layer {i}: invalid lower-bits flag {f2}", at);
            byte same = 0;
            if (f2 == 1) { r.Need(1024, "layer lower bits"); r.Read(lower, 0, 1024); d.LayerLower[i] = true; }
            else same = r.ReadByte();
            at = r.Pos();
            int f3 = r.ReadByte();
            if (f3 > 1) throw new ParseFail($"Layer {i}: invalid upper-bits flag {f3}", at);
            bool hasUp = f3 == 1;
            if (hasUp) { r.Need(3072, "layer upper bits"); r.Read(upper, 0, 3072); d.LayerUpper[i] = true; }
            int b0 = i * 1024;
            for (int c = 0; c < 1024; c++)
            {
                uint v = f2 == 1 ? lower[c] : same;
                if (hasUp) v |= (uint)upper[c * 3] << 8 | (uint)upper[c * 3 + 1] << 16 | (uint)upper[c * 3 + 2] << 24;
                d.Blocks[b0 + c] = v;
            }
        }
        d.Stability = ChanDoc.Read(r, 1, "stability");
        // mid section: heightmap 256, terrain 256, topsoil 32, biomes 256, intensities 1536, 2, custom data, normals 768
        long midStart = r.Pos();
        r.Need(256 + 256 + 32 + 256 + 1536 + 2 + 2, "terrain arrays");
        r.BaseStream.Seek(256 + 256 + 32 + 256 + 1536 + 2, SeekOrigin.Current);
        int custom = r.ReadUInt16();
        for (int i = 0; i < custom; i++)
        {
            r.Str(); r.ReadUInt64(); r.ReadBoolean();
            int dl = r.ReadUInt16();
            r.Need(dl, "custom data");
            r.BaseStream.Seek(dl, SeekOrigin.Current);
        }
        r.Need(768, "normals");
        r.BaseStream.Seek(768, SeekOrigin.Current);
        d.Mid = new byte[r.Pos() - midStart];
        Array.Copy(raw, midStart, d.Mid, 0, d.Mid.Length);
        d.Density = ChanDoc.Read(r, 1, "density");
        d.Light = ChanDoc.Read(r, 1, "light");
        d.Damage = ChanDoc.Read(r, 2, "damage");
        d.Textures = ChanDoc.Read(r, 6, "textures");
        d.Water = ChanDoc.Read(r, 2, "water");
        int tailStart = (int)r.Pos();
        d.TailRaw = new byte[raw.Length - tailStart];
        Array.Copy(raw, tailStart, d.TailRaw, 0, d.TailRaw.Length);
        d.NeedsLight = d.TailRaw.Length > 0 && d.TailRaw[0] != 0;
        try { d.ParseTail(raw, tailStart, g); }
        catch (Exception ex) when (ex is ParseFail || ex is EndOfStreamException || ex is InvalidDataException)
        {
            d.TailParsed = false; d.Tes = new(); d.TailNote = ex.Message;
        }
        return d;
    }

    void ParseTail(byte[] raw, int tailStart, GameData? g)
    {
        if (g == null) throw new ParseFail("no game data for entity parsing", tailStart);
        using var r = BinUtil.Reader(raw);
        r.BaseStream.Position = tailStart;
        r.ReadBoolean();
        EntCount = r.ReadInt32();
        if (EntCount < 0 || EntCount > 20000) throw new ParseFail("Implausible entity count", r.Pos() - 4);
        int entStart = (int)r.Pos();
        Ents = new();
        for (int i = 0; i < EntCount; i++) { var ent = Ecd.Read(r, g, false); Ents.Add((ent.EntityClass, ent.PosX, ent.PosY, ent.PosZ)); }
        EntBytes = new byte[r.Pos() - entStart];
        Array.Copy(raw, entStart, EntBytes, 0, EntBytes.Length);
        int te = r.ReadInt32();
        if (te < 0 || te > 100000) throw new ParseFail("Implausible tile entity count", r.Pos() - 4);
        var list = new List<TeRec>();
        for (int i = 0; i < te; i++) list.Add(TeRec.ReadOne(r, raw, i));
        int restStart = (int)r.Pos();
        Rest = new byte[raw.Length - restStart];
        Array.Copy(raw, restStart, Rest, 0, Rest.Length);
        Tes = list;
        TailParsed = true;
    }

    // ---------- encode ----------
    public byte[] EncodeInflated()
    {
        using var ms = new MemoryStream(160 * 1024);
        using var w = new BinaryWriter(ms);
        w.Write(X); w.Write(Y); w.Write(Z); w.Write(Ticks);
        for (int i = 0; i < 64; i++)
        {
            int b0 = i * 1024;
            bool any = false;
            for (int c = 0; c < 1024; c++) if (Blocks[b0 + c] != 0) { any = true; break; }
            if (!LayerPresent[i] && !any) { w.Write(false); continue; }
            w.Write(true);
            bool uniformLow = true; uint v0 = Blocks[b0];
            for (int c = 1; c < 1024; c++) if ((byte)Blocks[b0 + c] != (byte)v0) { uniformLow = false; break; }
            if (LayerLower[i] || !uniformLow)
            {
                w.Write(true);
                var lo = new byte[1024];
                for (int c = 0; c < 1024; c++) lo[c] = (byte)Blocks[b0 + c];
                w.Write(lo);
            }
            else { w.Write(false); w.Write((byte)v0); }
            bool anyUp = false;
            for (int c = 0; c < 1024; c++) if ((Blocks[b0 + c] >> 8) != 0) { anyUp = true; break; }
            if (LayerUpper[i] || anyUp)
            {
                w.Write(true);
                var up = new byte[3072];
                for (int c = 0; c < 1024; c++)
                {
                    uint v = Blocks[b0 + c];
                    up[c * 3] = (byte)(v >> 8); up[c * 3 + 1] = (byte)(v >> 16); up[c * 3 + 2] = (byte)(v >> 24);
                }
                w.Write(up);
            }
            else w.Write(false);
        }
        Stability.Write(w);
        w.Write(Mid);
        Density.Write(w);
        Light.Write(w);
        Damage.Write(w);
        Textures.Write(w);
        Water.Write(w);
        if (TailParsed)
        {
            w.Write(NeedsLight);
            w.Write(EntCount);
            w.Write(EntBytes);
            w.Write(Tes.Count);
            foreach (var t in Tes) w.Write(t.Encode());
            w.Write(Rest);
        }
        else
        {
            var t = (byte[])TailRaw.Clone();
            if (t.Length > 0) t[0] = (byte)(NeedsLight ? 1 : 0);
            w.Write(t);
        }
        w.Flush();
        return ms.ToArray();
    }

    public byte[] Encode()
    {
        var inflated = EncodeInflated();
        var comp = BinUtil.Deflate(inflated, System.IO.Compression.CompressionLevel.Optimal);
        var outp = new byte[8 + comp.Length];
        outp[0] = (byte)'t'; outp[1] = (byte)'t'; outp[2] = (byte)'c'; outp[3] = 0;
        BitConverter.GetBytes(Version).CopyTo(outp, 4);
        Array.Copy(comp, 0, outp, 8, comp.Length);
        return outp;
    }

    // ---------- block level edits ----------
    public static int TypeOf(uint v) => (int)(v & 0xFFFF);
    public static bool IsChild(uint v) => (v & 0x40000000u) != 0;
    public static bool IsWaterType(int t) => t == 240 || t == 241 || t == 242;

    public uint GetBlock(int x, int y, int z) => Blocks[Idx(x, y, z)];

    public bool HasWater(int x, int y, int z)
    {
        int i = Idx(x, y, z);
        return (Water.Planes[0][i] | Water.Planes[1][i]) != 0;
    }

    public int HeightAt(int x, int z) => Mid[x + z * 16];

    /// <summary>The game's RecalcHeights for one column: highest y holding a block or water.</summary>
    public void RecalcHeightColumn(int x, int z, int yStart = 255)
    {
        byte h = 0;
        for (int y = yStart; y >= 0; y--)
            if (Blocks[Idx(x, y, z)] != 0 || HasWater(x, y, z)) { h = (byte)y; break; }
        Mid[x + z * 16] = h;
    }

    public void RecalcHeights()
    {
        for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) RecalcHeightColumn(x, z);
    }

    /// <summary>Set one block. <paramref name="terrain"/> marks marching-cubes terrain shapes (density −128) – everything else is air density (127).</summary>
    public void SetBlock(int x, int y, int z, uint value, bool terrain = false)
    {
        int i = Idx(x, y, z);
        Blocks[i] = value;
        bool air = TypeOf(value) == 0;
        Damage.Planes[0][i] = 0; Damage.Planes[1][i] = 0;
        if (air)
        {
            Stability.Planes[0][i] = 0;
            Density.Planes[0][i] = 127;
            if (Mid[x + z * 16] == y) RecalcHeightColumn(x, z, y - 1);
        }
        else
        {
            Stability.Planes[0][i] = 15;
            Density.Planes[0][i] = terrain ? (byte)0x80 : (byte)127;
            if (Mid[x + z * 16] < y) Mid[x + z * 16] = (byte)y;
        }
        // an orphaned tile entity must not outlive its block
        if (TailParsed && air) RemoveTileEntityAt(x, y, z);
        NeedsLight = true;
        // light data of the touched layer is stale – the game recalculates when NeedsLight is set
    }

    public int RemoveTileEntityAt(int x, int y, int z)
    {
        return Tes.RemoveAll(t => t.HasPos && t.X == x && t.Y == y && t.Z == z);
    }

    public TeRec? TileEntityAt(int x, int y, int z) => Tes.FirstOrDefault(t => t.HasPos && t.X == x && t.Y == y && t.Z == z);

    public int SolidCount()
    {
        int n = 0;
        for (int i = 0; i < Blocks.Length; i++) if ((Blocks[i] & 0xFFFF) != 0) n++;
        return n;
    }
}

/// <summary>Contents of a storage-like tile entity (TEFeatureStorage payload).</summary>
public sealed class StorageData
{
    public int FeatureVersion = 20;
    public string? LootList;
    public Grid Grid = new();

    public static readonly int StorageHash = GameData.MonoHash("TEFeatureStorage");

    public static StorageData? TryRead(byte[] payload, GameData g)
    {
        try
        {
            using var r = BinUtil.Reader(payload);
            var s = new StorageData { FeatureVersion = r.ReadUInt16() };
            if (s.FeatureVersion != 20) return null;
            if (r.ReadBoolean()) s.LootList = r.Str();
            s.Grid = Grid.Read(r, g);
            if (r.Left() != 0) return null;
            return s;
        }
        catch (Exception ex) when (ex is ParseFail || ex is EndOfStreamException || ex is InvalidDataException) { return null; }
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)20);
        bool has = !string.IsNullOrEmpty(LootList);
        w.Write(has);
        if (has) w.Str(LootList!);
        Grid.Write(w);
        w.Flush();
        return ms.ToArray();
    }
}
