using System.Text.Json;
using SaveDoctor.Core;
using SaveDoctor.Formats;

namespace SaveDoctor;

/// <summary>World-wide search, statistics, heat maps and container editing endpoints.</summary>
public static partial class Api
{
    static IResult Indexing(WorldIndex ix) => Results.Json(new { building = true, done = ix.DoneCount, total = ix.Total });

    static void MapSearch(WebApplication app, Func<SaveCtx> Need)
    {
        IResult J(object o) => Results.Json(o);

        app.MapGet("/api/world/index", (HttpContext http) =>
        {
            var c = Need();
            var ix = WorldIndexer.Get(c, Q(http, "force") == "1");
            return J(new { building = !ix.Done, done = ix.DoneCount, total = ix.Total, error = ix.Error, chunks = ix.Chunks, regions = ix.Regions, bad = ix.BadChunks, unsplit = ix.UnsplitChunks, containers = ix.Containers.Count, seconds = ix.Millis / 1000.0 });
        });

        app.MapGet("/api/world/stats", () =>
        {
            var c = Need(); var g = c.Game;
            var ix = WorldIndexer.Get(c);
            if (!ix.Done) return Indexing(ix);
            long solid = ix.BlockTotals.Values.Sum();
            string BN(int id) => g.BlockNames.TryGetValue(id, out var n) ? n : "block#" + id;
            var top = ix.BlockTotals.OrderByDescending(kv => kv.Value).Take(80).Select(kv => new { id = kv.Key, name = BN(kv.Key), count = kv.Value, pct = Math.Round(kv.Value * 100.0 / Math.Max(1, solid), 3) });
            var te = ix.TeBlocks.OrderByDescending(kv => kv.Value).Take(60).Select(kv => new { name = kv.Key >= 0 ? BN(kv.Key) : "legacy tile entity type " + (-kv.Key), count = kv.Value });
            var ent = ix.Entities.GroupBy(e => e.cls).OrderByDescending(gp => gp.Count()).Take(60).Select(gp => new { name = g.EntityClassName(gp.Key), count = gp.Count() });
            var minCx = ix.Stats.Count == 0 ? 0 : ix.Stats.Min(s => s.Cx); var maxCx = ix.Stats.Count == 0 ? 0 : ix.Stats.Max(s => s.Cx);
            var minCz = ix.Stats.Count == 0 ? 0 : ix.Stats.Min(s => s.Cz); var maxCz = ix.Stats.Count == 0 ? 0 : ix.Stats.Max(s => s.Cz);
            var items = new Dictionary<int, long>();
            foreach (var cn in ix.Containers) foreach (var it in cn.Items) items[it.type] = items.GetValueOrDefault(it.type) + it.count;
            var topItems = items.OrderByDescending(kv => kv.Value).Take(60).Select(kv => new { name = g.NameOf(kv.Key), count = kv.Value });
            return J(new
            {
                chunks = ix.Chunks, regions = ix.Regions, bad = ix.BadChunks, unsplit = ix.UnsplitChunks, solid,
                bounds = new { minCx, maxCx, minCz, maxCz },
                containers = ix.Containers.Count, containersWithItems = ix.Containers.Count(x => x.Used > 0), containerItems = items.Values.Sum(),
                tileEntities = ix.TeBlocks.Values.Sum(), entities = ix.Entities.Count,
                topBlocks = top, topTileEntities = te, topEntities = ent, topItems,
            });
        });

        // kind: item | block | entity | container
        app.MapGet("/api/world/search", (HttpContext http) =>
        {
            var c = Need(); var g = c.Game;
            var kind = Q(http, "kind", "item"); var q = Q(http, "q").Trim();
            if (q.Length == 0) return J(new { results = Array.Empty<object>(), note = "Type something to search for." });
            bool Hit(string name) => name.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (kind == "item")
            {
                var ix = WorldIndexer.Get(c);
                if (!ix.Done) return Indexing(ix);
                var list = new List<object>();
                foreach (var cn in ix.Containers)
                {
                    var m = cn.Items.Where(i => Hit(g.NameOf(i.type))).ToList();
                    if (m.Count == 0) continue;
                    list.Add(new { where = "container", x = cn.X, y = cn.Y, z = cn.Z, block = g.NameOf(cn.BlockId), loot = cn.Loot, total = m.Sum(i => (long)i.count), matches = m.GroupBy(i => i.type).Select(gp => new { name = g.NameOf(gp.Key), count = gp.Sum(i => (long)i.count), quality = gp.Max(i => i.quality) }) });
                }
                var results = list.OrderByDescending(r => (long)r.GetType().GetProperty("total")!.GetValue(r)!).Take(500).ToList();
                var carriers = new List<object>();
                foreach (var f in c.ListFilesRel().Where(r => r.StartsWith("Player/", StringComparison.OrdinalIgnoreCase) && r.EndsWith(".ttp", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var p = PlayerFile.Parse(c.ReadAll(f), g);
                        foreach (var (label, grid) in new[] { ("toolbelt", p.Inventory.Grid), ("backpack", p.Bag.Grid), ("equipment", p.Equipment.Grid) })
                        {
                            var m = grid.Items.Where(s => s.Count > 0 && Hit(g.NameOf(s.Item.Type))).ToList();
                            if (m.Count > 0) carriers.Add(new { where = "player", player = p.Ecd.EntityName, file = f, slotGroup = label, total = m.Sum(s => (long)s.Count), matches = m.GroupBy(s => s.Item.Type).Select(gp => new { name = g.NameOf(gp.Key), count = gp.Sum(s => (long)s.Count), quality = gp.Max(s => s.Item.Quality) }) });
                        }
                    }
                    catch { }
                }
                foreach (var f in new[] { "vehicles.dat", "drones.dat" })
                {
                    try
                    {
                        if (!File.Exists(c.Abs(f))) continue;
                        var el = EntityListFile.Parse(c.ReadAll(f), g);
                        foreach (var e in el.Entities)
                        {
                            Grid? bag = null;
                            if (f == "vehicles.dat") bag = VehicleDataM.TryParse(e.EntityData, g, out _)?.Bag;
                            else bag = DroneDataM.TryParse(e.EntityData, g, out _)?.Bag;
                            if (bag == null) continue;
                            var m = bag.Items.Where(s => s.Count > 0 && Hit(g.NameOf(s.Item.Type))).ToList();
                            if (m.Count > 0) carriers.Add(new { where = f == "vehicles.dat" ? "vehicle" : "drone", player = g.EntityClassName(e.EntityClass), file = f, x = e.PosX, y = e.PosY, z = e.PosZ, total = m.Sum(s => (long)s.Count), matches = m.GroupBy(s => s.Item.Type).Select(gp => new { name = g.NameOf(gp.Key), count = gp.Sum(s => (long)s.Count), quality = gp.Max(s => s.Item.Quality) }) });
                        }
                    }
                    catch { }
                }
                return J(new { results, carriers, totalContainers = list.Count, truncated = list.Count > results.Count });
            }
            if (kind == "block")
            {
                var ix = WorldIndexer.Get(c);
                if (!ix.Done) return Indexing(ix);
                var ids = g.BlockNames.Where(kv => Hit(kv.Value)).Select(kv => kv.Key).ToHashSet();
                var types = ix.BlockTotals.Where(kv => ids.Contains(kv.Key)).OrderByDescending(kv => kv.Value).Take(100).Select(kv => new { id = kv.Key, name = g.BlockNames[kv.Key], count = kv.Value }).ToList();
                var chunks = ix.Stats.Select(s => (s, n: Enumerable.Range(0, s.Types.Length).Where(i => ids.Contains(s.Types[i])).Sum(i => (long)s.Counts[i]))).Where(t => t.n > 0).OrderByDescending(t => t.n).Take(300)
                    .Select(t => new { cx = t.s.Cx, cz = t.s.Cz, x = t.s.Cx * 16 + 8, z = t.s.Cz * 16 + 8, count = t.n }).ToList();
                return J(new { types, chunks, totalChunks = ix.Stats.Count(s => Enumerable.Range(0, s.Types.Length).Any(i => ids.Contains(s.Types[i]))) });
            }
            if (kind == "entity")
            {
                var ix = WorldIndexer.Get(c);
                if (!ix.Done) return Indexing(ix);
                var hits = ix.Entities.Where(e => Hit(g.EntityClassName(e.cls))).ToList();
                var byName = hits.GroupBy(e => g.EntityClassName(e.cls)).OrderByDescending(gp => gp.Count()).Take(60).Select(gp => new { name = gp.Key, count = gp.Count() }).ToList();
                var list = hits.Take(500).Select(e => new { name = g.EntityClassName(e.cls), x = (int)Math.Floor(e.x), y = (int)Math.Floor(e.y), z = (int)Math.Floor(e.z) }).ToList();
                return J(new { byName, results = list, total = hits.Count });
            }
            if (kind == "container")
            {
                var ix = WorldIndexer.Get(c);
                if (!ix.Done) return Indexing(ix);
                var hits = ix.Containers.Where(cn => Hit(g.NameOf(cn.BlockId)) || (cn.Loot != null && Hit(cn.Loot))).ToList();
                var list = hits.Take(500).Select(cn => new { x = cn.X, y = cn.Y, z = cn.Z, block = g.NameOf(cn.BlockId), loot = cn.Loot, used = cn.Used, slots = cn.W * cn.H, touched = cn.Touched, playerOwned = cn.PlayerOwned }).ToList();
                return J(new { results = list, total = hits.Count });
            }
            throw new InvalidOperationException("Unknown search kind " + kind);
        });

        // heat map cells per chunk
        app.MapGet("/api/world/heat", (HttpContext http) =>
        {
            var c = Need(); var g = c.Game;
            var ix = WorldIndexer.Get(c);
            if (!ix.Done) return Indexing(ix);
            var kind = Q(http, "kind", "solid"); var q = Q(http, "q").Trim();
            var cells = new Dictionary<(int, int), long>();
            bool Hit(string name) => q.Length == 0 || name.Contains(q, StringComparison.OrdinalIgnoreCase);
            switch (kind)
            {
                case "solid":
                    foreach (var s in ix.Stats) cells[(s.Cx, s.Cz)] = s.Solid;
                    break;
                case "block":
                    {
                        var ids = g.BlockNames.Where(kv => Hit(kv.Value)).Select(kv => kv.Key).ToHashSet();
                        foreach (var s in ix.Stats) { long n = 0; for (int i = 0; i < s.Types.Length; i++) if (ids.Contains(s.Types[i])) n += s.Counts[i]; if (n > 0) cells[(s.Cx, s.Cz)] = n; }
                        break;
                    }
                case "entity":
                    foreach (var e in ix.Entities.Where(e => Hit(g.EntityClassName(e.cls)))) { var k = ((int)Math.Floor(e.x) >> 4, (int)Math.Floor(e.z) >> 4); cells[k] = cells.GetValueOrDefault(k) + 1; }
                    break;
                case "container":
                    foreach (var cn in ix.Containers.Where(cn => Hit(g.NameOf(cn.BlockId)))) { var k = (cn.X >> 4, cn.Z >> 4); cells[k] = cells.GetValueOrDefault(k) + 1; }
                    break;
                case "item":
                    foreach (var cn in ix.Containers) { long n = cn.Items.Where(i => Hit(g.NameOf(i.type))).Sum(i => (long)i.count); if (n > 0) { var k = (cn.X >> 4, cn.Z >> 4); cells[k] = cells.GetValueOrDefault(k) + n; } }
                    break;
                case "tileentity":
                    foreach (var s in ix.Stats) if (s.TileEntities > 0) cells[(s.Cx, s.Cz)] = s.TileEntities;
                    break;
                default: throw new InvalidOperationException("Unknown heat map kind " + kind);
            }
            var minCx = ix.Stats.Count == 0 ? 0 : ix.Stats.Min(s => s.Cx); var maxCx = ix.Stats.Count == 0 ? 0 : ix.Stats.Max(s => s.Cx);
            var minCz = ix.Stats.Count == 0 ? 0 : ix.Stats.Min(s => s.Cz); var maxCz = ix.Stats.Count == 0 ? 0 : ix.Stats.Max(s => s.Cz);
            return J(new { minCx, maxCx, minCz, maxCz, max = cells.Count == 0 ? 0 : cells.Values.Max(), total = cells.Values.Sum(), cells = cells.Select(kv => new[] { kv.Key.Item1, kv.Key.Item2, (int)Math.Min(int.MaxValue, kv.Value) }), existing = ix.Stats.Select(s => new[] { s.Cx, s.Cz }) });
        });

        // ───────────── containers ─────────────
        app.MapGet("/api/world/container", (HttpContext http) =>
        {
            var c = Need();
            return J(ContainerOps.Read(c, int.Parse(Q(http, "x")), int.Parse(Q(http, "y")), int.Parse(Q(http, "z"))));
        });

        app.MapPost("/api/world/container", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            int x = b.GetProperty("x").GetInt32(), y = b.GetProperty("y").GetInt32(), z = b.GetProperty("z").GetInt32();
            var grid = Json.De<Grid>(b.GetProperty("grid"))!;
            string? loot = b.TryGetProperty("loot", out var lp) && lp.ValueKind == JsonValueKind.String ? lp.GetString() : null;
            bool reset = b.TryGetProperty("resetLoot", out var rp) && rp.ValueKind == JsonValueKind.True;
            string msg; lock (writeLock) msg = ContainerOps.Write(c, x, y, z, grid, loot, reset);
            WorldIndexer.Invalidate();
            Log("", "edit-container", $"{x},{y},{z}: {msg}");
            return J(new { ok = true, message = msg });
        });

        app.MapPost("/api/world/resetloot", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            int x1 = b.GetProperty("x1").GetInt32(), z1 = b.GetProperty("z1").GetInt32(), x2 = b.GetProperty("x2").GetInt32(), z2 = b.GetProperty("z2").GetInt32();
            if (x1 > x2) (x1, x2) = (x2, x1); if (z1 > z2) (z1, z2) = (z2, z1);
            if ((long)(x2 - x1 + 1) * (z2 - z1 + 1) > 4096L * 4096) throw new InvalidOperationException("Area too large (max 4096 × 4096 blocks per run).");
            string msg; lock (writeLock) msg = ContainerOps.ResetLoot(c, x1, z1, x2, z2, false);
            WorldIndexer.Invalidate();
            Log("", "reset-loot", msg);
            return J(new { ok = true, message = msg });
        });
    }
}
