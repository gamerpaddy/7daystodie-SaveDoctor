using System.Collections.Concurrent;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class CheckIssue
{
    public string Code = "";
    public string Severity = "warn";       // info | warn | error
    public string Message = "";
    public int X, Y, Z, Cx, Cz, Index = -1;
    public string Detail = "";
}

public sealed class CheckResult
{
    public bool Done;
    public int DoneRegions, TotalRegions, Chunks;
    public string? Error;
    public string Stamp = "";
    public long Millis;
    public List<CheckIssue> Issues = new();
}

/// <summary>Cross-checks of tile entities, entities and block ids inside the chunks – things the game tolerates silently but that point at damage, removed mods or bad edits.</summary>
public static class WorldCheck
{
    static readonly object gate = new();
    static CheckResult? cur; static string curRoot = "";

    public static CheckResult Get(SaveCtx ctx, bool force)
    {
        lock (gate)
        {
            string stamp = Directory.Exists(Path.Combine(ctx.Root, "Region")) ? WorldIndexer.StampOf(ctx) : "";
            if (!force && cur != null && curRoot == ctx.Root && (cur.Stamp == stamp || !cur.Done)) return cur;
            var r = new CheckResult { Stamp = stamp };
            cur = r; curRoot = ctx.Root;
            Task.Run(() => Run(ctx, r));
            return r;
        }
    }

    public static void Invalidate() { lock (gate) cur = null; }

    static void Run(SaveCtx ctx, CheckResult res)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var g = ctx.Game; var cat = BlockCatalog.For(ctx);
            var compositeBlock = new bool[65536];
            foreach (var kv in g.BlockNames) if (kv.Key > 0 && kv.Key < 65536 && cat.Get(kv.Value) is { CompositeTe: true }) compositeBlock[kv.Key] = true;
            var files = Directory.EnumerateFiles(Path.Combine(ctx.Root, "Region"), "r.*.7rg").Where(f => RegionIO.TryParseName(f, out _, out _)).OrderBy(f => f).ToList();
            res.TotalRegions = files.Count;
            var issues = new ConcurrentBag<CheckIssue>();
            var entIds = new ConcurrentDictionary<int, (int cx, int cz, int idx, string name)>();
            var dupEnt = new ConcurrentBag<(int id, int cx, int cz, int idx, string name, int fcx, int fcz)>();
            int chunks = 0;
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, file =>
            {
                RegionIO.TryParseName(file, out int rx, out int rz);
                byte[] data; try { data = File.ReadAllBytes(file); } catch { return; }
                var sc = RegionIO.Scan(data, rx, rz, false, false);
                foreach (var s in sc.Slots)
                {
                    if (s.Blob == null || s.Status == "bad") continue;
                    ChunkDoc d; try { d = ChunkDoc.Parse(s.Blob, g); } catch { continue; }
                    Interlocked.Increment(ref chunks);
                    int bx = d.X * 16, bz = d.Z * 16;
                    var unknownBlocks = new Dictionary<int, int>();
                    for (int i = 0; i < d.Blocks.Length; i++)
                    {
                        int t = (int)(d.Blocks[i] & 0xFFFF);
                        if (t == 0) continue;
                        if (!g.BlockNames.ContainsKey(t) && g.BlockNames.Count > 0) unknownBlocks[t] = unknownBlocks.GetValueOrDefault(t) + 1;
                    }
                    foreach (var kv in unknownBlocks) issues.Add(new CheckIssue { Code = "unknown-block", Severity = "info", Message = $"{kv.Value}× block id {kv.Key} is not in blockmappings.nim (removed mod?)", Cx = d.X, Cz = d.Z, X = bx + 8, Z = bz + 8, Detail = kv.Key.ToString() });
                    if (!d.TailParsed) { issues.Add(new CheckIssue { Code = "tail-unreadable", Severity = "warn", Message = "Tile entities / entities of this chunk cannot be read: " + d.TailNote, Cx = d.X, Cz = d.Z, X = bx + 8, Z = bz + 8 }); continue; }
                    var seen = new HashSet<(int, int, int)>();
                    foreach (var te in d.Tes)
                    {
                        if (!te.HasPos) continue;
                        int wx = bx + te.X, wz = bz + te.Z;
                        if (te.X < 0 || te.X > 15 || te.Z < 0 || te.Z > 15 || te.Y < 0 || te.Y > 255) { issues.Add(new CheckIssue { Code = "te-out-of-chunk", Severity = "error", Message = $"Tile entity ({te.X},{te.Y},{te.Z}) lies outside its chunk", Cx = d.X, Cz = d.Z, X = wx, Y = te.Y, Z = wz }); continue; }
                        if (!seen.Add((te.X, te.Y, te.Z))) issues.Add(new CheckIssue { Code = "te-duplicate", Severity = "error", Message = "Two tile entities share this position", Cx = d.X, Cz = d.Z, X = wx, Y = te.Y, Z = wz });
                        int bt = (int)(d.Blocks[d.Idx(te.X, te.Y, te.Z)] & 0xFFFF);
                        if (bt == 0) issues.Add(new CheckIssue { Code = "te-orphan", Severity = "warn", Message = $"{(te.Composite ? g.NameOf(te.BlockId) : LegacyTe.TypeName(te.Type))} tile entity has no block (the block was removed)", Cx = d.X, Cz = d.Z, X = wx, Y = te.Y, Z = wz });
                        else if (te.Composite && bt != te.BlockId) issues.Add(new CheckIssue { Code = "te-mismatch", Severity = "warn", Message = $"Tile entity belongs to {g.NameOf(te.BlockId)} but the block here is {g.NameOf(bt)}", Cx = d.X, Cz = d.Z, X = wx, Y = te.Y, Z = wz });
                        if (te.Composite)
                            foreach (var f in te.Feats!)
                                if (TeFeatures.KindOf(f.Hash) == "storage")
                                {
                                    var sd = StorageData.TryRead(f.Payload, g);
                                    if (sd == null) { issues.Add(new CheckIssue { Code = "storage-unreadable", Severity = "warn", Message = "Storage data has an unknown layout", Cx = d.X, Cz = d.Z, X = wx, Y = te.Y, Z = wz }); continue; }
                                    int unk = sd.Grid.Items.Count(st => st.Count > 0 && st.Item.Type != 0 && g.NameOf(st.Item.Type).Contains('#'));
                                    if (unk > 0) issues.Add(new CheckIssue { Code = "unknown-item", Severity = "info", Message = $"{g.NameOf(te.BlockId)} holds {unk} stack(s) of items that no longer exist (removed mod)", Cx = d.X, Cz = d.Z, X = wx, Y = te.Y, Z = wz });
                                }
                        if (te.Legacy is VendingM vm)
                        {
                            int unk = vm.Trader.Primary.Count(e => e.Item.Count > 0 && g.NameOf(e.Item.Item.Type).Contains('#'));
                            if (unk > 0) issues.Add(new CheckIssue { Code = "unknown-item", Severity = "info", Message = $"Vending machine stocks {unk} item(s) that no longer exist", Cx = d.X, Cz = d.Z, X = wx, Y = te.Y, Z = wz });
                            if (vm.Trader.Primary.Any(e => e.Markup < -4 || e.Markup > 100)) issues.Add(new CheckIssue { Code = "vending-markup", Severity = "warn", Message = "Vending machine has a price markup outside −4 … 100", Cx = d.X, Cz = d.Z, X = wx, Y = te.Y, Z = wz });
                        }
                    }
                    // composite blocks that should own a tile entity but have none
                    var teAt = new HashSet<int>(d.Tes.Where(t => t.HasPos).Select(t => d.Idx(t.X, t.Y, t.Z)));
                    int missing = 0; int fx = 0, fy = 0, fz = 0; var missNames = new Dictionary<string, int>();
                    for (int i = 0; i < d.Blocks.Length; i++)
                    {
                        uint v = d.Blocks[i]; int t = (int)(v & 0xFFFF);
                        if (t == 0 || !compositeBlock[t] || (v & 0x40000000u) != 0 || teAt.Contains(i)) continue;
                        if (missing++ == 0) { fx = i & 15; fz = (i >> 4) & 15; fy = i >> 8; }
                        var mn = g.NameOf(t); missNames[mn] = missNames.GetValueOrDefault(mn) + 1;
                    }
                    if (missing > 0) issues.Add(new CheckIssue { Code = "te-missing", Severity = "warn", Message = $"{missing} block(s) in this chunk should have a tile entity but have none: " + string.Join(", ", missNames.OrderByDescending(k => k.Value).Take(4).Select(k => k.Key + "×" + k.Value)) + " – interacting with them may fail", Cx = d.X, Cz = d.Z, X = bx + fx, Y = fy, Z = bz + fz, Detail = missing.ToString() });
                    // entities
                    for (int ei = 0; ei < d.EntRecs.Count; ei++)
                    {
                        var e = d.EntRecs[ei].E; string nm = g.EntityClassName(e.EntityClass);
                        if (!float.IsFinite(e.PosX) || !float.IsFinite(e.PosY) || !float.IsFinite(e.PosZ)) { issues.Add(new CheckIssue { Code = "entity-nan", Severity = "error", Message = $"{nm} has an invalid (NaN) position", Cx = d.X, Cz = d.Z, Index = ei, X = bx + 8, Z = bz + 8 }); continue; }
                        if (Math.Abs(e.PosX - bx - 8) > 80 || Math.Abs(e.PosZ - bz - 8) > 80) issues.Add(new CheckIssue { Code = "entity-far", Severity = "info", Message = $"{nm} is stored in a chunk more than 5 chunks away from its position", Cx = d.X, Cz = d.Z, Index = ei, X = (int)e.PosX, Y = (int)e.PosY, Z = (int)e.PosZ });
                        if (e.PosY < -5) issues.Add(new CheckIssue { Code = "entity-void", Severity = "warn", Message = $"{nm} has fallen below the world (y {e.PosY:0})", Cx = d.X, Cz = d.Z, Index = ei, X = (int)e.PosX, Y = (int)e.PosY, Z = (int)e.PosZ });
                        if (e.Id > 0 && !entIds.TryAdd(e.Id, (d.X, d.Z, ei, nm))) dupEnt.Add((e.Id, d.X, d.Z, ei, nm, entIds[e.Id].cx, entIds[e.Id].cz));
                    }
                }
                lock (res) res.DoneRegions++;
            });
            foreach (var du in dupEnt) issues.Add(new CheckIssue { Code = "entity-duplicate-id", Severity = "error", Message = $"{du.name} uses entity id {du.id}, which another entity in chunk ({du.fcx},{du.fcz}) already uses – the game can mix them up", Cx = du.cx, Cz = du.cz, Index = du.idx, X = du.cx * 16 + 8, Z = du.cz * 16 + 8, Detail = du.id.ToString() });
            res.Issues = issues.OrderBy(i => i.Severity == "error" ? 0 : i.Severity == "warn" ? 1 : 2).ThenBy(i => i.Code).ThenBy(i => i.X).ToList();
            res.Chunks = chunks;
        }
        catch (Exception ex) { res.Error = ex.Message; }
        res.Millis = sw.ElapsedMilliseconds; res.Done = true;
    }

    // ───────── fixes
    public static string Fix(SaveCtx ctx, string code)
    {
        var res = Get(ctx, false);
        if (!res.Done) throw new InvalidOperationException("The check is still running.");
        var g = ctx.Game; var we = new WorldEdit(ctx);
        int fixedN = 0;
        var list = res.Issues.Where(i => i.Code == code).ToList();
        if (list.Count == 0) return "Nothing to fix.";
        switch (code)
        {
            case "te-orphan": case "te-mismatch": case "te-duplicate": case "te-out-of-chunk":
                foreach (var grp in list.GroupBy(i => (i.Cx, i.Cz)))
                {
                    var d = we.Chunk(grp.Key.Cx, grp.Key.Cz, true); if (d == null || !d.TailParsed) continue;
                    foreach (var i in grp)
                    {
                        int lx = i.X - d.X * 16, lz = i.Z - d.Z * 16;
                        var hits = d.Tes.Where(t => t.HasPos && t.X == lx && t.Y == i.Y && t.Z == lz).ToList();
                        if (code == "te-duplicate") hits = hits.Skip(1).ToList();
                        else if (code == "te-mismatch") { int bt = (int)(d.Blocks[d.Idx(lx, i.Y, lz)] & 0xFFFF); hits = hits.Where(t => t.Composite && t.BlockId != bt).ToList(); }
                        foreach (var t in hits) { d.Tes.Remove(t); fixedN++; }
                    }
                    we.MarkDirty(d.X, d.Z);
                }
                break;
            case "unknown-item":
                foreach (var grp in list.GroupBy(i => (i.Cx, i.Cz)))
                {
                    var d = we.Chunk(grp.Key.Cx, grp.Key.Cz, true); if (d == null || !d.TailParsed) continue;
                    bool any = false;
                    foreach (var t in d.Tes)
                    {
                        if (t.Composite)
                            foreach (var f in t.Feats!.Where(f => TeFeatures.KindOf(f.Hash) == "storage"))
                            {
                                var sd = StorageData.TryRead(f.Payload, g); if (sd == null) continue;
                                int n = 0; foreach (var st in sd.Grid.Items) if (st.Count > 0 && g.NameOf(st.Item.Type).Contains('#')) { st.Count = 0; st.Item = new ItemVal(); n++; }
                                if (n > 0) { f.Payload = sd.Write(); t.Dirty = true; any = true; fixedN += n; }
                            }
                        if (t.Legacy is VendingM vm)
                        {
                            int n = vm.Trader.Primary.RemoveAll(e => e.Item.Count > 0 && g.NameOf(e.Item.Item.Type).Contains('#'));
                            if (n > 0) { t.Dirty = true; any = true; fixedN += n; }
                        }
                    }
                    if (any) we.MarkDirty(d.X, d.Z);
                }
                break;
            case "entity-duplicate-id": case "entity-nan": case "entity-void":
                foreach (var grp in list.GroupBy(i => (i.Cx, i.Cz)))
                {
                    var d = we.Chunk(grp.Key.Cx, grp.Key.Cz, true); if (d == null || !d.TailParsed) continue;
                    foreach (var i in grp.OrderByDescending(i => i.Index)) if (i.Index >= 0 && i.Index < d.EntRecs.Count) { d.EntRecs.RemoveAt(i.Index); d.EntitiesDirty = true; fixedN++; }
                    we.MarkDirty(d.X, d.Z);
                }
                break;
            default: throw new InvalidOperationException("There is no automatic fix for \"" + code + "\".");
        }
        if (fixedN == 0) return "Nothing was changed.";
        var msg = we.Commit("fix-" + code);
        WorldIndexer.Invalidate(); Invalidate();
        return $"Fixed {fixedN} item(s). " + msg;
    }
}
