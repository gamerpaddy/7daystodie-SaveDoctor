using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaveDoctor.Core;

public enum Sev { Ok, Info, Warn, Error }

public sealed class Issue
{
    public Sev Sev { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public long? Offset { get; set; }
    public Issue() { }
    public Issue(Sev sev, string code, string message, long? offset = null)
    { Sev = sev; Code = code; Message = message; Offset = offset; }
}

public sealed class RepairOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Destructive { get; set; }
    public RepairOption() { }
    public RepairOption(string id, string label, string description, bool destructive = false)
    { Id = id; Label = label; Description = description; Destructive = destructive; }
}

public sealed class Analysis
{
    public string Rel { get; set; } = "";
    public string Kind { get; set; } = "unknown";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Editable { get; set; }
    public long Size { get; set; }
    public List<Issue> Issues { get; set; } = new();
    public List<RepairOption> Repairs { get; set; } = new();
    public Dictionary<string, object?> Info { get; set; } = new();

    public Sev Worst
    {
        get
        {
            var w = Sev.Ok;
            foreach (var i in Issues) if (i.Sev > w) w = i.Sev;
            return w;
        }
    }

    public Analysis Add(Sev sev, string code, string msg, long? off = null)
    { Issues.Add(new Issue(sev, code, msg, off)); return this; }
}

/// <summary>Raised by parsers when the data is structurally invalid.</summary>
public sealed class ParseFail : Exception
{
    public long Offset { get; }
    public ParseFail(string msg, long offset = -1) : base(msg) { Offset = offset; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = false,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    public static string Ser(object? o) => JsonSerializer.Serialize(o, Opts);
    public static T? De<T>(string s) => JsonSerializer.Deserialize<T>(s, Opts);
    public static T? De<T>(JsonElement e) => e.Deserialize<T>(Opts);
}

/// <summary>One opened save folder: path safety, backups and guarded writes.</summary>
public sealed class SaveCtx
{
    public string Root { get; }
    public string Name { get; }
    public string BackupRoot { get; }
    GameData game = null!;
    string gameStamp = "";
    DateTime gameChecked = DateTime.MinValue;

    /// <summary>Id tables / config dumps. Reloaded automatically when the game rewrites them (it appends new ids while running).</summary>
    public GameData Game
    {
        get
        {
            if ((DateTime.UtcNow - gameChecked).TotalSeconds > 1.5)
            {
                gameChecked = DateTime.UtcNow;
                var st = Stamp();
                if (st != gameStamp || game == null) { game = new GameData(this); gameStamp = st; }
            }
            return game;
        }
    }

    string Stamp()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var f in new[] { "blockmappings.nim", "itemmappings.nim" })
            {
                var fi = new FileInfo(Path.Combine(Root, f));
                sb.Append(fi.Exists ? fi.Length + ":" + fi.LastWriteTimeUtc.Ticks : "-").Append('|');
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    public SaveCtx(string root)
    {
        Root = Path.GetFullPath(root).TrimEnd('\\', '/');
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        var parent = Directory.GetParent(Root)?.Name ?? "";
        Name = (parent.Length > 0 ? parent + "__" : "") + Path.GetFileName(Root);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(Root.ToLowerInvariant())))[..6].ToLowerInvariant();
        BackupRoot = Path.Combine(appData, "7DaysToDie", "SaveDoctorBackups", Sanitize(Name) + "_" + hash);
        gameStamp = Stamp(); game = new GameData(this); gameChecked = DateTime.UtcNow;
    }

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    public void ReloadGameData() { game = new GameData(this); gameStamp = Stamp(); gameChecked = DateTime.UtcNow; }

    /// <summary>Resolve a relative path (forward or back slashes) to an absolute one inside the save.</summary>
    public string Abs(string rel)
    {
        rel = (rel ?? "").Replace('/', '\\').TrimStart('\\');
        var full = Path.GetFullPath(Path.Combine(Root, rel));
        if (!full.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase) && !string.Equals(full, Root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path escapes the save folder: " + rel);
        return full;
    }

    public string Rel(string abs)
    {
        var r = Path.GetRelativePath(Root, abs).Replace('\\', '/');
        return r;
    }

    public IEnumerable<string> ListFilesRel()
    {
        foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            yield return Rel(f);
    }

    public byte[] ReadAll(string rel)
    {
        using var fs = new FileStream(Abs(rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[fs.Length];
        int off = 0;
        while (off < buf.Length)
        {
            int n = fs.Read(buf, off, buf.Length - off);
            if (n <= 0) break;
            off += n;
        }
        if (off != buf.Length) Array.Resize(ref buf, off);
        return buf;
    }

    public static bool IsServerRunning(out string? what)
    {
        what = null;
        try
        {
            foreach (var n in new[] { "7DaysToDieServer", "7DaysToDie" })
            {
                var ps = Process.GetProcessesByName(n);
                if (ps.Length > 0) { what = n + ".exe (pid " + ps[0].Id + ")"; foreach (var p in ps) p.Dispose(); return true; }
            }
        }
        catch { }
        return false;
    }

    public string NewBackupDir(string reason)
    {
        var dir = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + Sanitize(reason));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Copy the current version of a file into the backup area. Returns the backup path (or null if the file didn't exist).</summary>
    public string? Backup(string rel, string reason, string? dir = null)
    {
        var src = Abs(rel);
        if (!File.Exists(src)) return null;
        dir ??= NewBackupDir(reason);
        var dst = Path.Combine(dir, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, true);
        return dst;
    }

    public class WriteGuardException : Exception { public WriteGuardException(string m) : base(m) { } }

    public bool AllowWhileRunning { get; set; }

    void GuardWrite()
    {
        if (!AllowWhileRunning && IsServerRunning(out var what))
            throw new WriteGuardException("The game/server is running (" + what + "). Stop it before modifying save files – writing while it runs corrupts saves. (You can override this in the UI header.)");
    }

    /// <summary>Back up the old file, then atomically replace it with new content.</summary>
    public string? WriteFile(string rel, byte[] data, string reason)
    {
        GuardWrite();
        var dst = Abs(rel);
        var bak = Backup(rel, reason);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var tmp = dst + ".sdtmp";
        File.WriteAllBytes(tmp, data);
        if (File.Exists(dst)) File.Replace(tmp, dst, null);
        else File.Move(tmp, dst);
        return bak;
    }

    public string? DeleteFile(string rel, string reason)
    {
        GuardWrite();
        var bak = Backup(rel, reason);
        File.Delete(Abs(rel));
        return bak;
    }

    public void Snapshot(string reason, out string zipPath, out int count)
    {
        Directory.CreateDirectory(BackupRoot);
        zipPath = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + Sanitize(reason) + ".zip");
        count = 0;
        using var zfs = new FileStream(zipPath, FileMode.Create);
        using var zip = new System.IO.Compression.ZipArchive(zfs, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var rel in ListFilesRel())
        {
            if (rel.StartsWith("ConfigsDump/", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.EndsWith(".sdtmp")) continue;
            try
            {
                var entry = zip.CreateEntry(rel, System.IO.Compression.CompressionLevel.Fastest);
                using var es = entry.Open();
                using var fs = new FileStream(Abs(rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.CopyTo(es);
                count++;
            }
            catch { }
        }
    }
}
