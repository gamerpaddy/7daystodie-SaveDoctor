using System.Text.Json;
using SaveDoctor.Core;
using SaveDoctor.Formats;

namespace SaveDoctor;

/// <summary>Endpoints that work on the world as a whole (block editor, containers, search, schematics, trimming …).</summary>
public static partial class Api
{
    const long MaxFillVolume = 4_000_000;
    static readonly Dictionary<string, (DateTime t, Schematic s)> schemCache = new();
    static Schematic SchemCache(string f)
    {
        var t = File.GetLastWriteTimeUtc(f);
        lock (schemCache)
        {
            if (schemCache.TryGetValue(f, out var c) && c.t == t) return c.s;
            var s = Schematic.FromBytes(File.ReadAllBytes(f)); s.Raw = Array.Empty<uint>();
            schemCache[f] = (t, s); return s;
        }
    }

    static void MapWorld(WebApplication app, Func<SaveCtx> Need)
    {
        IResult J(object o) => Results.Json(o);
        MapSearch(app, Need);
        MapHistory(app, Need);
        MapPlayers(app, Need);

        // ───────────── block palette for the editor ─────────────
        app.MapGet("/api/world/palette", () =>
        {
            var c = Need();
            var we = new WorldEdit(c);
            var cat = BlockCatalog.Shared;
            var list = new List<object[]>();
            foreach (var kv in c.Game.BlockNames.OrderBy(k => k.Key))
            {
                if (kv.Key == 0) continue;
                var def = cat.Get(kv.Value);
                string? why = we.PlaceProblem(kv.Key);
                list.Add(new object[] { kv.Key, kv.Value, def?.Group ?? "", why == null ? 1 : 0, def != null && def.Terrain ? 1 : 0 });
            }
            return J(new { catalog = cat.Loaded, blocks = list });
        });

        app.MapGet("/api/world/players", () =>
        {
            var c = Need(); var g = c.Game;
            var list = new List<object>();
            foreach (var f in c.ListFilesRel().Where(r => r.StartsWith("Player/", StringComparison.OrdinalIgnoreCase) && r.EndsWith(".ttp", StringComparison.OrdinalIgnoreCase)))
            {
                try { var p = PlayerFile.Parse(c.ReadAll(f), g); list.Add(new { name = p.Ecd.EntityName, file = f, x = (int)Math.Floor(p.Ecd.PosX), y = (int)Math.Floor(p.Ecd.PosY), z = (int)Math.Floor(p.Ecd.PosZ) }); }
                catch { }
            }
            return J(list);
        });

        // ───────────── world trimming / resets ─────────────
        app.MapPost("/api/world/trim/plan", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            var ix = WorldIndexer.Get(c);
            if (!ix.Done) return J(new { building = true, done = ix.DoneCount, total = ix.Total });
            var plan = TrimOps.Plan(c, ix, Json.De<TrimOptions>(b)!);
            var cells = plan.Chunks.Select(kv => new[] { kv.Key.Item1, kv.Key.Item2, plan.Mask.GetValueOrDefault(kv.Key), plan.PoiChunks.Contains(kv.Key) ? 1 : 0 });
            return J(new
            {
                total = plan.Total, kept = plan.Kept, delete = plan.Delete, bytesTotal = plan.BytesTotal, bytesDelete = plan.BytesDelete, poiNote = plan.PoiNote,
                pois = plan.Pois.Select(p => new { p.Name, p.MinX, p.MinZ, p.MaxX, p.MaxZ, p.Y, p.SizeY }),
                cells,
            });
        });

        app.MapPost("/api/world/trim/apply", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            var ix = WorldIndexer.Get(c);
            if (!ix.Done) throw new InvalidOperationException("The world index is still being built – try again in a moment.");
            var plan = TrimOps.Plan(c, ix, Json.De<TrimOptions>(b)!);
            string msg;
            lock (writeLock) msg = TrimOps.RemoveChunks(c, plan.Chunks.Keys.Where(k => plan.Mask.GetValueOrDefault(k) == 0));
            WorldIndexer.Invalidate();
            Log("", "trim-world", msg);
            return J(new { ok = true, message = msg });
        });

        app.MapPost("/api/world/trim/rect", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            int x1 = b.GetProperty("x1").GetInt32(), z1 = b.GetProperty("z1").GetInt32(), x2 = b.GetProperty("x2").GetInt32(), z2 = b.GetProperty("z2").GetInt32();
            if (x1 > x2) (x1, x2) = (x2, x1); if (z1 > z2) (z1, z2) = (z2, z1);
            string what = b.GetProperty("what").GetString()!;
            string msg;
            lock (writeLock)
            {
                if (what == "loot") msg = ContainerOps.ResetLoot(c, x1, z1, x2, z2, false);
                else
                {
                    var ix = WorldIndexer.Get(c);
                    if (!ix.Done) throw new InvalidOperationException("The world index is still being built – try again in a moment.");
                    var list = ix.Stats.Where(s => s.Cx >= (x1 >> 4) && s.Cx <= (x2 >> 4) && s.Cz >= (z1 >> 4) && s.Cz <= (z2 >> 4)).Select(s => (s.Cx, s.Cz)).ToList();
                    msg = list.Count == 0 ? "No saved chunks in that area." : TrimOps.RemoveChunks(c, list);
                }
            }
            WorldIndexer.Invalidate();
            Log("", "reset-area", $"{what} {x1},{z1}…{x2},{z2}: {msg}");
            return J(new { ok = true, message = msg });
        });

        app.MapPost("/api/world/trim/damaged", () =>
        {
            var c = Need(); string msg;
            lock (writeLock) msg = TrimOps.RemoveDamagedEverywhere(c);
            WorldIndexer.Invalidate();
            Log("", "remove-damaged", msg);
            return J(new { ok = true, message = msg });
        });

        // ───────────── schematics ─────────────
        app.MapGet("/api/schem/list", () =>
        {
            Need();
            var list = new List<object>();
            foreach (var f in Directory.EnumerateFiles(Schematic.LibraryDir(), "*.sdschem").OrderByDescending(f => File.GetLastWriteTime(f)))
            {
                try
                {
                    var sc = SchemCache(f);
                    list.Add(new { name = Path.GetFileNameWithoutExtension(f), w = sc.W, h = sc.H, d = sc.D, blocks = sc.BlockCount, tes = sc.Tes.Count, source = sc.Source, created = sc.Created, size = new FileInfo(f).Length, palette = sc.Palette.Skip(1).Take(40) });
                }
                catch (Exception ex) { list.Add(new { name = Path.GetFileNameWithoutExtension(f), error = ex.Message }); }
            }
            return J(new { dir = Schematic.LibraryDir(), items = list });
        });

        app.MapPost("/api/schem/copy", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            string name = Schematic.SafeName(b.GetProperty("name").GetString() ?? "schematic");
            var (sc, notes) = SchematicOps.Copy(c, b.GetProperty("x1").GetInt32(), b.GetProperty("y1").GetInt32(), b.GetProperty("z1").GetInt32(), b.GetProperty("x2").GetInt32(), b.GetProperty("y2").GetInt32(), b.GetProperty("z2").GetInt32(), name);
            var path = Path.Combine(Schematic.LibraryDir(), name + ".sdschem");
            if (File.Exists(path) && !(b.TryGetProperty("overwrite", out var ow) && ow.ValueKind == JsonValueKind.True)) throw new InvalidOperationException($"A schematic called \"{name}\" already exists – choose another name.");
            File.WriteAllBytes(path, sc.ToBytes());
            Log("", "schematic-copy", $"{name}: {sc.W}×{sc.H}×{sc.D}, {sc.BlockCount} blocks, {sc.Tes.Count} tile entities");
            return J(new { ok = true, message = $"Saved schematic \"{name}\": {sc.W}×{sc.H}×{sc.D} blocks, {sc.BlockCount:N0} placed blocks, {sc.Tes.Count} tile entities (containers, lamps…).", notes });
        });

        app.MapGet("/api/schem/download", (HttpContext http) =>
        {
            var path = Path.Combine(Schematic.LibraryDir(), Schematic.SafeName(Q(http, "name")) + ".sdschem");
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(File.ReadAllBytes(path), "application/octet-stream", Path.GetFileName(path));
        });

        app.MapPost("/api/schem/upload", async (HttpContext http) =>
        {
            Need();
            using var ms = new MemoryStream(); await http.Request.Body.CopyToAsync(ms);
            var sc = Schematic.FromBytes(ms.ToArray());
            string name = Schematic.SafeName(Q(http, "name", sc.Name));
            var path = Path.Combine(Schematic.LibraryDir(), name + ".sdschem");
            if (File.Exists(path)) throw new InvalidOperationException($"A schematic called \"{name}\" already exists – rename the file first.");
            sc.Name = name;
            File.WriteAllBytes(path, sc.ToBytes());
            return J(new { ok = true, message = $"Imported \"{name}\" ({sc.W}×{sc.H}×{sc.D}, {sc.BlockCount:N0} blocks)." });
        });

        app.MapPost("/api/schem/delete", async (HttpContext http) =>
        {
            Need(); var b = await Body(http);
            var path = Path.Combine(Schematic.LibraryDir(), Schematic.SafeName(b.GetProperty("name").GetString()!) + ".sdschem");
            if (File.Exists(path)) File.Delete(path);
            return J(new { ok = true });
        });

        app.MapPost("/api/schem/paste", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            string name = Schematic.SafeName(b.GetProperty("name").GetString()!);
            var path = Path.Combine(Schematic.LibraryDir(), name + ".sdschem");
            if (!File.Exists(path)) throw new InvalidOperationException("Schematic not found.");
            var sc = Schematic.FromBytes(File.ReadAllBytes(path));
            bool dry = b.TryGetProperty("dryRun", out var dr) && dr.ValueKind == JsonValueKind.True;
            bool air = b.TryGetProperty("replaceAir", out var ra) && ra.ValueKind == JsonValueKind.True;
            int x = b.GetProperty("x").GetInt32(), y = b.GetProperty("y").GetInt32(), z = b.GetProperty("z").GetInt32();
            SchematicOps.PasteResult res;
            lock (writeLock) res = SchematicOps.Paste(c, sc, x, y, z, air, dry);
            if (!dry) { WorldIndexer.Invalidate(); Log("", "schematic-paste", $"{name} at {x},{y},{z}: {res.Message}"); }
            return J(new { ok = true, dryRun = dry, message = res.Message, placed = res.Placed, cleared = res.Cleared, skipped = res.Skipped, tes = res.TeCopied, notes = res.Notes });
        });

        // ───────────── block edits (batch) ─────────────
        app.MapPost("/api/world/edit", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            string msg;
            lock (writeLock)
            {
                var we = new WorldEdit(c);
                int skipped = 0;
                var notes = new List<string>();
                foreach (var op in b.GetProperty("ops").EnumerateArray())
                    skipped += ApplyBlockOp(we, op, notes);
                if (we.BlocksChanged == 0) msg = "Nothing changed" + (skipped > 0 ? $" ({skipped} position(s) skipped: " + string.Join("; ", notes.Distinct().Take(3)) + ")" : ".");
                else
                {
                    msg = we.Commit("edit-blocks");
                    if (skipped > 0) msg += $" {skipped} position(s) skipped (" + string.Join("; ", notes.Distinct().Take(3)) + ").";
                    foreach (var n in we.Notes) msg += " " + n;
                }
            }
            Log("", "edit-blocks", msg);
            return J(new { ok = true, message = msg });
        });
    }

    /// <summary>Apply one block edit op to the pending world edit. Returns the number of positions that could not be edited.</summary>
    static int ApplyBlockOp(WorldEdit we, JsonElement op, List<string> notes)
    {
        string kind = op.GetProperty("op").GetString()!;
        int type = op.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
        int rot = op.TryGetProperty("rot", out var r) ? r.GetInt32() : 0;
        if (type != 0 && we.PlaceProblem(type) is string why) throw new InvalidOperationException(why);
        int skipped = 0;
        switch (kind)
        {
            case "set":
                {
                    int x = op.GetProperty("x").GetInt32(), y = op.GetProperty("y").GetInt32(), z = op.GetProperty("z").GetInt32();
                    if (!we.Place(x, y, z, type, rot)) { skipped++; notes.Add("chunk does not exist"); }
                    break;
                }
            case "fill":
                {
                    int x1 = op.GetProperty("x1").GetInt32(), y1 = op.GetProperty("y1").GetInt32(), z1 = op.GetProperty("z1").GetInt32();
                    int x2 = op.GetProperty("x2").GetInt32(), y2 = op.GetProperty("y2").GetInt32(), z2 = op.GetProperty("z2").GetInt32();
                    if (x1 > x2) (x1, x2) = (x2, x1); if (y1 > y2) (y1, y2) = (y2, y1); if (z1 > z2) (z1, z2) = (z2, z1);
                    y1 = Math.Max(0, y1); y2 = Math.Min(255, y2);
                    long vol = (long)(x2 - x1 + 1) * (y2 - y1 + 1) * (z2 - z1 + 1);
                    if (vol > MaxFillVolume) throw new InvalidOperationException($"Fill volume {vol:N0} is larger than the limit of {MaxFillVolume:N0} blocks.");
                    string only = op.TryGetProperty("only", out var o) && o.ValueKind != JsonValueKind.Null ? o.ToString() : "any";
                    int onlyType = int.TryParse(only, out int ot) ? ot : -1;
                    for (int y = y1; y <= y2; y++)
                        for (int z = z1; z <= z2; z++)
                            for (int x = x1; x <= x2; x++)
                            {
                                if (!we.TryGet(x, y, z, out uint cur)) { skipped++; notes.Add("chunk does not exist"); continue; }
                                int ct = ChunkDoc.TypeOf(cur);
                                bool match = only switch { "any" => true, "air" => ct == 0, "solid" => ct != 0, _ => ct == onlyType };
                                if (!match) continue;
                                if (type == 0 && ct == 0) continue;
                                if (ct == type && (cur >> 16 & 31) == (uint)(rot & 31) && !ChunkDoc.IsChild(cur)) continue;
                                we.Place(x, y, z, type, rot);
                            }
                    break;
                }
            default: throw new InvalidOperationException("Unknown block op " + kind);
        }
        return skipped;
    }
}
