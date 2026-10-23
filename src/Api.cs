using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SaveDoctor.Core;
using SaveDoctor.Formats;

namespace SaveDoctor;

public static class Api
{
    sealed class ScanJob
    {
        public int Total, Done;
        public bool Finished;
        public ConcurrentDictionary<string, Analysis> Results = new();
        public DateTime Started = DateTime.Now;
        public string Root = "";
    }

    static ScanJob? job;
    static readonly object writeLock = new();
    static readonly List<object> actionLog = new();

    static void Log(string rel, string action, string msg)
    {
        lock (actionLog) actionLog.Add(new { time = DateTime.Now.ToString("HH:mm:ss"), rel, action, message = msg });
    }

    static string Q(HttpContext c, string name, string def = "") => c.Request.Query[name].FirstOrDefault() ?? def;

    static async Task<JsonElement> Body(HttpContext c)
    {
        using var doc = await JsonDocument.ParseAsync(c.Request.Body);
        return doc.RootElement.Clone();
    }

    public static void Map(WebApplication app, Func<SaveCtx?> getCtx, Action<SaveCtx> setCtx, Func<string> getToken)
    {
        SaveCtx Need() => getCtx() ?? throw new InvalidOperationException("No save folder is open yet.");

        IResult J(object o) => Results.Json(o);

        // ───────────── state / opening saves ─────────────
        app.MapGet("/api/state", () =>
        {
            var c = getCtx();
            return J(new
            {
                open = c != null,
                root = c?.Root,
                name = c?.Name,
                serverRunning = SaveCtx.IsServerRunning(out var what),
                serverProcess = what,
                allowWhileRunning = c?.AllowWhileRunning ?? false,
                backupRoot = c?.BackupRoot,
                icons = IconDir() != null,
                saves = FindSaves(),
            });
        });

        app.MapPost("/api/open", async (HttpContext http) =>
        {
            var b = await Body(http);
            var path = b.GetProperty("path").GetString()!.Trim().Trim('"');
            var c = new SaveCtx(path);
            setCtx(c);
            job = null;
            Log("", "open", c.Root);
            return J(new { ok = true, root = c.Root, name = c.Name });
        });

        app.MapPost("/api/settings", async (HttpContext http) =>
        {
            var b = await Body(http);
            var c = Need();
            if (b.TryGetProperty("allowWhileRunning", out var v)) c.AllowWhileRunning = v.GetBoolean();
            if (b.TryGetProperty("iconDir", out var idir) && idir.GetString() is string ip && Directory.Exists(ip)) IconOverride = ip;
            return J(new { ok = true, allowWhileRunning = c.AllowWhileRunning, icons = IconDir() != null });
        });

        // ───────────── files & scanning ─────────────
        app.MapGet("/api/files", () =>
        {
            var c = Need();
            var list = new List<object>();
            var names = PlayerNames(c);
            var labelRx = new System.Text.RegularExpressions.Regex(@"^Player/([^/]+?)(\.ttp(?:\.ext\.bak|\.bak|\.meta|\.loadFailed|\.tmp)?|/(r\.-?\d+\.-?\d+)\.7rm.*)$");
            foreach (var rel in c.ListFilesRel().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(c.Abs(rel));
                var (baseRel, suffix) = Registry.SplitBackup(rel);
                var h = Registry.For(rel);
                string? label = null;
                var lm = labelRx.Match(rel);
                if (lm.Success && names.TryGetValue(lm.Groups[1].Value, out var pn))
                {
                    var tail = lm.Groups[2].Value;
                    label = pn + (tail.EndsWith(".meta") ? " (info)" : tail.EndsWith(".ext.bak") ? " (extra backup)" : tail.EndsWith(".bak") ? " (backup)" : tail.StartsWith("/") ? " – map " + lm.Groups[3].Value : "");
                }
                list.Add(new { rel, label, size = fi.Length, mtime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"), kind = h.Kind, title = h.Title, backupOf = suffix == null ? null : baseRel, suffix });
            }
            return J(new { root = c.Root, files = list });
        });

        app.MapPost("/api/scan/start", () =>
        {
            var c = Need();
            var files = c.ListFilesRel().Where(r => !r.StartsWith("ConfigsDump/", StringComparison.OrdinalIgnoreCase)).ToList();
            var j = new ScanJob { Total = files.Count, Root = c.Root };
            job = j;
            Task.Run(() =>
            {
                // heavy files (regions) last so the quick ones show up immediately
                var order = files.OrderBy(f => Registry.For(f).Kind == "region" ? 1 : 0).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                Parallel.ForEach(order, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, rel =>
                {
                    try { j.Results[rel] = Registry.Analyze(c, rel); }
                    catch (Exception ex) { j.Results[rel] = new Analysis { Rel = rel, Kind = "unknown" }.Add(Sev.Error, "crash", ex.Message); }
                    Interlocked.Increment(ref j.Done);
                });
                j.Finished = true;
            });
            return J(new { started = true, total = j.Total });
        });

        app.MapGet("/api/scan/status", () =>
        {
            var j = job;
            if (j == null) return J(new { running = false, total = 0, done = 0, results = new object[0] });
            var res = j.Results.Values.Select(a => new
            {
                a.Rel, a.Kind, a.Title, a.Summary, a.Size,
                worst = a.Worst,
                issues = a.Issues.Count(i => i.Sev >= Sev.Warn),
                top = a.Issues.Where(i => i.Sev >= Sev.Warn).Select(i => i.Message).FirstOrDefault(),
                repairs = a.Repairs.Count,
                backupOf = a.Info.TryGetValue("backupOf", out var b) ? b : null,
            }).ToList();
            return J(new { running = !j.Finished, total = j.Total, done = j.Done, results = res });
        });

        app.MapGet("/api/analyze", (HttpContext http) =>
        {
            var c = Need();
            return J(Registry.Analyze(c, Q(http, "rel")));
        });

        // ───────────── structured editing ─────────────
        app.MapGet("/api/model", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            var (baseRel, _) = Registry.SplitBackup(rel);
            var h = Registry.For(rel);
            var data = c.ReadAll(rel);
            object? m;
            try { m = h.LoadModel(c, baseRel, data); }
            catch (ParseFail pf) { return Results.Json(new { error = pf.Message, offset = pf.Offset }, statusCode: 422); }
            catch (EndOfStreamException) { return Results.Json(new { error = "File ends unexpectedly (truncated)" }, statusCode: 422); }
            if (m == null) return Results.Json(new { error = "No structured editor for this file." }, statusCode: 404);
            return J(new { kind = h.Kind, model = m });
        });

        app.MapPost("/api/model", async (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            if (Registry.SplitBackup(rel).suffix != null) throw new InvalidOperationException("Open the main file, not a backup copy, to edit.");
            var body = await Body(http);
            var h = Registry.For(rel);
            var orig = File.Exists(c.Abs(rel)) ? c.ReadAll(rel) : Array.Empty<byte>();
            byte[] bytes = h.BuildFromModel(c, rel, orig, body);
            string? bak;
            lock (writeLock) bak = c.WriteFile(rel, bytes, "edit");
            RegionHandler.Invalidate(c, rel);
            Log(rel, "edit", $"saved {BinUtil.Size(bytes.Length)}");
            return J(new { ok = true, size = bytes.Length, backup = bak, analysis = Registry.Analyze(c, rel) });
        });

        app.MapPost("/api/repair", async (HttpContext http) =>
        {
            var c = Need();
            var b = await Body(http);
            var rel = b.GetProperty("rel").GetString()!;
            var action = b.GetProperty("action").GetString()!;
            var args = b.TryGetProperty("args", out var a) ? a : default;
            string msg;
            lock (writeLock) msg = Repairs.Run(c, rel, action, args);
            Log(rel, action, msg);
            return J(new { ok = true, message = msg, analysis = File.Exists(c.Abs(Registry.SplitBackup(rel).baseRel)) ? Registry.Analyze(c, Registry.SplitBackup(rel).baseRel) : null });
        });

        app.MapPost("/api/delete", async (HttpContext http) =>
        {
            var c = Need();
            var b = await Body(http);
            var rel = b.GetProperty("rel").GetString()!;
            lock (writeLock) c.DeleteFile(rel, "delete");
            RegionHandler.Invalidate(c, rel);
            Log(rel, "delete", "moved to backup folder");
            return J(new { ok = true });
        });

        // ───────────── hex editor ─────────────
        app.MapGet("/api/hex", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            long off = long.Parse(Q(http, "offset", "0"));
            int len = Math.Min(int.Parse(Q(http, "length", "4096")), 1 << 20);
            using var fs = new FileStream(c.Abs(rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (off > fs.Length) off = fs.Length;
            fs.Seek(off, SeekOrigin.Begin);
            var buf = new byte[Math.Min(len, fs.Length - off)];
            int got = 0; while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
            return J(new { size = fs.Length, offset = off, data = Convert.ToBase64String(buf, 0, got) });
        });

        app.MapPost("/api/hexwrite", async (HttpContext http) =>
        {
            var c = Need();
            var b = await Body(http);
            var rel = b.GetProperty("rel").GetString()!;
            var data = c.ReadAll(rel);
            foreach (var e in b.GetProperty("edits").EnumerateArray())
            {
                long off = e.GetProperty("offset").GetInt64();
                var bytes = BinUtil.FromHex(e.GetProperty("hex").GetString()!);
                if (off < 0 || off + bytes.Length > data.Length) throw new InvalidOperationException("Edit outside the file");
                Array.Copy(bytes, 0, data, off, bytes.Length);
            }
            if (b.TryGetProperty("truncate", out var t) && t.ValueKind == JsonValueKind.Number) Array.Resize(ref data, t.GetInt32());
            string? bak;
            lock (writeLock) bak = c.WriteFile(rel, data, "hex-edit");
            RegionHandler.Invalidate(c, rel);
            Log(rel, "hex-edit", "bytes patched");
            return J(new { ok = true, backup = bak, analysis = Registry.Analyze(c, rel) });
        });

        app.MapGet("/api/download", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            var data = c.ReadAll(rel);
            return Results.File(data, "application/octet-stream", Path.GetFileName(rel));
        });

        app.MapPost("/api/upload", async (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms);
            string? bak;
            lock (writeLock) bak = c.WriteFile(rel, ms.ToArray(), "upload");
            RegionHandler.Invalidate(c, rel);
            Log(rel, "upload", $"replaced with {BinUtil.Size(ms.Length)}");
            return J(new { ok = true, backup = bak, analysis = Registry.Analyze(c, rel) });
        });

        // ───────────── regions / chunks ─────────────
        app.MapGet("/api/region/slots", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            var data = c.ReadAll(rel);
            var sc = RegionHandler.CachedScan(c, rel, data, false);
            return J(new { rx = sc.RX, rz = sc.RZ, fileVersion = sc.FileVersion, length = sc.FileLength, slots = sc.Slots, issues = sc.Issues });
        });

        app.MapGet("/api/region/chunk", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel"); int slot = int.Parse(Q(http, "slot"));
            RegionIO.TryParseName(Registry.SplitBackup(rel).baseRel, out int rx, out int rz);
            var data = c.ReadAll(rel);
            var sc = RegionIO.Scan(data, rx, rz, true, true, c.Game);
            var s = sc.Slots.FirstOrDefault(x => x.Index == slot);
            if (s == null) return Results.Json(new { error = "Slot is empty" }, statusCode: 404);
            var census = s.Chunk?.Census?.OrderByDescending(kv => kv.Value).Select(kv => new { id = kv.Key, name = c.Game.BlockNames.TryGetValue(kv.Key, out var n) ? n : "block#" + kv.Key, count = kv.Value }).ToList();
            return J(new { slot = s, census, unknownBlocks = census?.Count(x => x.name.StartsWith("block#")) ?? 0 });
        });

        app.MapGet("/api/region/export", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel"); int slot = int.Parse(Q(http, "slot"));
            RegionIO.TryParseName(Registry.SplitBackup(rel).baseRel, out int rx, out int rz);
            var sc = RegionIO.Scan(c.ReadAll(rel), rx, rz, false, false);
            var s = sc.Slots.FirstOrDefault(x => x.Index == slot && x.Blob != null) ?? throw new InvalidOperationException("No data in that slot");
            return Results.File(s.Blob!, "application/octet-stream", $"chunk_{s.ChunkX}_{s.ChunkZ}.ttc");
        });

        app.MapPost("/api/region/import", async (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel"); int slot = int.Parse(Q(http, "slot"));
            using var ms = new MemoryStream(); await http.Request.Body.CopyToAsync(ms);
            string msg; lock (writeLock) msg = RegionOps.ImportChunk(c, rel, slot, ms.ToArray());
            Log(rel, "import-chunk", msg);
            return J(new { ok = true, message = msg });
        });

        app.MapPost("/api/region/op", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            var rel = b.GetProperty("rel").GetString()!;
            var op = b.GetProperty("op").GetString()!;
            string msg;
            lock (writeLock)
            {
                switch (op)
                {
                    case "remove":
                        msg = RegionOps.RemoveChunks(c, rel, b.GetProperty("slots").EnumerateArray().Select(x => x.GetInt32()).ToList());
                        break;
                    case "replaceBlocks":
                        msg = RegionOps.ReplaceBlocks(c, rel, b.GetProperty("from").GetInt32(), b.GetProperty("to").GetInt32(),
                            b.TryGetProperty("slot", out var sl) && sl.ValueKind == JsonValueKind.Number ? sl.GetInt32() : null);
                        break;
                    case "recover":
                        msg = RegionOps.RecoverFrom(c, rel, b.GetProperty("source").GetString()!);
                        break;
                    default: throw new InvalidOperationException("Unknown region op " + op);
                }
            }
            Log(rel, op, msg);
            return J(new { ok = true, message = msg });
        });

        app.MapGet("/api/region/census", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            RegionIO.TryParseName(Registry.SplitBackup(rel).baseRel, out int rx, out int rz);
            var sc = RegionIO.Scan(c.ReadAll(rel), rx, rz, true, true, c.Game);
            var total = new Dictionary<int, long>();
            foreach (var s in sc.Slots) if (s.Chunk?.Census != null) foreach (var kv in s.Chunk.Census) total[kv.Key] = total.GetValueOrDefault(kv.Key) + kv.Value;
            return J(total.OrderByDescending(kv => kv.Value).Select(kv => new { id = kv.Key, name = c.Game.BlockNames.TryGetValue(kv.Key, out var n) ? n : "block#" + kv.Key, count = kv.Value }));
        });

        app.MapGet("/api/region/voxels", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            var want = Q(http, "slots").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Distinct().Take(100).ToHashSet();
            RegionIO.TryParseName(Registry.SplitBackup(rel).baseRel, out int rx, out int rz);
            var sc = RegionIO.Scan(c.ReadAll(rel), rx, rz, false, false);
            var chunks = new List<object>(); var ids = new HashSet<int>();
            foreach (var s in sc.Slots.Where(s => want.Contains(s.Index) && s.Blob != null))
            {
                var ci = ChunkCodec.Inspect(s.Blob!, false);
                if (!ci.Ok || ci.Inflated == null || ci.Version != ChunkCodec.CurrentVersion) continue;
                var arr = new ushort[16 * 16 * 256];
                foreach (var lr in ci.Layers)
                    for (int cell = 0; cell < 1024; cell++)
                    {
                        int id = ChunkCodec.BlockIdAt(ci.Inflated, lr, cell);
                        if (id == 0) continue;
                        int y = lr.Index * 4 + cell / 256, rem = cell % 256;
                        arr[y * 256 + (rem / 16) * 16 + rem % 16] = (ushort)id;
                        ids.Add(id);
                    }
                var bytes = new byte[arr.Length * 2];
                Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length);
                chunks.Add(new { slot = s.Index, cx = s.ChunkX, cz = s.ChunkZ, data = Convert.ToBase64String(bytes) });
            }
            if (chunks.Count == 0) return Results.Json(new { error = "None of the requested chunks could be decoded." }, statusCode: 422);
            var names = ids.ToDictionary(i => i.ToString(), i => c.Game.BlockNames.TryGetValue(i, out var n) ? n : "block#" + i);
            return J(new { chunks, names });
        });

        app.MapGet("/api/deco/summary", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            return J(DecoIO.Summary(DecoIO.Load(c, rel), c.Game));
        });

        app.MapGet("/api/deco/page", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            return J(DecoIO.Page(DecoIO.Load(c, rel), c.Game, int.Parse(Q(http, "offset", "0")), Math.Min(500, int.Parse(Q(http, "limit", "100"))), Q(http, "block"), int.Parse(Q(http, "state", "-1"))));
        });

        app.MapPost("/api/deco/apply", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            var rel = b.GetProperty("rel").GetString()!;
            var a = Json.De<DecoIO.Apply>(b)!;
            var (data, msg) = DecoIO.Run(DecoIO.Load(c, rel), a);
            lock (writeLock) c.WriteFile(rel, data, "edit-deco");
            DecoIO.Invalidate();
            Log(rel, "edit", msg);
            return J(new { ok = true, message = msg, analysis = Registry.Analyze(c, rel) });
        });

        app.MapGet("/api/region/topdown", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(RegionMap.TopDown(c, rel, c.ReadAll(rel)), "image/png");
        });

        app.MapGet("/api/region/markers", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            return J(RegionMap.Collect(c, rel));
        });

        // map region preview
        app.MapGet("/api/map/preview", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            var raw = MapRegionHandler.Decode(c.ReadAll(rel), out var err);
            if (raw == null || raw.Length != MapRegionHandler.Expected) return Results.Json(new { error = err ?? "wrong size" }, statusCode: 422);
            // 32x32 chunks of 16x16 5:5:5 colours -> 512x512 image. chunk (i=row j=col), within chunk row-major
            var rgb = new byte[512 * 512 * 3];
            for (int cz = 0; cz < 32; cz++)
                for (int cx = 0; cx < 32; cx++)
                    for (int p = 0; p < 256; p++)
                    {
                        int idx = (cz * 32 + cx) * 256 + p;
                        ushort col = BitConverter.ToUInt16(raw, idx * 2);
                        int px = cx * 16 + (p & 15), py = cz * 16 + (p >> 4);
                        int o = (py * 512 + px) * 3;
                        rgb[o] = (byte)(((col >> 10) & 31) * 255 / 31); rgb[o + 1] = (byte)(((col >> 5) & 31) * 255 / 31); rgb[o + 2] = (byte)((col & 31) * 255 / 31);
                    }
            return Results.File(PngWriter.Encode(rgb, 512, 512), "image/png");
        });

        app.MapGet("/api/image", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            return Results.File(c.ReadAll(rel), "image/png");
        });

        // ───────────── backups ─────────────
        app.MapPost("/api/snapshot", () =>
        {
            var c = Need();
            string zip; int n;
            c.Snapshot("manual", out zip, out n);
            Log("", "snapshot", $"{n} files → {zip}");
            return J(new { ok = true, path = zip, files = n, size = new FileInfo(zip).Length });
        });

        app.MapGet("/api/backups", () =>
        {
            var c = Need();
            var list = new List<object>();
            if (Directory.Exists(c.BackupRoot))
            {
                foreach (var d in Directory.EnumerateDirectories(c.BackupRoot).OrderByDescending(x => x))
                {
                    var files = Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Select(f => new { rel = Path.GetRelativePath(d, f).Replace('\\', '/'), size = new FileInfo(f).Length }).ToList();
                    list.Add(new { type = "dir", name = Path.GetFileName(d), files });
                }
                foreach (var z in Directory.EnumerateFiles(c.BackupRoot, "*.zip").OrderByDescending(x => x))
                    list.Add(new { type = "zip", name = Path.GetFileName(z), size = new FileInfo(z).Length, files = new object[0] });
            }
            return J(new { root = c.BackupRoot, backups = list });
        });

        app.MapPost("/api/backups/restore", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            var name = b.GetProperty("name").GetString()!; var rel = b.GetProperty("rel").GetString()!;
            var src = Path.GetFullPath(Path.Combine(c.BackupRoot, name, rel.Replace('/', '\\')));
            if (!src.StartsWith(Path.GetFullPath(c.BackupRoot) + "\\")) throw new UnauthorizedAccessException();
            string? bak;
            lock (writeLock) bak = c.WriteFile(rel, File.ReadAllBytes(src), "restore-from-backup");
            RegionHandler.Invalidate(c, rel);
            Log(rel, "restore-backup", "restored from " + name);
            return J(new { ok = true });
        });

        app.MapPost("/api/backups/restore-zip", async (HttpContext http) =>
        {
            var c = Need(); var b = await Body(http);
            var name = b.GetProperty("name").GetString()!; var rel = b.GetProperty("rel").GetString()!;
            var zp = Path.GetFullPath(Path.Combine(c.BackupRoot, name));
            if (!zp.StartsWith(Path.GetFullPath(c.BackupRoot) + "\\")) throw new UnauthorizedAccessException();
            using var zip = ZipFile.OpenRead(zp);
            var entry = zip.GetEntry(rel) ?? throw new FileNotFoundException(rel + " is not in that snapshot");
            using var es = entry.Open(); using var ms = new MemoryStream(); es.CopyTo(ms);
            lock (writeLock) c.WriteFile(rel, ms.ToArray(), "restore-from-snapshot");
            RegionHandler.Invalidate(c, rel);
            Log(rel, "restore-snapshot", "restored from " + name);
            return J(new { ok = true });
        });

        app.MapGet("/api/backups/zipentries", (HttpContext http) =>
        {
            var c = Need(); var name = Q(http, "name");
            var zp = Path.GetFullPath(Path.Combine(c.BackupRoot, name));
            if (!zp.StartsWith(Path.GetFullPath(c.BackupRoot) + "\\")) throw new UnauthorizedAccessException();
            using var zip = ZipFile.OpenRead(zp);
            return J(zip.Entries.Select(e => new { rel = e.FullName, size = e.Length }));
        });

        // ───────────── game data / misc ─────────────
        app.MapGet("/api/items", (HttpContext http) =>
        {
            var c = Need(); var q = Q(http, "q").ToLowerInvariant();
            IEnumerable<KeyValuePair<int, string>> src = c.Game.ItemNames.Concat(c.Game.BlockNames.Where(kv => kv.Key != 0));
            if (q.Length > 0) src = src.Where(kv => kv.Value.ToLowerInvariant().Contains(q));
            return J(src.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).Take(100).Select(kv =>
            {
                c.Game.Items.TryGetValue(kv.Value, out var info);
                return new
                {
                    id = kv.Key, name = kv.Value, isBlock = kv.Key < GameData.ItemsStartHere,
                    isMod = kv.Key >= GameData.ItemsStartHere && c.Game.ModifierItemNames.Contains(kv.Value),
                    quality = info?.ShowQuality ?? false, modSlots = info?.ModSlots, stack = info?.StackSize ?? 1,
                };
            }));
        });

        app.MapGet("/api/hexfind", (HttpContext http) =>
        {
            var c = Need(); var rel = Q(http, "rel");
            long from = long.Parse(Q(http, "from", "0"));
            byte[] pat = Q(http, "mode") == "text" ? Encoding.UTF8.GetBytes(Q(http, "pattern")) : BinUtil.FromHex(Q(http, "pattern"));
            if (pat.Length == 0) return J(new { found = false });
            using var fs = new FileStream(c.Abs(rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[1 << 20];
            long pos = from;
            fs.Seek(pos, SeekOrigin.Begin);
            int keep = 0;
            while (true)
            {
                int n = fs.Read(buf, keep, buf.Length - keep);
                if (n <= 0) break;
                int total = keep + n;
                int idx = buf.AsSpan(0, total).IndexOf(pat);
                if (idx >= 0) return J(new { found = true, offset = pos + idx });
                keep = Math.Min(pat.Length - 1, total);
                Array.Copy(buf, total - keep, buf, 0, keep);
                pos += total - keep;
            }
            return J(new { found = false });
        });

        app.MapGet("/api/icon", (HttpContext http) =>
        {
            var c = Need();
            var dir = IconDir();
            if (dir == null) return Results.NotFound();
            string name = int.TryParse(Q(http, "type"), out int t) ? c.Game.IconName(t) : Q(http, "name");
            if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains("..")) return Results.NotFound();
            var f = Path.Combine(dir, name + ".png");
            if (!File.Exists(f)) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(File.ReadAllBytes(f), "image/png");
        });

        app.MapGet("/api/blocks", () =>
        {
            var c = Need();
            return J(c.Game.BlockNames.OrderBy(kv => kv.Key).Select(kv => new { id = kv.Key, name = kv.Value }));
        });

        app.MapGet("/api/log", () => { lock (actionLog) return J(actionLog.ToArray()); });

        app.MapPost("/api/reveal", async (HttpContext http) =>
        {
            var b = await Body(http);
            var p = b.GetProperty("path").GetString()!;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
            return J(new { ok = true });
        });
    }

    /// <summary>Player file id (file name without extension) → player name, from the .ttp.meta files (falls back to reading the .ttp).</summary>
    static Dictionary<string, string> PlayerNames(SaveCtx c)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = c.Abs("Player");
        if (!Directory.Exists(dir)) return d;
        foreach (var f in Directory.EnumerateFiles(dir, "*.ttp"))
        {
            var id = Path.GetFileNameWithoutExtension(f);
            string? name = null;
            try
            {
                var meta = f + ".meta";
                if (File.Exists(meta))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(XmlHandler.DecodeText(File.ReadAllBytes(meta), out _), "name=\"([^\"]*)\"");
                    if (m.Success) name = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                }
                if (string.IsNullOrWhiteSpace(name)) name = PlayerFile.Parse(File.ReadAllBytes(f), c.Game).Ecd.EntityName;
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(name)) d[id] = name!;
        }
        return d;
    }

    public static string? IconOverride;
    static string? iconDirCache;

    /// <summary>Data\ItemIcons of the game / dedicated server install (auto-detected, or set via the UI).</summary>
    static string? IconDir()
    {
        if (IconOverride != null && Directory.Exists(IconOverride)) return IconOverride;
        if (iconDirCache != null && Directory.Exists(iconDirCache)) return iconDirCache;
        var roots = new List<string>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            foreach (var rel in new[] { "SteamLibrary\\SteamApps\\common", "SteamLibrary\\steamapps\\common", "Steam\\steamapps\\common", "Program Files (x86)\\Steam\\steamapps\\common", "Program Files\\Steam\\steamapps\\common", "SteamApps\\common" })
                roots.Add(Path.Combine(d.RootDirectory.FullName, rel));
        foreach (var r in roots.Where(Directory.Exists))
            foreach (var g in Directory.EnumerateDirectories(r, "7 Days to Die*"))
            {
                var p = Path.Combine(g, "Data", "ItemIcons");
                if (Directory.Exists(p)) return iconDirCache = p;
            }
        return null;
    }

    static List<object> FindSaves()
    {
        var list = new List<object>();
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie", "Saves");
            if (!Directory.Exists(root)) return list;
            foreach (var world in Directory.EnumerateDirectories(root))
                foreach (var game in Directory.EnumerateDirectories(world))
                {
                    var main = Path.Combine(game, "main.ttw");
                    var fi = File.Exists(main) ? new FileInfo(main) : null;
                    list.Add(new { path = game, world = Path.GetFileName(world), game = Path.GetFileName(game), modified = (fi ?? new FileInfo(game) as FileSystemInfo).LastWriteTime.ToString("yyyy-MM-dd HH:mm"), hasWorldFile = fi != null });
                }
        }
        catch { }
        return list.OrderByDescending(x => ((dynamic)x).modified).ToList();
    }
}

/// <summary>Minimal PNG encoder (RGB, 8 bit) for map previews.</summary>
public static class PngWriter
{
    public static byte[] Encode(byte[] rgb, int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr);
        var raw = new byte[(w * 3 + 1) * h];
        for (int y = 0; y < h; y++) { raw[y * (w * 3 + 1)] = 0; Array.Copy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3); }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw, 0, raw.Length);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }
    static void WriteBE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(td, 0); data.CopyTo(td, 4);
        s.Write(td);
        var crc = new byte[4]; WriteBE(crc, 0, (int)PngHandler.Crc32(td, 0, td.Length)); s.Write(crc);
    }
}
