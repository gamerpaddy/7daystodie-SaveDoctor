using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class RegionSlot
{
    public int Index { get; set; }
    public int ChunkX { get; set; }
    public int ChunkZ { get; set; }
    public int Offset { get; set; }
    public int Length { get; set; }               // sectors
    public uint Timestamp { get; set; }
    public string Status { get; set; } = "ok";    // ok | warn | bad
    public string? Message { get; set; }
    public int DataLength { get; set; }
    public uint Version { get; set; }
    public int InflatedSize { get; set; }
    public int SolidBlocks { get; set; }
    public int Entities { get; set; } = -1;
    public bool Deep { get; set; }
    public bool Full { get; set; }
    public int TileEntities { get; set; } = -1;
    public string? TailNote { get; set; }
    [JsonIgnore] public byte[]? Blob { get; set; }
    [JsonIgnore] public ChunkInfo? Chunk { get; set; }
}

public sealed class RegionScan
{
    public int RX { get; set; }
    public int RZ { get; set; }
    public int FileVersion { get; set; }
    public long FileLength { get; set; }
    public List<RegionSlot> Slots { get; set; } = new();
    public List<Issue> Issues { get; set; } = new();
    public int Good => Slots.Count(s => s.Status != "bad");
    public int Bad => Slots.Count(s => s.Status == "bad");
}

public static class RegionIO
{
    public const int Sector = 4096;
    public static readonly Regex NameRx = new(@"^r\.(-?\d+)\.(-?\d+)\.7rg$", RegexOptions.IgnoreCase);

    public static bool TryParseName(string file, out int rx, out int rz)
    {
        var m = NameRx.Match(Path.GetFileName(file));
        rx = rz = 0;
        if (!m.Success) return false;
        return int.TryParse(m.Groups[1].Value, out rx) && int.TryParse(m.Groups[2].Value, out rz);
    }

    /// <summary>Same quirky slot formula the game uses (negative coordinates are shifted by one).</summary>
    public static int SlotIndex(int cx, int cz)
    {
        int x = cx % 32; int z = cz % 32;
        if (cx < 0) x += 31;
        if (cz < 0) z += 31;
        return x + z * 32;
    }

    public static (int cx, int cz) ChunkFromSlot(int rx, int rz, int index)
    {
        int lx = index & 31, lz = index >> 5;
        int kx = rx < 0 ? (lx <= 30 ? lx + 1 : 0) : lx;
        int kz = rz < 0 ? (lz <= 30 ? lz + 1 : 0) : lz;
        return (rx * 32 + kx, rz * 32 + kz);
    }

    public static int RegionOf(int c) => (int)Math.Floor(c / 32.0);

    public static RegionScan Scan(byte[] data, int rx, int rz, bool deep = true, bool census = false, GameData? g = null)
    {
        var sc = new RegionScan { RX = rx, RZ = rz, FileLength = data.Length };
        void Err(string code, string msg, long? off = null) => sc.Issues.Add(new Issue(Sev.Error, code, msg, off));
        void Wrn(string code, string msg, long? off = null) => sc.Issues.Add(new Issue(Sev.Warn, code, msg, off));
        if (data.Length < 12288) { Err("header-short", $"File is only {data.Length} bytes – the 12 KB header is incomplete.", 0); return sc; }
        if (!(data[0] == '7' && data[1] == 'r' && data[2] == 'g')) Err("magic", "Wrong signature – the first bytes are not '7rg'. The game throws \"Incorrect region file header\" and cannot load this region.", 0);
        sc.FileVersion = data[3];
        if (sc.FileVersion == 0) { Wrn("v1", "Legacy V1 region format; contents are not deep-checked."); return sc; }
        if (sc.FileVersion != 1) Wrn("version", $"Unknown region format version {sc.FileVersion} (the game reads 1).", 3);

        long sectors = (data.Length + Sector - 1) / Sector;
        if (data.Length % Sector != 0) Wrn("size-align", $"File length {data.Length} is not a multiple of 4096 (last write was cut off?).", data.Length);

        var occupied = new List<(int off, int len, int idx)>();
        for (int e = 0; e < 1024; e++)
        {
            int p = Sector + e * 4;
            int offRaw = data[p] | (data[p + 1] << 8);
            // game: short conversion overflows to 0 for >32767
            int off = offRaw > 32767 ? 0 : offRaw;
            int len = data[p + 3];
            uint ts = BitConverter.ToUInt32(data, 2 * Sector + e * 4);
            if (off == 0 && len == 0)
            {
                if (offRaw != 0 || data[p + 2] != 0) Wrn("slot-junk", $"Slot {e} has leftover bytes in an empty entry.", p);
                continue;
            }
            var (cx, cz) = ChunkFromSlot(rx, rz, e);
            var s = new RegionSlot { Index = e, ChunkX = cx, ChunkZ = cz, Offset = off, Length = len, Timestamp = ts };
            sc.Slots.Add(s);
            if (off == 0 || len == 0) { s.Status = "bad"; s.Message = "Location entry is half-empty (offset or length is 0) – the chunk is unreachable."; continue; }
            if (off < 3) { s.Status = "bad"; s.Message = $"Sector offset {off} points into the header."; continue; }
            if ((long)(off + len) > sectors)
            {
                s.Status = "bad";
                s.Message = $"Chunk is stored at sectors {off}–{off + len - 1} but the file only has {sectors} sectors (file truncated).";
                // partial data may still be there – but it is incomplete
                continue;
            }
            occupied.Add((off, len, e));
            long pos = (long)off * Sector;
            int dataLen = BitConverter.ToInt32(data, (int)pos);
            s.DataLength = dataLen;
            if (dataLen <= 8 || dataLen > len * Sector - 16)
            {
                s.Status = "bad";
                s.Message = $"Stored length {dataLen} does not fit its {len} sector(s).";
                continue;
            }
            var blob = new byte[dataLen];
            Array.Copy(data, pos + 16, blob, 0, dataLen);
            s.Blob = blob;
            if (!deep) continue;
            var ci = ChunkCodec.Inspect(blob, census, g);
            s.Chunk = ci;
            s.Version = ci.Version; s.InflatedSize = ci.InflatedSize; s.SolidBlocks = ci.SolidBlocks; s.Entities = ci.Entities; s.Deep = ci.Deep; s.Full = ci.Full; s.TileEntities = ci.TileEntities; s.TailNote = ci.TailNote;
            if (!ci.Ok) { s.Status = "bad"; s.Message = ci.Error; continue; }
            if (ci.X != cx || ci.Z != cz)
            {
                s.Status = "bad";
                s.Message = $"Chunk data says it is chunk ({ci.X},{ci.Z}) but it sits in the slot for ({cx},{cz}) – misplaced/garbled.";
                continue;
            }
            if (ci.Y != 0) { s.Status = "warn"; s.Message = $"Unusual Y coordinate {ci.Y}."; }
            if (ci.Version != ChunkCodec.CurrentVersion) { s.Status = s.Status == "ok" ? "warn" : s.Status; s.Message ??= $"Chunk format version {ci.Version} (current is {ChunkCodec.CurrentVersion}); only the basic envelope was verified."; }
        }

        // overlap detection
        occupied.Sort((a, b) => a.off.CompareTo(b.off));
        for (int i = 1; i < occupied.Count; i++)
        {
            var prev = occupied[i - 1]; var cur = occupied[i];
            if (cur.off < prev.off + prev.len)
            {
                foreach (var idx in new[] { prev.idx, cur.idx })
                {
                    var s = sc.Slots.First(x => x.Index == idx);
                    if (s.Status != "bad") { s.Status = "bad"; }
                    s.Message = (s.Message == null ? "" : s.Message + " ") + $"Overlaps the sectors of another chunk (slots {prev.idx} and {cur.idx}).";
                }
            }
        }
        int dupOff = occupied.GroupBy(o => o.off).Count(g => g.Count() > 1);
        if (dupOff > 0) Err("dup-offset", $"{dupOff} sector positions are claimed by more than one chunk.");

        // slots without data at the end of the file
        long used = occupied.Count == 0 ? 3 : occupied.Max(o => (long)o.off + o.len);
        if (sectors > used + 16) Wrn("slack", $"{sectors - used} unused sectors at the end of the file (wasted space, harmless).");

        int bad = sc.Slots.Count(s => s.Status == "bad");
        if (bad > 0) Err("bad-chunks", $"{bad} of {sc.Slots.Count} chunks are damaged: " +
            string.Join(", ", sc.Slots.Where(s => s.Status == "bad").Take(12).Select(s => $"({s.ChunkX},{s.ChunkZ})")) + (bad > 12 ? ", …" : ""));
        return sc;
    }

    /// <summary>Assemble a fresh, compact region file from chunk blobs (header + sequential sectors).</summary>
    public static byte[] Build(IEnumerable<(int index, byte[] blob, uint ts)> chunks, int fileVersion = 1)
    {
        var list = chunks.OrderBy(c => c.index).ToList();
        using var ms = new MemoryStream();
        var head = new byte[3 * Sector];
        head[0] = (byte)'7'; head[1] = (byte)'r'; head[2] = (byte)'g'; head[3] = (byte)fileVersion;
        ms.Write(head, 0, head.Length);
        int sector = 3;
        var loc = new byte[Sector]; var tsb = new byte[Sector];
        foreach (var (index, blob, ts) in list)
        {
            int needed = blob.Length + 16;
            int sec = (needed + Sector - 1) / Sector;
            if (sec > 255) throw new InvalidOperationException($"Chunk in slot {index} is too large for one region entry ({sec} sectors)");
            if (sector + sec > 32767) throw new InvalidOperationException("Region file would become larger than the 128 MB limit of the format");
            var rec = new byte[sec * Sector];
            BitConverter.GetBytes(blob.Length).CopyTo(rec, 0);
            Array.Copy(blob, 0, rec, 16, blob.Length);
            ms.Write(rec, 0, rec.Length);
            loc[index * 4] = (byte)(sector & 0xFF); loc[index * 4 + 1] = (byte)(sector >> 8); loc[index * 4 + 2] = 0; loc[index * 4 + 3] = (byte)sec;
            BitConverter.GetBytes(ts).CopyTo(tsb, index * 4);
            sector += sec;
        }
        var all = ms.ToArray();
        Array.Copy(loc, 0, all, Sector, Sector);
        Array.Copy(tsb, 0, all, 2 * Sector, Sector);
        return all;
    }
}

public sealed class RegionHandler : FormatHandler
{
    public override string Kind => "region";
    public override string Title => "World region (terrain & player builds)";
    static readonly Regex Rx = new(@"(^|/)Region/r\.-?\d+\.-?\d+\.7rg$", RegexOptions.IgnoreCase);
    public override bool Match(string rel) => Rx.IsMatch(rel);

    static readonly Dictionary<string, (long size, DateTime t, RegionScan scan)> Cache = new();

    public static RegionScan CachedScan(SaveCtx ctx, string rel, byte[] data, bool census)
    {
        if (!RegionIO.TryParseName(Registry.SplitBackup(rel).baseRel, out int rx, out int rz)) throw new InvalidOperationException("Not a region file name");
        var fi = new FileInfo(ctx.Abs(rel));
        var key = ctx.Root + "|" + rel + "|" + census;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var c) && c.size == fi.Length && c.t == fi.LastWriteTimeUtc) return c.scan;
        }
        var sc = RegionIO.Scan(data, rx, rz, true, census, ctx.Game);
        foreach (var sl in sc.Slots) if (sl.Chunk != null) { sl.Chunk.Inflated = null; sl.Chunk.Layers.Clear(); }   // keep the cache small
        lock (Cache) { Cache[key] = (fi.Length, fi.LastWriteTimeUtc, sc); if (Cache.Count > 64) Cache.Remove(Cache.Keys.First()); }
        return sc;
    }

    public static void Invalidate(SaveCtx ctx, string rel)
    {
        lock (Cache) foreach (var k in Cache.Keys.Where(k => k.StartsWith(ctx.Root + "|" + rel + "|")).ToList()) Cache.Remove(k);
    }

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data); a.Editable = true;
        if (LooksWiped(data, out var why))
        {
            a.Add(Sev.Error, "wiped", "This region file is unusable: " + why + ".");
            a.Repairs.Add(new RepairOption("delete", "Delete region file", "The game regenerates terrain for the whole region (≈512×512 blocks). Player builds in this region are lost.", true));
            return a;
        }
        if (!RegionIO.TryParseName(rel, out int rx, out int rz)) return a.Add(Sev.Warn, "name", "File name is not r.<x>.<z>.7rg.");
        var sc = RegionIO.Scan(data, rx, rz, true, false, ctx.Game);
        foreach (var i in sc.Issues) a.Issues.Add(i);
        a.Summary = $"{sc.Slots.Count} chunks · {sc.Bad} damaged · {BinUtil.Size(data.Length)}";
        a.Info["chunks"] = sc.Slots.Count; a.Info["damaged"] = sc.Bad; a.Info["regionX"] = rx; a.Info["regionZ"] = rz;
        a.Info["slots"] = true;
        if (sc.Issues.Any(i => i.Sev >= Sev.Error) || sc.Bad > 0)
        {
            a.Repairs.Add(new RepairOption("rebuild", "Rebuild file, drop damaged chunks",
                $"Writes a clean region file with the {sc.Good} intact chunks. The {sc.Bad} damaged chunk(s) are removed – the game re-generates natural terrain there (builds in those chunks are lost).", true));
            if (sc.Bad > 0)
                a.Repairs.Add(new RepairOption("fromBackup", "Recover damaged chunks from .bak / other file…",
                    "Copies intact versions of the damaged chunks from a backup copy of this region (choose in the region view).", false));
        }
        else if (sc.Issues.Any(i => i.Code is "slack" or "size-align"))
            a.Repairs.Add(new RepairOption("rebuild", "Compact file", "Rewrites the file without unused space.", false));
        return a;
    }

    public override string Repair(SaveCtx ctx, string rel, string action, JsonElement args)
    {
        if (!RegionIO.TryParseName(rel, out int rx, out int rz)) throw new InvalidOperationException("Not a region file");
        var data = ctx.ReadAll(rel);
        switch (action)
        {
            case "delete":
                ctx.DeleteFile(rel, "delete-region"); Invalidate(ctx, rel);
                return "Deleted " + rel + ".";
            case "rebuild":
                {
                    var sc = RegionIO.Scan(data, rx, rz, true, false, ctx.Game);
                    var keep = sc.Slots.Where(s => s.Status != "bad" && s.Blob != null)
                        .Select(s => (s.Index, s.Blob!, s.Timestamp)).ToList();
                    var rebuilt = RegionIO.Build(keep);
                    var check = RegionIO.Scan(rebuilt, rx, rz, true, false);
                    if (check.Bad > 0) throw new InvalidOperationException("Internal check failed – refusing to write.");
                    ctx.WriteFile(rel, rebuilt, "rebuild-region"); Invalidate(ctx, rel);
                    return $"Rebuilt {rel}: kept {keep.Count} chunks, dropped {sc.Bad} damaged. New size {BinUtil.Size(rebuilt.Length)} (was {BinUtil.Size(data.Length)}).";
                }
            case "fromBackup":
                {
                    string src = args.TryGetProperty("source", out var sp) ? sp.GetString()! : rel + ".bak";
                    return RegionOps.RecoverFrom(ctx, rel, src);
                }
        }
        return base.Repair(ctx, rel, action, args);
    }
}

/// <summary>Chunk-level operations used by the region view.</summary>
public static class RegionOps
{
    public static string RecoverFrom(SaveCtx ctx, string rel, string source)
    {
        RegionIO.TryParseName(rel, out int rx, out int rz);
        var data = ctx.ReadAll(rel);
        byte[] sdata;
        if (Path.IsPathRooted(source) && File.Exists(source)) sdata = File.ReadAllBytes(source);
        else sdata = ctx.ReadAll(source);
        var tgt = RegionIO.Scan(data, rx, rz, true, false, ctx.Game);
        var src = RegionIO.Scan(sdata, rx, rz, true, false, ctx.Game);
        var srcBy = src.Slots.Where(s => s.Status != "bad" && s.Blob != null).ToDictionary(s => s.Index);
        var final = new Dictionary<int, (byte[] blob, uint ts)>();
        foreach (var s in tgt.Slots.Where(s => s.Status != "bad" && s.Blob != null)) final[s.Index] = (s.Blob!, s.Timestamp);
        int recovered = 0, unrecoverable = 0;
        foreach (var s in tgt.Slots.Where(s => s.Status == "bad"))
        {
            if (srcBy.TryGetValue(s.Index, out var good)) { final[s.Index] = (good.Blob!, good.Timestamp); recovered++; }
            else unrecoverable++;
        }
        var rebuilt = RegionIO.Build(final.Select(kv => (kv.Key, kv.Value.blob, kv.Value.ts)));
        ctx.WriteFile(rel, rebuilt, "recover-region");
        RegionHandler.Invalidate(ctx, rel);
        return $"Recovered {recovered} chunk(s) from the other file; {unrecoverable} could not be recovered and were dropped.";
    }

    public static string RemoveChunks(SaveCtx ctx, string rel, IEnumerable<int> slots)
    {
        RegionIO.TryParseName(rel, out int rx, out int rz);
        var data = ctx.ReadAll(rel);
        var sc = RegionIO.Scan(data, rx, rz, false, false);
        var drop = slots.ToHashSet();
        var keep = sc.Slots.Where(s => !drop.Contains(s.Index) && s.Blob != null && s.Status != "bad").Select(s => (s.Index, s.Blob!, s.Timestamp)).ToList();
        var rebuilt = RegionIO.Build(keep);
        ctx.WriteFile(rel, rebuilt, "remove-chunks");
        RegionHandler.Invalidate(ctx, rel);
        return $"Removed {sc.Slots.Count(s => drop.Contains(s.Index))} chunk(s); {keep.Count} remain.";
    }

    public static string ImportChunk(SaveCtx ctx, string rel, int slot, byte[] blob)
    {
        RegionIO.TryParseName(rel, out int rx, out int rz);
        var ci = ChunkCodec.Inspect(blob, false);
        if (!ci.Ok) throw new InvalidOperationException("Imported chunk is not valid: " + ci.Error);
        var (cx, cz) = RegionIO.ChunkFromSlot(rx, rz, slot);
        if (ci.X != cx || ci.Z != cz) throw new InvalidOperationException($"Chunk data is for ({ci.X},{ci.Z}), but slot {slot} is ({cx},{cz}).");
        var data = ctx.ReadAll(rel);
        var sc = RegionIO.Scan(data, rx, rz, false, false);
        var final = sc.Slots.Where(s => s.Index != slot && s.Blob != null && s.Status != "bad").Select(s => (s.Index, s.Blob!, s.Timestamp)).ToList();
        final.Add((slot, blob, sc.Slots.FirstOrDefault(s => s.Index == slot)?.Timestamp ?? 0u));
        ctx.WriteFile(rel, RegionIO.Build(final), "import-chunk");
        RegionHandler.Invalidate(ctx, rel);
        return $"Imported chunk ({cx},{cz}).";
    }

    public static string ReplaceBlocks(SaveCtx ctx, string rel, int fromId, int toId, int? onlySlot)
    {
        RegionIO.TryParseName(rel, out int rx, out int rz);
        var data = ctx.ReadAll(rel);
        var sc = RegionIO.Scan(data, rx, rz, true, false, ctx.Game);
        var final = new List<(int, byte[], uint)>();
        int total = 0, chunksChanged = 0;
        foreach (var s in sc.Slots.Where(s => s.Blob != null && s.Status != "bad"))
        {
            byte[] blob = s.Blob!;
            if (s.Chunk is { Ok: true } && (onlySlot == null || onlySlot == s.Index))
            {
                var nb = ChunkCodec.ReplaceBlocks(blob, s.Chunk, id => id == fromId ? toId : null, out int ch);
                if (nb != null)
                {
                    var chk = ChunkCodec.Inspect(nb, false);
                    if (!chk.Ok) throw new InvalidOperationException("Internal error: rewritten chunk failed validation: " + chk.Error);
                    blob = nb; total += ch; chunksChanged++;
                }
            }
            final.Add((s.Index, blob, s.Timestamp));
        }
        if (total == 0) return "No blocks with that id were found.";
        ctx.WriteFile(rel, RegionIO.Build(final), "replace-blocks");
        RegionHandler.Invalidate(ctx, rel);
        return $"Replaced {total} block(s) in {chunksChanged} chunk(s).";
    }
}
