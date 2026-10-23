using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>
/// Edits blocks / tile entities across any number of chunks and regions of a save, then writes every touched region exactly once.
/// Always works through <see cref="ChunkDoc"/>; refuses regions that contain damaged chunks (repair them first).
/// </summary>
public sealed class WorldEdit
{
    sealed class Reg
    {
        public string Rel = "";
        public int RX, RZ;
        public bool Exists;
        public Dictionary<int, (byte[] blob, uint ts)> Slots = new();
        public bool HasBad;
    }

    readonly SaveCtx ctx;
    readonly GameData g;
    readonly Dictionary<(int, int), Reg> regs = new();
    readonly Dictionary<(int, int), ChunkDoc?> docs = new();
    readonly HashSet<(int, int)> dirty = new();
    public readonly List<(int x, int y, int z)> RemovedMulti = new();
    public readonly List<MultiBlockRec> AddedMulti = new();
    public readonly List<string> Notes = new();
    public int BlocksChanged;

    public WorldEdit(SaveCtx ctx) { this.ctx = ctx; g = ctx.Game; }

    public static string RegionRel(int rx, int rz) => $"Region/r.{rx}.{rz}.7rg";

    Reg Region(int rx, int rz)
    {
        if (regs.TryGetValue((rx, rz), out var r)) return r;
        r = new Reg { Rel = RegionRel(rx, rz), RX = rx, RZ = rz };
        if (File.Exists(ctx.Abs(r.Rel)))
        {
            r.Exists = true;
            var data = ctx.ReadAll(r.Rel);
            var sc = RegionIO.Scan(data, rx, rz, false, false);
            r.HasBad = sc.Bad > 0 || sc.Issues.Any(i => i.Sev >= Sev.Error);
            foreach (var s in sc.Slots.Where(s => s.Blob != null && s.Status != "bad")) r.Slots[s.Index] = (s.Blob!, s.Timestamp);
        }
        regs[(rx, rz)] = r;
        return r;
    }

    public ChunkDoc? Chunk(int cx, int cz, bool forWrite)
    {
        if (docs.TryGetValue((cx, cz), out var d))
        {
            return d;
        }
        var r = Region(RegionIO.RegionOf(cx), RegionIO.RegionOf(cz));
        if (forWrite && r.HasBad) throw new InvalidOperationException($"{r.Rel} contains damaged chunks – repair or rebuild the region first (editing would silently drop them).");
        d = null;
        if (r.Slots.TryGetValue(RegionIO.SlotIndex(cx, cz), out var s))
        {
            d = ChunkDoc.Parse(s.blob, g);
            if (d.X != cx || d.Z != cz) throw new InvalidOperationException($"Chunk in {r.Rel} claims to be ({d.X},{d.Z}) instead of ({cx},{cz}).");
        }
        docs[(cx, cz)] = d;
        return d;
    }

    static int FloorDiv16(int v) => v >> 4;

    public bool TryGet(int x, int y, int z, out uint v)
    {
        v = 0;
        if (y < 0 || y > 255) return false;
        var d = Chunk(FloorDiv16(x), FloorDiv16(z), false);
        if (d == null) return false;
        v = d.Blocks[d.Idx(x & 15, y, z & 15)];
        return true;
    }

    public ChunkDoc? ChunkAt(int x, int z, bool forWrite = true) => Chunk(FloorDiv16(x), FloorDiv16(z), forWrite);

    void Touch(int cx, int cz) => dirty.Add((cx, cz));
    public void MarkDirty(int cx, int cz) => dirty.Add((cx, cz));

    public BlockDef? DefOf(int type)
    {
        if (type == 0) return null;
        if (!g.BlockNames.TryGetValue(type, out var n)) return null;
        return BlockCatalog.Shared.Get(n);
    }

    public static (int x, int y, int z) ParentOffset(uint v) => ((int)((v >> 22) & 15) - 8, (int)((v >> 16) & 63) - 32, (int)((v >> 26) & 15) - 8);

    /// <summary>Raw set without any multi-block handling. Returns false if the chunk does not exist.</summary>
    public bool SetRaw(int x, int y, int z, uint v)
    {
        if (y < 0 || y > 255) return false;
        int cx = FloorDiv16(x), cz = FloorDiv16(z);
        var d = Chunk(cx, cz, true);
        if (d == null) return false;
        var def = DefOf(ChunkDoc.TypeOf(v));
        int i = d.Idx(x & 15, y, z & 15);
        if (d.Blocks[i] == v) return true;
        d.SetBlock(x & 15, y, z & 15, v, def?.Terrain ?? false);
        Touch(cx, cz);
        BlocksChanged++;
        return true;
    }

    /// <summary>Remove the block at the position including all parts of a multi-block structure and its tile entity.</summary>
    public int Clear(int x, int y, int z)
    {
        if (!TryGet(x, y, z, out uint v) || ChunkDoc.TypeOf(v) == 0) return 0;
        int type = ChunkDoc.TypeOf(v);
        if (ChunkDoc.IsChild(v))
        {
            var (px, py, pz) = ParentOffset(v);
            int ppx = x + px, ppy = y + py, ppz = z + pz;
            if (TryGet(ppx, ppy, ppz, out uint pv) && ChunkDoc.TypeOf(pv) == type && !ChunkDoc.IsChild(pv)) return ClearGroup(ppx, ppy, ppz, type);
            SetRaw(x, y, z, 0); return 1;
        }
        var def = DefOf(type);
        if (def is { MultiDim: true }) return ClearGroup(x, y, z, type);
        SetRaw(x, y, z, 0);
        return 1;
    }

    int ClearGroup(int px, int py, int pz, int type)
    {
        var def = DefOf(type);
        int r = def == null ? 1 : Math.Max(def.Dim[0], Math.Max(def.Dim[1], def.Dim[2])) - 1;
        int n = 0;
        for (int dy = -r; dy <= r; dy++)
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    int x = px + dx, y = py + dy, z = pz + dz;
                    if (!TryGet(x, y, z, out uint cv) || ChunkDoc.TypeOf(cv) != type || !ChunkDoc.IsChild(cv)) continue;
                    var (ox, oy, oz) = ParentOffset(cv);
                    if (x + ox == px && y + oy == py && z + oz == pz) { SetRaw(x, y, z, 0); n++; }
                }
        SetRaw(px, py, pz, 0);
        RemovedMulti.Add((px, py, pz));
        return n + 1;
    }

    /// <summary>Why this block type cannot be placed freshly (null = fine).</summary>
    public string? PlaceProblem(int type)
    {
        if (type == 0) return null;
        if (type > 65535) return "block id out of range";
        if (ChunkDoc.IsWaterType(type)) return "water is not a block (it lives in its own channel)";
        if (!g.BlockNames.ContainsKey(type)) return "block id " + type + " is not in blockmappings.nim";
        var def = DefOf(type);
        if (def == null) return null;     // blocks.xml unavailable: allow
        if (def.MultiDim) return $"{def.Name} is a multi-part block ({def.Dim[0]}×{def.Dim[1]}×{def.Dim[2]}) – copy it with the schematic tools instead";
        if (def.NeedsTileEntity) return $"{def.Name} needs a tile entity – copy it with the schematic tools instead";
        return null;
    }

    /// <summary>Place a plain block. Existing block (and multi-block parts) are removed first.</summary>
    public bool Place(int x, int y, int z, int type, int rot, bool force = false)
    {
        if (!force && PlaceProblem(type) is string why) throw new InvalidOperationException(why);
        if (TryGet(x, y, z, out uint old) && ChunkDoc.TypeOf(old) != 0) Clear(x, y, z);
        uint v = (uint)type | ((uint)(rot & 31) << 16);
        if (type == 0) v = 0;
        return SetRaw(x, y, z, v);
    }

    // ---------- commit ----------
    public string Commit(string reason)
    {
        var perRegion = dirty.GroupBy(c => (RegionIO.RegionOf(c.Item1), RegionIO.RegionOf(c.Item2))).ToList();
        using var batch = ctx.BeginBatch(reason);
        int written = 0, chunks = 0;
        foreach (var grp in perRegion)
        {
            var reg = Region(grp.Key.Item1, grp.Key.Item2);
            var final = reg.Slots.ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var (cx, cz) in grp)
            {
                var d = docs[(cx, cz)]!;
                var blob = d.Encode();
                var chk = ChunkCodec.Inspect(blob, false);
                if (!chk.Ok) throw new InvalidOperationException($"Internal check failed for chunk ({cx},{cz}): {chk.Error} – nothing was written.");
                int slot = RegionIO.SlotIndex(cx, cz);
                final[slot] = (blob, final.TryGetValue(slot, out var old) ? old.ts : 0u);
                chunks++;
            }
            var rebuilt = RegionIO.Build(final.Select(kv => (kv.Key, kv.Value.blob, kv.Value.ts)));
            var verify = RegionIO.Scan(rebuilt, reg.RX, reg.RZ, false, false);
            if (verify.Bad > 0) throw new InvalidOperationException("Internal check of the rebuilt region failed – nothing was written.");
            ctx.WriteFile(reg.Rel, rebuilt, reason);
            RegionHandler.Invalidate(ctx, reg.Rel);
            written++;
        }
        UpdateMultiBlockFile();
        dirty.Clear();
        return $"{BlocksChanged} block(s) changed in {chunks} chunk(s) of {written} region file(s).";
    }

    void UpdateMultiBlockFile()
    {
        if (RemovedMulti.Count == 0 && AddedMulti.Count == 0) return;
        const string rel = "multiblocks.7dt";
        if (!File.Exists(ctx.Abs(rel))) return;
        try
        {
            var list = MultiBlockRec.Parse(ctx.ReadAll(rel));
            int before = list.Count;
            var rem = RemovedMulti.ToHashSet();
            list.RemoveAll(r => rem.Contains((r.X, r.Y, r.Z)));
            foreach (var a in AddedMulti) { list.RemoveAll(r => r.X == a.X && r.Y == a.Y && r.Z == a.Z); list.Add(a); }
            if (list.Count != before || AddedMulti.Count > 0) ctx.WriteFile(rel, MultiBlockRec.Build(list), "edit-multiblocks");
        }
        catch (Exception ex) { Notes.Add("multiblocks.7dt was not updated: " + ex.Message); }
    }
}
