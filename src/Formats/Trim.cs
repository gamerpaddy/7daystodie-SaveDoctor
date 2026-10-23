using System.Text.RegularExpressions;
using System.Xml.Linq;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class TrimOptions
{
    public bool Players { get; set; } = true;          // positions, spawn points, bedrolls of players
    public bool Claims { get; set; } = true;
    public bool Vehicles { get; set; } = true;         // vehicles, drones
    public bool Power { get; set; } = true;
    public bool Owned { get; set; } = true;            // player placed storage
    public bool Built { get; set; } = true;            // constructions outside POIs
    public bool KeepPoi { get; set; } = false;
    public int Radius { get; set; } = 4;               // chunks around players / vehicles / power / containers
    public int ClaimRadius { get; set; } = 8;
    public int BuiltThreshold { get; set; } = 150;
    public List<int[]> Protect { get; set; } = new();  // x1,z1,x2,z2 (block coordinates)
}

public sealed class PoiInfo
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int Rot { get; set; }
    public int MinX { get; set; }
    public int MinZ { get; set; }
    public int MaxX { get; set; }
    public int MaxZ { get; set; }
    public int SizeY { get; set; }
}

public sealed class TrimPlan
{
    public const int Player = 1, Claim = 2, Vehicle = 4, Power = 8, Owned = 16, Built = 32, Manual = 64, Poi = 128;
    public Dictionary<(int, int), int> Mask = new();       // chunk → reasons it is kept (0 = delete)
    public HashSet<(int, int)> PoiChunks = new();
    public List<PoiInfo> Pois = new();
    public Dictionary<(int, int), ChunkStat> Chunks = new();
    public int Total, Kept, Delete;
    public long BytesTotal, BytesDelete;
    public string? PoiNote;
}

public static class TrimOps
{
    // ───────── POI footprints from prefabs.xml + prefab files
    static Dictionary<string, string>? prefabFiles;

    static string? FindPrefabsXml(SaveCtx ctx)
    {
        var world = Directory.GetParent(ctx.Root)?.Name ?? "";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var cands = new List<string> { Path.Combine(appData, "7DaysToDie", "GeneratedWorlds", world, "prefabs.xml") };
        var data = GameInstall.DataDir();
        if (data != null) cands.Add(Path.Combine(data, "Worlds", world, "prefabs.xml"));
        return cands.FirstOrDefault(File.Exists);
    }

    static Dictionary<string, string> PrefabIndex()
    {
        if (prefabFiles != null) return prefabFiles;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var data = GameInstall.DataDir();
        var roots = new List<string>();
        if (data != null) roots.Add(Path.Combine(data, "Prefabs"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie", "LocalPrefabs"));
        foreach (var r in roots.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(r, "*.tts", SearchOption.AllDirectories)) d[Path.GetFileNameWithoutExtension(f)] = f;
        return prefabFiles = d;
    }

    static (int x, int y, int z)? PrefabSize(string name)
    {
        if (!PrefabIndex().TryGetValue(name, out var f)) return null;
        try
        {
            using var fs = File.OpenRead(f); using var r = new BinaryReader(fs);
            if (r.ReadByte() != 't' || r.ReadByte() != 't' || r.ReadByte() != 's' || r.ReadByte() != 0) return null;
            r.ReadUInt32();
            return (r.ReadInt16(), r.ReadInt16(), r.ReadInt16());
        }
        catch { return null; }
    }

    public static List<PoiInfo> LoadPois(SaveCtx ctx, out string? note)
    {
        note = null;
        var list = new List<PoiInfo>();
        var xml = FindPrefabsXml(ctx);
        if (xml == null) { note = "prefabs.xml of this world was not found – POIs are not recognised."; return list; }
        int unknown = 0;
        foreach (var e in XDocument.Load(xml).Descendants("decoration"))
        {
            var name = (string?)e.Attribute("name"); var pos = ((string?)e.Attribute("position"))?.Split(',');
            if (name == null || pos == null || pos.Length != 3 || !int.TryParse(pos[0], out int x) || !int.TryParse(pos[1], out int y) || !int.TryParse(pos[2], out int z)) continue;
            int rot = int.TryParse((string?)e.Attribute("rotation"), out var rr) ? rr & 3 : 0;
            var sz = PrefabSize(name);
            if (sz == null) { unknown++; continue; }
            int sx = (rot & 1) == 0 ? sz.Value.x : sz.Value.z, szz = (rot & 1) == 0 ? sz.Value.z : sz.Value.x;
            list.Add(new PoiInfo { Name = name, X = x, Y = y, Z = z, Rot = rot, MinX = x, MinZ = z, MaxX = x + sx - 1, MaxZ = z + szz - 1, SizeY = sz.Value.y });
        }
        if (unknown > 0) note = $"{unknown} POI(s) could not be sized (prefab file not found in the game install).";
        return list;
    }

    // ───────── markers of things players care about
    sealed record Mk(int Kind, double X, double Z);

    static List<Mk> Markers(SaveCtx c, TrimOptions o)
    {
        var g = c.Game; var list = new List<Mk>();
        if (o.Players && Directory.Exists(c.Abs("Player")))
            foreach (var f in Directory.EnumerateFiles(c.Abs("Player"), "*.ttp"))
                try
                {
                    var p = PlayerFile.Parse(File.ReadAllBytes(f), g);
                    list.Add(new Mk(TrimPlan.Player, p.Ecd.PosX, p.Ecd.PosZ));
                    if (p.LastSpawnPos.Any(v => v != 0)) list.Add(new Mk(TrimPlan.Player, p.LastSpawnPos[0], p.LastSpawnPos[2]));
                    foreach (var sp in p.SpawnPoints) list.Add(new Mk(TrimPlan.Player, sp[0], sp[2]));
                }
                catch { }
        if (o.Vehicles)
            foreach (var file in new[] { "vehicles.dat", "drones.dat", "turrets.dat" })
                try { if (File.Exists(c.Abs(file)) && file != "turrets.dat") foreach (var e in EntityListFile.Parse(c.ReadAll(file), g).Entities) list.Add(new Mk(TrimPlan.Vehicle, e.PosX, e.PosZ)); } catch { }
        if (o.Power)
            try { if (File.Exists(c.Abs("power.dat"))) foreach (var n in PowerFile.Parse(c.ReadAll("power.dat"), g).All()) list.Add(new Mk(TrimPlan.Power, n.Pos[0], n.Pos[2])); } catch { }
        if (o.Claims || o.Players)
            try
            {
                if (File.Exists(c.Abs("players.xml")))
                {
                    var doc = XDocument.Parse(XmlHandler.DecodeText(c.ReadAll("players.xml"), out _));
                    foreach (var pl in doc.Descendants("player"))
                    {
                        void P(string el, int kind) { foreach (var e in pl.Descendants(el)) { var v = ((string?)e.Attribute("pos"))?.Split(','); if (v?.Length == 3 && double.TryParse(v[0], out var x) && double.TryParse(v[2], out var z)) list.Add(new Mk(kind, x, z)); } }
                        if (o.Claims) P("lpblock", TrimPlan.Claim);
                        if (o.Players) { P("bedroll", TrimPlan.Player); P("backpack", TrimPlan.Player); }
                        if (o.Players && ((string?)pl.Attribute("position"))?.Split(',') is { Length: 3 } pp && double.TryParse(pp[0], out var px) && double.TryParse(pp[2], out var pz)) list.Add(new Mk(TrimPlan.Player, px, pz));
                    }
                }
            }
            catch { }
        return list;
    }

    public static TrimPlan Plan(SaveCtx ctx, WorldIndex ix, TrimOptions o)
    {
        var plan = new TrimPlan();
        foreach (var s in ix.Stats) { plan.Chunks[(s.Cx, s.Cz)] = s; plan.BytesTotal += s.Bytes; }
        plan.Total = plan.Chunks.Count;
        var pois = LoadPois(ctx, out plan.PoiNote);
        plan.Pois = pois;
        foreach (var p in pois)
            for (int cz = p.MinZ >> 4; cz <= p.MaxZ >> 4; cz++) for (int cx = p.MinX >> 4; cx <= p.MaxX >> 4; cx++) plan.PoiChunks.Add((cx, cz));
        void Mark(int cx, int cz, int r, int kind)
        {
            for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++)
            {
                var k = (cx + dx, cz + dz);
                if (plan.Chunks.ContainsKey(k)) plan.Mask[k] = plan.Mask.GetValueOrDefault(k) | kind;
            }
        }
        foreach (var m in Markers(ctx, o)) Mark((int)Math.Floor(m.X) >> 4, (int)Math.Floor(m.Z) >> 4, m.Kind == TrimPlan.Claim ? o.ClaimRadius : o.Radius, m.Kind);
        foreach (var s in ix.Stats)
        {
            var k = (s.Cx, s.Cz);
            if (o.Owned && s.OwnedContainers > 0) Mark(s.Cx, s.Cz, Math.Max(1, o.Radius / 2), TrimPlan.Owned);
            if (o.Built && s.Constructed >= o.BuiltThreshold && !plan.PoiChunks.Contains(k)) Mark(s.Cx, s.Cz, 1, TrimPlan.Built);
            if (o.KeepPoi && plan.PoiChunks.Contains(k)) plan.Mask[k] = plan.Mask.GetValueOrDefault(k) | TrimPlan.Poi;
        }
        foreach (var r in o.Protect)
        {
            if (r.Length != 4) continue;
            for (int cz = Math.Min(r[1], r[3]) >> 4; cz <= Math.Max(r[1], r[3]) >> 4; cz++) for (int cx = Math.Min(r[0], r[2]) >> 4; cx <= Math.Max(r[0], r[2]) >> 4; cx++)
                if (plan.Chunks.ContainsKey((cx, cz))) plan.Mask[(cx, cz)] = plan.Mask.GetValueOrDefault((cx, cz)) | TrimPlan.Manual;
        }
        foreach (var kv in plan.Chunks)
        {
            if (plan.Mask.GetValueOrDefault(kv.Key) == 0) { plan.Delete++; plan.BytesDelete += kv.Value.Bytes; }
            else plan.Kept++;
        }
        return plan;
    }

    /// <summary>Remove the given chunks (world chunk coordinates) from their region files. Regions with damaged chunks are skipped.</summary>
    public static string RemoveChunks(SaveCtx ctx, IEnumerable<(int cx, int cz)> chunks)
    {
        var byRegion = chunks.GroupBy(c => (RegionIO.RegionOf(c.cx), RegionIO.RegionOf(c.cz))).ToList();
        using var batch = ctx.BeginBatch("trim-world");
        int removed = 0, files = 0, deletedFiles = 0, skipped = 0; long before = 0, after = 0;
        var gone = new List<(int, int)>();
        foreach (var grp in byRegion)
        {
            var rel = WorldEdit.RegionRel(grp.Key.Item1, grp.Key.Item2);
            if (!File.Exists(ctx.Abs(rel))) continue;
            var data = ctx.ReadAll(rel);
            var sc = RegionIO.Scan(data, grp.Key.Item1, grp.Key.Item2, false, false);
            if (sc.Bad > 0 || sc.Issues.Any(i => i.Sev >= Sev.Error)) { skipped++; continue; }
            var drop = grp.Select(c => RegionIO.SlotIndex(c.cx, c.cz)).ToHashSet();
            var keep = sc.Slots.Where(s => !drop.Contains(s.Index) && s.Blob != null).Select(s => (s.Index, s.Blob!, s.Timestamp)).ToList();
            int n = sc.Slots.Count - keep.Count;
            if (n == 0) continue;
            before += data.Length;
            if (keep.Count == 0) { ctx.DeleteFile(rel, "trim-world"); deletedFiles++; }
            else { var rebuilt = RegionIO.Build(keep); ctx.WriteFile(rel, rebuilt, "trim-world"); after += rebuilt.Length; }
            RegionHandler.Invalidate(ctx, rel);
            removed += n; files++;
            foreach (var s in sc.Slots.Where(s => drop.Contains(s.Index))) gone.Add((s.ChunkX, s.ChunkZ));
        }
        CleanMultiBlocks(ctx, gone);
        return $"Removed {removed:N0} chunk(s) from {files} region file(s)" + (deletedFiles > 0 ? $" ({deletedFiles} became empty and were deleted)" : "") + $"; saved {BinUtil.Size(Math.Max(0, before - after))}." + (skipped > 0 ? $" {skipped} region(s) with damaged chunks were skipped – repair them first." : "");
    }

    static void CleanMultiBlocks(SaveCtx ctx, List<(int, int)> gone)
    {
        if (gone.Count == 0 || !File.Exists(ctx.Abs("multiblocks.7dt"))) return;
        try
        {
            var set = gone.ToHashSet();
            var list = MultiBlockRec.Parse(ctx.ReadAll("multiblocks.7dt"));
            int n = list.RemoveAll(r => set.Contains((r.X >> 4, r.Z >> 4)));
            if (n > 0) ctx.WriteFile("multiblocks.7dt", MultiBlockRec.Build(list), "trim-world");
        }
        catch { }
    }

    public static string RemoveDamagedEverywhere(SaveCtx ctx)
    {
        int regions = 0, chunks = 0;
        using var batch = ctx.BeginBatch("remove-damaged-chunks");
        foreach (var rel in ctx.ListFilesRel().Where(r => Regex.IsMatch(r, @"^Region/r\.-?\d+\.-?\d+\.7rg$", RegexOptions.IgnoreCase)))
        {
            RegionIO.TryParseName(rel, out int rx, out int rz);
            var data = ctx.ReadAll(rel);
            var sc = RegionIO.Scan(data, rx, rz, true, false, ctx.Game);
            if (sc.Bad == 0) continue;
            var keep = sc.Slots.Where(s => s.Status != "bad" && s.Blob != null).Select(s => (s.Index, s.Blob!, s.Timestamp)).ToList();
            ctx.WriteFile(rel, RegionIO.Build(keep), "remove-damaged-chunks");
            RegionHandler.Invalidate(ctx, rel);
            regions++; chunks += sc.Bad;
        }
        return regions == 0 ? "No damaged chunks found." : $"Removed {chunks} damaged chunk(s) from {regions} region file(s). The game regenerates them from the world seed.";
    }
}
