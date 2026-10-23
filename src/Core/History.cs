using System.IO.Compression;
using System.Text.Json;
using SaveDoctor.Formats;

namespace SaveDoctor.Core;

public abstract class BackupSource : IDisposable
{
    public string Name = "";
    public virtual bool IsPartial => false;      // only holds the files that were changed at that time
    public abstract IEnumerable<(string rel, long size)> Files();
    public abstract byte[]? Read(string rel);
    public virtual void Dispose() { }
}

sealed class LiveSource : BackupSource
{
    readonly SaveCtx ctx;
    public LiveSource(SaveCtx c) { ctx = c; Name = "live"; }
    public override IEnumerable<(string rel, long size)> Files()
        => ctx.ListFilesRel().Where(r => !r.StartsWith("ConfigsDump/", StringComparison.OrdinalIgnoreCase) && !r.EndsWith(".sdtmp")).Select(r => (r, new FileInfo(ctx.Abs(r)).Length));
    public override byte[]? Read(string rel) { try { return ctx.ReadAll(rel); } catch { return null; } }
}

sealed class ZipSource : BackupSource
{
    readonly ZipArchive zip;
    public ZipSource(string path) { zip = ZipFile.OpenRead(path); Name = Path.GetFileName(path); }
    public override IEnumerable<(string rel, long size)> Files() => zip.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith("/")).Select(e => (e.FullName.Replace('\\', '/'), e.Length));
    public override byte[]? Read(string rel)
    {
        var e = zip.GetEntry(rel); if (e == null) return null;
        using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }
    public override void Dispose() => zip.Dispose();
}

sealed class DirSource : BackupSource
{
    readonly string dir;
    public DirSource(string d) { dir = d; Name = Path.GetFileName(d); }
    public override bool IsPartial => true;
    public override IEnumerable<(string rel, long size)> Files() => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => (Path.GetRelativePath(dir, f).Replace('\\', '/'), new FileInfo(f).Length));
    public override byte[]? Read(string rel) { var p = Path.GetFullPath(Path.Combine(dir, rel.Replace('/', '\\'))); return p.StartsWith(dir + "\\") && File.Exists(p) ? File.ReadAllBytes(p) : null; }
}

public sealed class AutoSnapshotSettings
{
    public bool Enabled { get; set; }
    public int MinHours { get; set; } = 12;
    public int Keep { get; set; } = 5;
}

public static class History
{
    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie", "SaveDoctor.settings.json");

    public static AutoSnapshotSettings LoadAuto()
    {
        try { return JsonSerializer.Deserialize<AutoSnapshotSettings>(File.ReadAllText(SettingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
        catch { return new(); }
    }

    public static void SaveAuto(AutoSnapshotSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
    }

    public sealed class Item
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";       // zip | dir
        public DateTime Time { get; set; }
        public string Reason { get; set; } = "";
        public int Files { get; set; }
        public long Size { get; set; }
        public List<string> Sample { get; set; } = new();
    }

    static DateTime ParseTime(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\d{8})-(\d{6})(?:-(\d{3}))?_(.*?)(\.zip)?$");
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out var t)) return t;
        return DateTime.MinValue;
    }

    public static List<Item> List(SaveCtx c)
    {
        var list = new List<Item>();
        if (!Directory.Exists(c.BackupRoot)) return list;
        foreach (var d in Directory.EnumerateDirectories(c.BackupRoot))
        {
            var name = Path.GetFileName(d);
            var files = Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).ToList();
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^\d{8}-\d{6}(?:-\d{3})?_(.*)$");
            list.Add(new Item { Id = name, Kind = "dir", Time = ParseTime(name), Reason = m.Success ? m.Groups[1].Value : name, Files = files.Count, Size = files.Sum(f => new FileInfo(f).Length), Sample = files.Take(4).Select(f => Path.GetRelativePath(d, f).Replace('\\', '/')).ToList() });
        }
        foreach (var z in Directory.EnumerateFiles(c.BackupRoot, "*.zip"))
        {
            var name = Path.GetFileName(z);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^\d{8}-\d{6}(?:-\d{3})?_(.*)\.zip$");
            int n = 0; try { using var zip = ZipFile.OpenRead(z); n = zip.Entries.Count; } catch { }
            list.Add(new Item { Id = name, Kind = "zip", Time = ParseTime(name), Reason = m.Success ? m.Groups[1].Value : name, Files = n, Size = new FileInfo(z).Length });
        }
        return list.OrderByDescending(i => i.Time).ThenByDescending(i => i.Id).ToList();
    }

    public static BackupSource Open(SaveCtx c, string id)
    {
        if (id == "live") return new LiveSource(c);
        var p = Path.GetFullPath(Path.Combine(c.BackupRoot, id));
        if (!p.StartsWith(Path.GetFullPath(c.BackupRoot) + "\\", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
        if (Directory.Exists(p)) return new DirSource(p);
        if (File.Exists(p) && p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return new ZipSource(p);
        throw new FileNotFoundException("Backup not found: " + id);
    }

    public sealed class FileDiff { public string Rel { get; set; } = ""; public string Status { get; set; } = ""; public long SizeA { get; set; } public long SizeB { get; set; } public string Kind { get; set; } = ""; public string? Label { get; set; } }

    /// <summary>File-level comparison of two sources (a = older, b = newer / live). Content is compared byte for byte.</summary>
    public static List<FileDiff> Diff(BackupSource a, BackupSource b, Func<string, string?>? label = null)
    {
        var fa = a.Files().GroupBy(x => x.rel).ToDictionary(g => g.Key, g => g.First().size, StringComparer.OrdinalIgnoreCase);
        var fb = b.Files().GroupBy(x => x.rel).ToDictionary(g => g.Key, g => g.First().size, StringComparer.OrdinalIgnoreCase);
        if (a.IsPartial) fb = fb.Where(kv => fa.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var res = new List<FileDiff>();
        foreach (var rel in fa.Keys.Union(fb.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (rel.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".loadFailed", StringComparison.OrdinalIgnoreCase)) continue;
            bool ina = fa.TryGetValue(rel, out long sa), inb = fb.TryGetValue(rel, out long sb);
            string status;
            if (ina && !inb) status = "removed";
            else if (!ina && inb) status = "added";
            else if (sa != sb) status = "changed";
            else
            {
                var da = a.Read(rel); var db = b.Read(rel);
                status = da != null && db != null && BinUtil.SameBytes(da, db) ? "same" : "changed";
            }
            if (status == "same") continue;
            res.Add(new FileDiff { Rel = rel, Status = status, SizeA = ina ? sa : 0, SizeB = inb ? sb : 0, Kind = Registry.For(rel).Kind, Label = label?.Invoke(rel) });
        }
        return res;
    }

    // ───────── automatic snapshots
    static DateTime lastAutoCheck = DateTime.MinValue;

    public static string? MaybeAutoSnapshot(SaveCtx c)
    {
        var s = LoadAuto();
        if (!s.Enabled) return null;
        try
        {
            if (SaveCtx.IsServerRunning(out _)) return null;      // a running game may be writing – do not copy half-written files
            var autos = List(c).Where(i => i.Kind == "zip" && i.Reason.StartsWith("auto")).ToList();
            if (autos.Count > 0 && (DateTime.Now - autos[0].Time).TotalHours < s.MinHours) return null;
            c.Snapshot("auto", out var zip, out int n);
            var all = List(c).Where(i => i.Kind == "zip" && i.Reason.StartsWith("auto")).Skip(Math.Max(1, s.Keep)).ToList();
            foreach (var old in all) try { File.Delete(Path.Combine(c.BackupRoot, old.Id)); } catch { }
            return $"Automatic snapshot of {n} files created.";
        }
        catch { return null; }
    }
}
