using System.Text.Json;
using SaveDoctor.Core;
using SaveDoctor.Formats;

namespace SaveDoctor;

/// <summary>Objects tab / block inspector: block state, tile entities (vending machines, workstations, signs …), entities lying around, drawn signs.</summary>
public static partial class Api
{
    static readonly JsonSerializerOptions WebOpt = new(JsonSerializerDefaults.Web);

    static ChunkDoc NeedChunk(WorldEdit we, int x, int z, bool write)
        => we.ChunkAt(x, z, write) ?? throw new InvalidOperationException("That chunk does not exist in the save.");

    static void MapObjects(WebApplication app, Func<SaveCtx> Need)
    {
        IResult J(object o) => Results.Json(o);

        // ───────── list from the world index
        app.MapGet("/api/world/objects", (HttpContext http) =>
        {
            var c = Need(); var g = c.Game;
            var ix = WorldIndexer.Get(c);
            if (!ix.Done) return Indexing(ix);
            var kind = Q(http, "kind"); var q = Q(http, "q").Trim();
            var counts = ix.Objects.GroupBy(o => o.Kind).ToDictionary(gp => gp.Key, gp => gp.Count());
            var list = ix.Objects.Where(o => (kind.Length == 0 || o.Kind == kind) && (q.Length == 0 || o.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || o.Detail.Contains(q, StringComparison.OrdinalIgnoreCase) || (o.Owner?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Take(1000).Select(o => new { o.Kind, o.Name, o.Detail, o.X, o.Y, o.Z, o.Cx, o.Cz, o.Index, o.Count, o.Owner, o.Flag });
            return J(new { counts, results = list });
        });

        // ───────── inspector
        app.MapGet("/api/world/inspect", (HttpContext http) =>
        {
            var c = Need(); var g = c.Game;
            int x = int.Parse(Q(http, "x")), y = int.Parse(Q(http, "y")), z = int.Parse(Q(http, "z"));
            var we = new WorldEdit(c);
            var d = NeedChunk(we, x, z, false);
            if (y < 0 || y > 255) throw new InvalidOperationException("y must be 0–255");
            int lx = x & 15, lz = z & 15, i = d.Idx(lx, y, lz);
            uint v = d.Blocks[i]; int type = ChunkDoc.TypeOf(v);
            object? redirectedFrom = null;
            // the other half of a multi-part block (vending machine, bed, door …) holds no data: show its main block instead
            if (BlockState.Child(v) && d.TileEntityAt(lx, y, lz) == null)
            {
                var (ox, oy, oz) = WorldEdit.ParentOffset(v);
                int nx = x + ox, ny = y + oy, nz = z + oz;
                if (ny >= 0 && ny <= 255)
                {
                    try
                    {
                        var d2 = NeedChunk(we, nx, nz, false);
                        int i2 = d2.Idx(nx & 15, ny, nz & 15);
                        if (ChunkDoc.TypeOf(d2.Blocks[i2]) != 0 && !BlockState.Child(d2.Blocks[i2]))
                        {
                            redirectedFrom = new { x, y, z };
                            x = nx; y = ny; z = nz; d = d2; lx = x & 15; lz = z & 15; i = i2; v = d.Blocks[i]; type = ChunkDoc.TypeOf(v);
                        }
                    }
                    catch (InvalidOperationException) { }
                }
            }
            var def = we.DefOf(type);
            var tex = d.TexturesAt(i);
            var block = new
            {
                id = type, name = g.NameOf(type), raw = v, rotation = BlockState.Rotation(v), meta = BlockState.Meta(v), meta2 = BlockState.Meta2(v), meta3 = BlockState.Meta3(v),
                child = BlockState.Child(v), decal = BlockState.Decal(v), damage = d.DamageAt(i), stability = (int)d.Stability.Planes[0][i], density = (int)(sbyte)d.Density.Planes[0][i], light = (int)d.Light.Planes[0][i],
                water = d.Water.Planes[0][i] | (d.Water.Planes[1][i] << 8), textures = tex, textureNames = tex.Select(t => t == 0 ? "" : g.PaintNames.GetValueOrDefault(t, "paint " + t)).ToArray(),
                def = def == null ? null : new { def.Class, def.Group, def.Material, dim = def.Dim, def.NeedsTileEntity, def.Terrain, def.Shape },
                fields = def == null ? new List<StateField>() : BlockState.Describe(def.Class, v),
            };
            object? te = null;
            var rec = d.TileEntityAt(lx, y, lz);
            if (rec != null)
            {
                te = new
                {
                    type = rec.Type, typeName = LegacyTe.TypeName(rec.Type), composite = rec.Composite, tailParsed = d.TailParsed,
                    owner = rec.Composite ? rec.GetOwner() : null,
                    features = rec.Feats?.Select(f => TeFeatures.Decode(f, g)),
                    legacy = (object?)rec.Legacy, legacyKind = rec.Legacy?.Kind, legacyNote = rec.LegacyNote,
                    bytes = rec.Encode().Length,
                };
            }
            var near = d.EntRecs.Select((e, idx) => (e, idx)).Where(t => Math.Abs(t.e.E.PosX - x - 0.5) < 2.5 && Math.Abs(t.e.E.PosZ - z - 0.5) < 2.5 && Math.Abs(t.e.E.PosY - y) < 3.5).Select(t => EntityTools.Describe(t.e, t.idx, g)).ToList();
            var names = new Dictionary<int, string>();
            if (te != null)
                foreach (System.Text.RegularExpressions.Match mt in System.Text.RegularExpressions.Regex.Matches(JsonSerializer.Serialize(te, WebOpt), @"""type"":(\d+)"))
                {
                    int id = int.Parse(mt.Groups[1].Value);
                    if (id > 0 && (g.ItemNames.ContainsKey(id) || g.BlockNames.ContainsKey(id))) names[id] = g.NameOf(id);
                }
            var econ = names.Keys.Select(k => (k, e: g.EconomyOf(k))).Where(t => t.e != null).ToDictionary(t => t.k, t => new[] { (double)t.e!.Value.value, t.e.Value.bundle });
            return J(new { x, y, z, cx = d.X, cz = d.Z, tailParsed = d.TailParsed, tailNote = d.TailNote, redirectedFrom, block, te, entities = near, names, econ });
        });

        app.MapPost("/api/world/block-state", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            int x = b.GetProperty("x").GetInt32(), y = b.GetProperty("y").GetInt32(), z = b.GetProperty("z").GetInt32();
            string msg;
            lock (writeLock)
            {
                var we = new WorldEdit(c);
                var d = NeedChunk(we, x, z, true);
                int i = d.Idx(x & 15, y, z & 15);
                uint v = d.Blocks[i];
                if (ChunkDoc.TypeOf(v) == 0) throw new InvalidOperationException("There is no block at that position.");
                int? I(string n) => b.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
                var def = we.DefOf(ChunkDoc.TypeOf(v));
                if (b.TryGetProperty("fields", out var ff) && ff.ValueKind == JsonValueKind.Object)
                    v = BlockState.Apply(v, def?.Class ?? "", ff.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.True ? 1 : p.Value.ValueKind == JsonValueKind.False ? 0 : p.Value.GetInt32()));
                v = BlockState.With(v, rot: I("rotation"), meta: I("meta"), meta2: I("meta2"), meta3: I("meta3"), decal: b.TryGetProperty("decal", out var dc) && dc.ValueKind is JsonValueKind.True or JsonValueKind.False ? dc.GetBoolean() : null);
                d.Blocks[i] = v;
                if (I("damage") is int dmg) d.SetDamage(i, Math.Clamp(dmg, 0, 65535));
                if (I("stability") is int st) d.Stability.Planes[0][i] = (byte)Math.Clamp(st, 0, 15);
                if (b.TryGetProperty("textures", out var tx) && tx.ValueKind == JsonValueKind.Array) d.SetTextures(i, tx.EnumerateArray().Select(t => Math.Clamp(t.GetInt32(), 0, 255)).ToArray());
                we.MarkDirty(d.X, d.Z);
                msg = we.Commit("edit-block-state").Replace("0 block(s) changed in", "Block state saved –");
            }
            WorldIndexer.Invalidate();
            Log("", "edit-block-state", $"{x},{y},{z}: {msg}");
            return J(new { ok = true, message = msg });
        });

        // ───────── tile entity edit: owner, composite features, legacy model
        app.MapPost("/api/world/te", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http); var g = c.Game;
            int x = b.GetProperty("x").GetInt32(), y = b.GetProperty("y").GetInt32(), z = b.GetProperty("z").GetInt32();
            string msg;
            lock (writeLock)
            {
                var we = new WorldEdit(c);
                var d = NeedChunk(we, x, z, true);
                if (!d.TailParsed) throw new InvalidOperationException("This chunk's tile entities cannot be edited safely: " + d.TailNote);
                var te = d.TileEntityAt(x & 15, y, z & 15) ?? throw new InvalidOperationException("There is no tile entity at that position.");
                int changed = 0;
                if (b.TryGetProperty("owner", out var ow))
                {
                    if (!te.Composite) throw new InvalidOperationException("Only composite tile entities carry an owner.");
                    te.SetOwner(ow.ValueKind == JsonValueKind.Null ? null : ow.Deserialize<PlatformId>(WebOpt)); changed++;
                }
                if (b.TryGetProperty("features", out var fs) && fs.ValueKind == JsonValueKind.Object)
                    foreach (var p in fs.EnumerateObject())
                    {
                        int hash = int.Parse(p.Name);
                        var f = te.Feats?.FirstOrDefault(f => f.Hash == hash) ?? throw new InvalidOperationException("Feature not found.");
                        TeFeatures.Apply(f, p.Value, g); te.Dirty = true; changed++;
                    }
                if (b.TryGetProperty("legacy", out var lg) && lg.ValueKind == JsonValueKind.Object)
                {
                    if (te.Legacy == null) throw new InvalidOperationException("This tile entity has no editable data (" + (te.LegacyNote ?? "unsupported type") + ").");
                    ILegacyTe model = te.Legacy switch
                    {
                        VendingM => lg.Deserialize<VendingM>(WebOpt)!,
                        WorkstationM => lg.Deserialize<WorkstationM>(WebOpt)!,
                        ForgeM => lg.Deserialize<ForgeM>(WebOpt)!,
                        CollectorM => lg.Deserialize<CollectorM>(WebOpt)!,
                        _ => throw new InvalidOperationException("Not editable."),
                    };
                    if (model is VendingM vm) vm.Rentable = g.RentableTraders.Contains(vm.Trader.TraderId);
                    var old = te.Legacy; te.Legacy = model; te.Dirty = true;
                    // the written bytes must read back as the same model type, otherwise nothing is saved
                    var bytes = LegacyTe.Encode(te);
                    using (var rd = BinUtil.Reader(bytes)) { var probe = TeRec.ReadOne(rd, bytes, 0, g); if (probe.Legacy == null) { te.Legacy = old; throw new InvalidOperationException("The edited values could not be written back safely."); } }
                    changed++;
                }
                if (changed == 0) throw new InvalidOperationException("Nothing to change.");
                we.MarkDirty(d.X, d.Z);
                msg = we.Commit("edit-tile-entity").Replace("0 block(s) changed in", "Tile entity saved –");
            }
            WorldIndexer.Invalidate();
            Log("", "edit-tile-entity", $"{x},{y},{z}: {msg}");
            return J(new { ok = true, message = msg });
        });

        // ───────── entities lying around / traders
        app.MapGet("/api/world/entity", (HttpContext http) =>
        {
            var c = Need();
            var we = new WorldEdit(c);
            int cx = int.Parse(Q(http, "cx")), cz = int.Parse(Q(http, "cz")), idx = int.Parse(Q(http, "index"));
            var d = we.Chunk(cx, cz, false) ?? throw new InvalidOperationException("That chunk does not exist.");
            if (!d.TailParsed || idx < 0 || idx >= d.EntRecs.Count) throw new InvalidOperationException("Entity not found (the world changed – refresh the list).");
            var names = new Dictionary<int, string>();
            var det = EntityTools.Describe(d.EntRecs[idx], idx, c.Game);
            void N(ItemVal? it) { if (it == null || it.Type == 0) return; names[it.Type] = c.Game.NameOf(it.Type); if (it.Mods != null) foreach (var m in it.Mods) N(m); if (it.Cosmetics != null) foreach (var m in it.Cosmetics) N(m); }
            if (det.DroppedItem != null) N(det.DroppedItem.Item);
            if (det.Inventory != null) foreach (var s in det.Inventory.Items) N(s.Item);
            if (det.Trader != null) { foreach (var e in det.Trader.Primary) N(e.Item.Item); foreach (var t in det.Trader.Tiers) foreach (var s in t) N(s.Item); }
            var econ = names.Keys.Select(k => (k, e: c.Game.EconomyOf(k))).Where(t => t.e != null).ToDictionary(t => t.k, t => new[] { (double)t.e!.Value.value, t.e.Value.bundle });
            return J(new { cx, cz, entity = det, names, econ });
        });

        app.MapPost("/api/world/entity", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            int cx = b.GetProperty("cx").GetInt32(), cz = b.GetProperty("cz").GetInt32(), idx = b.GetProperty("index").GetInt32();
            bool del = b.TryGetProperty("delete", out var dl) && dl.ValueKind == JsonValueKind.True;
            string msg;
            lock (writeLock)
            {
                var we = new WorldEdit(c);
                var d = we.Chunk(cx, cz, true) ?? throw new InvalidOperationException("That chunk does not exist.");
                if (!d.TailParsed || idx < 0 || idx >= d.EntRecs.Count) throw new InvalidOperationException("Entity not found (the world changed – refresh the list).");
                if (del) { d.EntRecs.RemoveAt(idx); d.EntitiesDirty = true; }
                else EntityTools.Apply(d.EntRecs[idx], b.GetProperty("edit").Deserialize<EntityEdit>(WebOpt)!, c.Game);
                we.MarkDirty(cx, cz);
                msg = we.Commit(del ? "delete-entity" : "edit-entity").Replace("0 block(s) changed in", del ? "Entity removed –" : "Entity saved –");
            }
            WorldIndexer.Invalidate();
            Log("", del ? "delete-entity" : "edit-entity", msg);
            return J(new { ok = true, message = msg });
        });


        // ───────── consistency check
        app.MapGet("/api/world/check", (HttpContext http) =>
        {
            var c = Need();
            var r = WorldCheck.Get(c, Q(http, "force") == "1");
            if (!r.Done) return J(new { building = true, done = r.DoneRegions, total = r.TotalRegions });
            var byCode = r.Issues.GroupBy(i => i.Code).Select(gp => new { code = gp.Key, severity = gp.First().Severity, count = gp.Count(), sample = gp.Take(200) }).OrderBy(x => x.severity == "error" ? 0 : x.severity == "warn" ? 1 : 2).ToList();
            return J(new { building = false, error = r.Error, chunks = r.Chunks, seconds = r.Millis / 1000.0, total = r.Issues.Count, groups = byCode });
        });

        app.MapPost("/api/world/check/fix", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            string msg; lock (writeLock) msg = WorldCheck.Fix(c, b.GetProperty("code").GetString()!);
            Log("", "world-check-fix", msg);
            return J(new { ok = true, message = msg });
        });

        // one block column of the region view: what is stacked at x,z
        app.MapGet("/api/world/column", (HttpContext http) =>
        {
            var c = Need(); var g = c.Game;
            int x = int.Parse(Q(http, "x")), z = int.Parse(Q(http, "z"));
            var we = new WorldEdit(c);
            var d = NeedChunk(we, x, z, false);
            int lx = x & 15, lz = z & 15;
            var runs = new List<object>(); int top = -1, runType = -1, runTop = 0;
            for (int y = 255; y >= -1; y--)
            {
                int t = y >= 0 ? ChunkDoc.TypeOf(d.Blocks[d.Idx(lx, y, lz)]) : 0;
                if (t != runType)
                {
                    if (runType > 0) runs.Add(new { y2 = runTop, y1 = y + 1, id = runType, name = g.NameOf(runType) });
                    runType = t; runTop = y; if (t > 0 && top < 0) top = y;
                }
            }
            var tes = d.TailParsed ? d.Tes.Where(t => t.HasPos && t.X == lx && t.Z == lz).OrderByDescending(t => t.Y).Select(t => new { y = t.Y, name = g.NameOf(ChunkDoc.TypeOf(d.Blocks[d.Idx(lx, t.Y, lz)])), kind = t.Composite ? "composite" : LegacyTe.TypeName(t.Type) }).ToList() : null;
            var ents = d.EntRecs.Select((e, i) => (e, i)).Where(t => Math.Abs(t.e.E.PosX - x - 0.5) < 2 && Math.Abs(t.e.E.PosZ - z - 0.5) < 2).Select(t => new { t.i, name = g.EntityClassName(t.e.E.EntityClass), x = t.e.E.PosX, y = t.e.E.PosY, z = t.e.E.PosZ, index = t.i }).ToList();
            return J(new { x, z, cx = d.X, cz = d.Z, lx, lz, top, runs, tes = (object?)tes ?? Array.Empty<object>(), entities = ents });
        });

        // ───────── drawn signs
        app.MapGet("/api/world/sign", (HttpContext http) =>
        {
            var c = Need();
            var m = Signs.Find(c, Q(http, "library"), Q(http, "guid"), out var source);
            if (m == null) return J(new { found = false, source });
            return J(new { found = true, source, name = m.Name, guid = m.Guid, modified = m.Modified, layers = m.LayerCount, texts = m.Texts, svg = Signs.ToSvg(m) });
        });

        app.MapGet("/api/signs/list", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            if (!rel.EndsWith(".signs", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Not a sign store.");
            var list = new List<object>();
            foreach (var blob in Signs.ReadStore(c.ReadAll(rel)))
                try { var m = Signs.ReadBlob(blob); list.Add(new { m.Guid, m.Name, m.Modified, m.Sharing, layers = m.LayerCount, texts = m.Texts, svg = Signs.ToSvg(m), bytes = blob.Length }); }
                catch (ParseFail pf) { list.Add(new { error = pf.Message, bytes = blob.Length }); }
            return J(new { signs = list });
        });

        app.MapPost("/api/signs/delete", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            var rel = b.GetProperty("rel").GetString()!; var guid = b.GetProperty("guid").GetString()!;
            if (!rel.EndsWith(".signs", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Not a sign store.");
            string msg;
            lock (writeLock)
            {
                var blobs = Signs.ReadStore(c.ReadAll(rel));
                var keep = blobs.Where(bl => { try { return !Signs.ReadBlob(bl).Guid.Equals(guid, StringComparison.OrdinalIgnoreCase); } catch { return true; } }).ToList();
                if (keep.Count == blobs.Count) throw new InvalidOperationException("Sign not found.");
                c.WriteFile(rel, Signs.BuildStore(keep), "delete-sign"); msg = "Deleted the sign from the store (backup kept).";
            }
            Log(rel, "delete-sign", msg);
            return J(new { ok = true, message = msg });
        });
    }
}
