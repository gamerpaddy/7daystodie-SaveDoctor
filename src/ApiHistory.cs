using System.Text.Json;
using SaveDoctor.Core;
using SaveDoctor.Formats;

namespace SaveDoctor;

/// <summary>Backup history: timeline, comparison with a snapshot, restoring single files / chunks / players, automatic snapshots.</summary>
public static partial class Api
{
    static void MapHistory(WebApplication app, Func<SaveCtx> Need)
    {
        IResult J(object o) => Results.Json(o);

        app.MapGet("/api/history/list", () =>
        {
            var c = Need();
            return J(new { root = c.BackupRoot, items = History.List(c), auto = History.LoadAuto() });
        });

        app.MapPost("/api/history/auto", async (HttpContext http) =>
        {
            Need(); var b = await Body(http);
            var s = new AutoSnapshotSettings { Enabled = b.GetProperty("enabled").GetBoolean(), MinHours = Math.Clamp(b.GetProperty("minHours").GetInt32(), 1, 24 * 30), Keep = Math.Clamp(b.GetProperty("keep").GetInt32(), 1, 50) };
            History.SaveAuto(s);
            return J(new { ok = true });
        });

        app.MapGet("/api/history/diff", (HttpContext http) =>
        {
            var c = Need();
            var names = PlayerNames(c);
            using var a = History.Open(c, Q(http, "a"));
            using var b = History.Open(c, Q(http, "b", "live"));
            var labelRx = new System.Text.RegularExpressions.Regex(@"^Player/([^/]+?)\.ttp$");
            var diff = History.Diff(a, b, rel => { var m = labelRx.Match(rel); return m.Success && names.TryGetValue(m.Groups[1].Value, out var n) ? n : null; });
            return J(new { diff });
        });

        app.MapGet("/api/history/regiondiff", (HttpContext http) =>
        {
            var c = Need(); var g = c.Game; var rel = Q(http, "rel");
            if (!RegionIO.TryParseName(rel, out int rx, out int rz)) throw new InvalidOperationException("Not a region file");
            using var a = History.Open(c, Q(http, "a"));
            using var b = History.Open(c, Q(http, "b", "live"));
            var da = a.Read(rel); var db = b.Read(rel);
            var sa = da == null ? new Dictionary<int, RegionSlot>() : RegionIO.Scan(da, rx, rz, false, false).Slots.Where(s => s.Blob != null).ToDictionary(s => s.Index);
            var sb = db == null ? new Dictionary<int, RegionSlot>() : RegionIO.Scan(db, rx, rz, false, false).Slots.Where(s => s.Blob != null).ToDictionary(s => s.Index);
            var list = new List<object>(); int inspected = 0;
            foreach (var idx in sa.Keys.Union(sb.Keys).OrderBy(i => i))
            {
                bool ina = sa.TryGetValue(idx, out var x), inb = sb.TryGetValue(idx, out var y);
                var (cx, cz) = RegionIO.ChunkFromSlot(rx, rz, idx);
                string st = ina && !inb ? "removed" : !ina && inb ? "added" : BinUtil.SameBytes(x!.Blob!, y!.Blob!) ? "same" : "changed";
                if (st == "same") continue;
                int? blocks = null;
                if (st == "changed" && inspected++ < 300)
                {
                    try
                    {
                        var ca = ChunkDoc.Parse(x!.Blob!, g); var cb = ChunkDoc.Parse(y!.Blob!, g);
                        int n = 0; for (int i = 0; i < 65536; i++) if ((ca.Blocks[i] & 0xFFFF) != (cb.Blocks[i] & 0xFFFF)) n++;
                        blocks = n;
                    }
                    catch { }
                }
                list.Add(new { slot = idx, cx, cz, status = st, blocks });
            }
            return J(new { rx, rz, chunks = list, inA = sa.Count, inB = sb.Count });
        });

        app.MapPost("/api/history/restore", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            var id = b.GetProperty("a").GetString()!; var rel = b.GetProperty("rel").GetString()!;
            using var a = History.Open(c, id);
            var data = a.Read(rel);
            string msg;
            lock (writeLock)
            {
                if (data == null) { c.DeleteFile(rel, "restore-history"); msg = $"{rel} did not exist in that backup – the current file was removed (backed up)."; }
                else { c.WriteFile(rel, data, "restore-history"); msg = $"Restored {rel} from {id}."; }
            }
            RegionHandler.Invalidate(c, rel); WorldIndexer.Invalidate();
            Log(rel, "restore-history", msg);
            return J(new { ok = true, message = msg });
        });

        app.MapPost("/api/history/restore-chunks", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            var id = b.GetProperty("a").GetString()!; var rel = b.GetProperty("rel").GetString()!;
            var slots = b.GetProperty("slots").EnumerateArray().Select(x => x.GetInt32()).ToHashSet();
            if (!RegionIO.TryParseName(rel, out int rx, out int rz)) throw new InvalidOperationException("Not a region file");
            using var a = History.Open(c, id);
            var da = a.Read(rel) ?? throw new InvalidOperationException("That backup does not contain " + rel + ".");
            var src = RegionIO.Scan(da, rx, rz, false, false).Slots.Where(s => s.Blob != null && s.Status != "bad").ToDictionary(s => s.Index);
            string msg;
            lock (writeLock)
            {
                var live = File.Exists(c.Abs(rel)) ? RegionIO.Scan(c.ReadAll(rel), rx, rz, false, false) : new RegionScan { RX = rx, RZ = rz };
                if (live.Bad > 0) throw new InvalidOperationException("The live region file has damaged chunks – repair it first.");
                var final = live.Slots.Where(s => s.Blob != null).ToDictionary(s => s.Index, s => (s.Blob!, s.Timestamp));
                int restored = 0, removed = 0;
                foreach (var s in slots)
                {
                    if (src.TryGetValue(s, out var o)) { final[s] = (o.Blob!, o.Timestamp); restored++; }
                    else if (final.Remove(s)) removed++;
                }
                c.WriteFile(rel, RegionIO.Build(final.Select(kv => (kv.Key, kv.Value.Item1, kv.Value.Item2))), "restore-chunks-history");
                msg = $"Restored {restored} chunk(s) from {id}" + (removed > 0 ? $" and removed {removed} chunk(s) that did not exist then." : ".");
            }
            RegionHandler.Invalidate(c, rel); WorldIndexer.Invalidate();
            Log(rel, "restore-chunks", msg);
            return J(new { ok = true, message = msg });
        });

        // mode: items | position | file
        app.MapPost("/api/history/restore-player", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http); var g = c.Game;
            var id = b.GetProperty("a").GetString()!; var rel = b.GetProperty("rel").GetString()!; var mode = b.GetProperty("mode").GetString()!;
            using var a = History.Open(c, id);
            var old = a.Read(rel) ?? throw new InvalidOperationException("That backup has no such player file.");
            string msg;
            lock (writeLock)
            {
                if (mode == "file") { c.WriteFile(rel, old, "restore-player-history"); msg = $"Restored the whole player file from {id}."; }
                else
                {
                    var po = PlayerFile.Parse(old, g);
                    var pc = PlayerFile.Parse(c.ReadAll(rel), g);
                    if (mode == "items")
                    {
                        pc.Inventory = po.Inventory; pc.Bag = po.Bag; pc.Equipment = po.Equipment;
                        msg = "Restored toolbelt, backpack and equipment from " + id + "; everything else of the player is unchanged.";
                    }
                    else if (mode == "position")
                    {
                        pc.Ecd.PosX = po.Ecd.PosX; pc.Ecd.PosY = po.Ecd.PosY; pc.Ecd.PosZ = po.Ecd.PosZ;
                        msg = "Moved the player back to the position stored in " + id + ".";
                    }
                    else throw new InvalidOperationException("Unknown mode " + mode);
                    c.WriteFile(rel, pc.Build(g), "restore-player-history");
                }
            }
            Log(rel, "restore-player", msg);
            return J(new { ok = true, message = msg });
        });
    }
}
