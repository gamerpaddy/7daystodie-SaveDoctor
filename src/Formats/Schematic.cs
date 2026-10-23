using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class SchemTe
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int Type { get; set; }
    public string Block { get; set; } = "";
    public string Raw { get; set; } = "";           // base64 of the tile entity record
}

/// <summary>A copied piece of world (blocks by name, so it can be pasted into any save), stored as gzip-compressed JSON (.sdschem).</summary>
public sealed class Schematic
{
    public string Format { get; set; } = "SaveDoctorSchematic";
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Created { get; set; } = "";
    public int W { get; set; }
    public int H { get; set; }
    public int D { get; set; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public int OriginZ { get; set; }
    public List<string> Palette { get; set; } = new() { "" };     // index 0 = air
    public string Cells { get; set; } = "";                      // base64 of uint32[W*H*D], index (y*D+z)*W+x; low 16 bits = palette index
    public List<SchemTe> Tes { get; set; } = new();
    public int BlockCount { get; set; }

    [JsonIgnore] public uint[] Raw = Array.Empty<uint>();

    public static string LibraryDir()
    {
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie", "SaveDoctorSchematics");
        Directory.CreateDirectory(d);
        return d;
    }

    public static string SafeName(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.Trim().Trim('.');
        return n.Length == 0 ? "schematic" : n.Length > 80 ? n[..80] : n;
    }

    public byte[] ToBytes()
    {
        var b = new byte[Raw.Length * 4];
        Buffer.BlockCopy(Raw, 0, b, 0, b.Length);
        Cells = Convert.ToBase64String(b);
        var json = JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = false });
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(json);
        Cells = "";
        return ms.ToArray();
    }

    public static Schematic FromBytes(byte[] data)
    {
        byte[] json;
        if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var o = new MemoryStream(); gz.CopyTo(o); json = o.ToArray();
        }
        else json = data;
        var s = JsonSerializer.Deserialize<Schematic>(json) ?? throw new InvalidDataException("Not a schematic file");
        if (s.Format != "SaveDoctorSchematic") throw new InvalidDataException("This is not a SaveDoctor schematic.");
        if (s.Version != 1) throw new InvalidDataException("Unsupported schematic version " + s.Version);
        if (s.W <= 0 || s.H <= 0 || s.D <= 0 || (long)s.W * s.H * s.D > 40_000_000) throw new InvalidDataException("Implausible schematic size");
        var b = Convert.FromBase64String(s.Cells);
        if (b.Length != s.W * s.H * s.D * 4) throw new InvalidDataException("Schematic block data has the wrong length.");
        s.Raw = new uint[b.Length / 4];
        Buffer.BlockCopy(b, 0, s.Raw, 0, b.Length);
        s.Cells = "";
        return s;
    }
}

public static class SchematicOps
{
    /// <summary>Copy a box of the world into a schematic.</summary>
    public static (Schematic s, List<string> notes) Copy(SaveCtx ctx, int x1, int y1, int z1, int x2, int y2, int z2, string name)
    {
        if (x1 > x2) (x1, x2) = (x2, x1); if (y1 > y2) (y1, y2) = (y2, y1); if (z1 > z2) (z1, z2) = (z2, z1);
        y1 = Math.Max(0, y1); y2 = Math.Min(255, y2);
        int W = x2 - x1 + 1, H = y2 - y1 + 1, D = z2 - z1 + 1;
        if ((long)W * H * D > 20_000_000) throw new InvalidOperationException("The selection is too large (limit 20 million blocks).");
        var g = ctx.Game; var we = new WorldEdit(ctx);
        var notes = new List<string>();
        var s = new Schematic { Name = name, Source = ctx.Name, Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), W = W, H = H, D = D, OriginX = x1, OriginY = y1, OriginZ = z1 };
        s.Raw = new uint[W * H * D];
        var pal = new Dictionary<int, int>();
        int missingChunks = 0, droppedTe = 0, unknown = 0, orphanChildren = 0;
        var seenMissing = new HashSet<(int, int)>();
        for (int y = y1; y <= y2; y++)
            for (int z = z1; z <= z2; z++)
                for (int x = x1; x <= x2; x++)
                {
                    if (!we.TryGet(x, y, z, out uint v)) { if (seenMissing.Add((x >> 4, z >> 4))) missingChunks++; continue; }
                    int t = ChunkDoc.TypeOf(v);
                    if (t == 0) continue;
                    if (!g.BlockNames.TryGetValue(t, out var nm)) { unknown++; continue; }
                    var def = we.DefOf(t);
                    var doc = we.ChunkAt(x, z, false)!;
                    if (ChunkDoc.IsChild(v))
                    {
                        // keep the part only when its parent block is inside the selection too
                        var (px, py, pz) = WorldEdit.ParentOffset(v);
                        int ax = x + px, ay = y + py, az = z + pz;
                        if (ax < x1 || ax > x2 || ay < y1 || ay > y2 || az < z1 || az > z2) { orphanChildren++; continue; }
                    }
                    else if (def != null && def.NeedsTileEntity)
                    {
                        var te = doc.TailParsed ? doc.TileEntityAt(x & 15, y, z & 15) : null;
                        if (te == null) { droppedTe++; continue; }
                        s.Tes.Add(new SchemTe { X = x - x1, Y = y - y1, Z = z - z1, Type = te.Type, Block = nm, Raw = Convert.ToBase64String(te.Encode()) });
                    }
                    if (!pal.TryGetValue(t, out int pi)) { pi = s.Palette.Count; s.Palette.Add(nm); pal[t] = pi; }
                    s.Raw[((y - y1) * D + (z - z1)) * W + (x - x1)] = (v & 0xFFFF0000u) | (uint)pi;
                    s.BlockCount++;
                }
        // tile entities of blocks that are not flagged as needing one by blocks.xml (e.g. lights placed by prefabs): copy them by position
        foreach (var cz in Enumerable.Range(z1 >> 4, (z2 >> 4) - (z1 >> 4) + 1))
            foreach (var cx in Enumerable.Range(x1 >> 4, (x2 >> 4) - (x1 >> 4) + 1))
            {
                var d = we.Chunk(cx, cz, false);
                if (d == null || !d.TailParsed) continue;
                foreach (var te in d.Tes)
                {
                    if (!te.HasPos) continue;
                    int wx = cx * 16 + te.X, wz = cz * 16 + te.Z;
                    if (wx < x1 || wx > x2 || te.Y < y1 || te.Y > y2 || wz < z1 || wz > z2) continue;
                    if (s.Tes.Any(e => e.X == wx - x1 && e.Y == te.Y - y1 && e.Z == wz - z1)) continue;
                    uint v = s.Raw[((te.Y - y1) * D + (wz - z1)) * W + (wx - x1)];
                    if ((v & 0xFFFF) == 0) continue;
                    s.Tes.Add(new SchemTe { X = wx - x1, Y = te.Y - y1, Z = wz - z1, Type = te.Type, Block = s.Palette[(int)(v & 0xFFFF)], Raw = Convert.ToBase64String(te.Encode()) });
                }
            }
        if (missingChunks > 0) notes.Add($"{missingChunks} chunk(s) in the selection do not exist in the save (never generated) – treated as air.");
        if (droppedTe > 0) notes.Add($"{droppedTe} block(s) that need a tile entity were left out because their chunk's tile entities cannot be read.");
        if (unknown > 0) notes.Add($"{unknown} block(s) with ids unknown to blockmappings.nim were left out.");
        if (orphanChildren > 0) notes.Add($"{orphanChildren} part(s) of multi-part blocks were cut by the selection border and left out.");
        if (s.BlockCount == 0) throw new InvalidOperationException("The selection contains no blocks.");
        return (s, notes);
    }

    public sealed class PasteResult { public int Placed, Cleared, Skipped, TeCopied, MissingChunk; public List<string> Missing = new(); public List<string> Notes = new(); public string Message = ""; }

    /// <summary>Paste a schematic with its minimum corner at (ox, oy, oz).</summary>
    public static PasteResult Paste(SaveCtx ctx, Schematic s, int ox, int oy, int oz, bool replaceWithAir, bool dryRun)
    {
        var g = ctx.Game; var we = new WorldEdit(ctx);
        var res = new PasteResult();
        if (oy < 0 || oy + s.H > 256) throw new InvalidOperationException($"The schematic is {s.H} blocks tall; at y={oy} it would leave the world (0–255).");
        var ids = new int[s.Palette.Count];
        var missingNames = new HashSet<string>();
        for (int i = 1; i < s.Palette.Count; i++)
        {
            if (g.BlockIds.TryGetValue(s.Palette[i], out int id)) ids[i] = id;
            else { ids[i] = 0; missingNames.Add(s.Palette[i]); }
        }
        res.Missing = missingNames.OrderBy(x => x).ToList();
        // the chunks must exist
        var chunkOk = new Dictionary<(int, int), bool>();
        bool ChunkExists(int x, int z)
        {
            var k = (x >> 4, z >> 4);
            if (!chunkOk.TryGetValue(k, out bool ok)) { ok = we.Chunk(k.Item1, k.Item2, !dryRun) != null; chunkOk[k] = ok; }
            return ok;
        }
        // first clear everything that will be overwritten (so multi-part blocks of the old content vanish completely)
        for (int y = 0; y < s.H; y++)
            for (int z = 0; z < s.D; z++)
                for (int x = 0; x < s.W; x++)
                {
                    uint v = s.Raw[(y * s.D + z) * s.W + x];
                    int pi = (int)(v & 0xFFFF);
                    int wx = ox + x, wy = oy + y, wz = oz + z;
                    if (!ChunkExists(wx, wz)) { res.MissingChunk++; res.Skipped++; continue; }
                    if (pi == 0 && !replaceWithAir) continue;
                    if (pi != 0 && ids[pi] == 0) { res.Skipped++; continue; }
                    if (!dryRun && we.TryGet(wx, wy, wz, out uint old) && ChunkDoc.TypeOf(old) != 0) { we.Clear(wx, wy, wz); res.Cleared++; }
                }
        // place
        var parents = new List<(int x, int y, int z, uint raw)>();
        for (int y = 0; y < s.H; y++)
            for (int z = 0; z < s.D; z++)
                for (int x = 0; x < s.W; x++)
                {
                    uint v = s.Raw[(y * s.D + z) * s.W + x];
                    int pi = (int)(v & 0xFFFF);
                    if (pi == 0 || ids[pi] == 0) continue;
                    int wx = ox + x, wy = oy + y, wz = oz + z;
                    if (!ChunkExists(wx, wz)) continue;
                    uint nv = (v & 0xFFFF0000u) | (uint)ids[pi];
                    res.Placed++;
                    if (dryRun) continue;
                    we.SetRaw(wx, wy, wz, nv);
                    var def = we.DefOf(ids[pi]);
                    if (def is { MultiDim: true } && !ChunkDoc.IsChild(nv)) parents.Add((wx, wy, wz, nv));
                }
        // tile entities
        foreach (var t in s.Tes)
        {
            int wx = ox + t.X, wy = oy + t.Y, wz = oz + t.Z;
            if (!ChunkExists(wx, wz)) continue;
            uint v = s.Raw[(t.Y * s.D + t.Z) * s.W + t.X];
            int pi = (int)(v & 0xFFFF);
            if (pi == 0 || ids[pi] == 0) continue;
            if (dryRun) { res.TeCopied++; continue; }
            var doc = we.ChunkAt(wx, wz, true)!;
            if (!doc.TailParsed) { res.Notes.Add($"chunk {(wx >> 4)},{(wz >> 4)}: tile entities cannot be written there (unsupported tile entities already in the chunk)"); continue; }
            var rec = ParseRec(Convert.FromBase64String(t.Raw));
            if (rec == null) continue;
            doc.RemoveTileEntityAt(wx & 15, wy, wz & 15);
            doc.Tes.Add(rec.Moved(wx & 15, wy, wz & 15, rec.Composite ? ids[pi] : null));
            we.MarkDirty(wx >> 4, wz >> 4);
            res.TeCopied++;
        }
        // multi-part blocks that reach into the next chunk are tracked by multiblocks.7dt
        foreach (var p in parents)
        {
            var def = we.DefOf(ChunkDoc.TypeOf(p.raw));
            if (def == null) continue;
            int r = Math.Max(def.Dim[0], Math.Max(def.Dim[1], def.Dim[2])) - 1;
            bool cross = ((p.x - r) >> 4) != ((p.x + r) >> 4) || ((p.z - r) >> 4) != ((p.z + r) >> 4);
            if (cross) we.AddedMulti.Add(new MultiBlockRec { X = p.x, Y = p.y, Z = p.z, Raw = p.raw, Flags = 2 });
        }
        if (res.MissingChunk > 0) res.Notes.Add($"{res.MissingChunk} position(s) lie in chunks that do not exist yet – walk there in the game once so the world generates them, then paste again.");
        if (res.Missing.Count > 0) res.Notes.Add($"{res.Missing.Count} block type(s) are unknown to this save's blockmappings.nim and were skipped: {string.Join(", ", res.Missing.Take(8))}{(res.Missing.Count > 8 ? "…" : "")}");
        if (dryRun) { res.Message = $"Would place {res.Placed:N0} blocks and {res.TeCopied} tile entities."; return res; }
        if (res.Placed == 0 && res.Cleared == 0) { res.Message = "Nothing to paste."; return res; }
        res.Message = we.Commit("paste-schematic") + $" Placed {res.Placed:N0} blocks, {res.TeCopied} tile entities.";
        foreach (var n in we.Notes) res.Notes.Add(n);
        return res;
    }

    static TeRec? ParseRec(byte[] raw)
    {
        try { using var r = BinUtil.Reader(raw); return TeRec.ReadOne(r, raw, 0); }
        catch (Exception ex) when (ex is ParseFail || ex is EndOfStreamException) { return null; }
    }
}
