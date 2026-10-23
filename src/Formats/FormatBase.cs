using System.Text.Json;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>One handler per kind of file the game writes.</summary>
public abstract class FormatHandler
{
    public abstract string Kind { get; }
    public abstract string Title { get; }

    /// <param name="rel">Save-relative path with '/' separators and the backup suffix already removed.</param>
    public abstract bool Match(string rel);

    /// <summary>Validate the file. Must never throw for malformed content – report issues instead.</summary>
    public abstract Analysis Analyze(SaveCtx ctx, string rel, byte[] data);

    /// <summary>Structured, editable representation (serialised to JSON for the UI). Null = no structured editor.</summary>
    public virtual object? LoadModel(SaveCtx ctx, string rel, byte[] data) => null;

    /// <summary>Rebuild the file bytes from an edited model.</summary>
    public virtual byte[] BuildFromModel(SaveCtx ctx, string rel, byte[] original, JsonElement model)
        => throw new NotSupportedException("This file type has no structured editor – use the hex editor.");

    /// <summary>Run a named repair. Return a human readable result message.</summary>
    public virtual string Repair(SaveCtx ctx, string rel, string action, JsonElement args)
        => throw new NotSupportedException("Unknown repair action: " + action);

    protected Analysis NewAnalysis(string rel, byte[] data) => new()
    {
        Rel = rel,
        Kind = Kind,
        Title = Title,
        Size = data.Length,
    };

    /// <summary>Does this file exist and, if so, is it empty / zero-filled (typical for a crash during write)?</summary>
    public static bool LooksWiped(byte[] data, out string why)
    {
        why = "";
        if (data.Length == 0) { why = "file is empty (0 bytes)"; return true; }
        int check = Math.Min(data.Length, 4096);
        bool allZero = true;
        for (int i = 0; i < check; i++) if (data[i] != 0) { allZero = false; break; }
        if (allZero && data.Length > 16) { why = "file starts with a block of zero bytes (typical after a crash/power loss)"; return true; }
        return false;
    }
}

public static class Registry
{
    public static readonly FormatHandler[] All =
    {
        new RegionHandler(),
        new PlayerDataHandler(),
        new WorldStateHandler(),
        new NameIdMapHandler(),
        new SimpleDatHandler(),
        new DecoHandler(),
        new MultiBlockHandler(),
        new MapRegionHandler(),
        new XmlHandler(),
        new JsonHandler(),
        new PngHandler(),
        new GenericHandler(),
    };

    static readonly string[] BackupSuffixes = { ".ext.bak", ".bak", ".loadFailed", ".sdtmp", ".tmp" };

    /// <summary>Split "Foo.ttp.bak" into ("Foo.ttp", ".bak").</summary>
    public static (string baseRel, string? suffix) SplitBackup(string rel)
    {
        foreach (var s in BackupSuffixes)
            if (rel.EndsWith(s, StringComparison.OrdinalIgnoreCase) && rel.Length > s.Length)
                return (rel.Substring(0, rel.Length - s.Length), s);
        return (rel, null);
    }

    public static FormatHandler For(string rel)
    {
        var (b, _) = SplitBackup(rel.Replace('\\', '/'));
        foreach (var h in All) if (h.Match(b)) return h;
        return All[^1];
    }

    public static Analysis Analyze(SaveCtx ctx, string rel)
    {
        rel = rel.Replace('\\', '/');
        byte[] data;
        try { data = ctx.ReadAll(rel); }
        catch (Exception ex)
        {
            return new Analysis { Rel = rel, Kind = "unknown", Title = "Unreadable" }
                .Add(Sev.Error, "io", "Cannot read file: " + ex.Message);
        }
        var (baseRel, suffix) = SplitBackup(rel);
        var h = For(rel);
        Analysis a;
        try { a = h.Analyze(ctx, baseRel, data); }
        catch (Exception ex)
        {
            a = new Analysis { Kind = h.Kind, Title = h.Title, Size = data.Length }
                .Add(Sev.Error, "analyzer-crash", "Analyzer failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        a.Rel = rel;
        a.Size = data.Length;
        if (suffix != null)
        {
            a.Title += " (backup " + suffix + ")";
            a.Info["backupOf"] = baseRel;
            a.Repairs.Clear();
            if (a.Worst < Sev.Error && !suffix.Contains("sdtmp") && !suffix.EndsWith(".tmp") && !suffix.Equals(".loadFailed", StringComparison.OrdinalIgnoreCase))
                a.Repairs.Add(new RepairOption("restoreFromThis", "Restore this backup as the main file", "Replace " + baseRel + " with this file (current one is backed up first)."));
        }
        else
        {
            AddBackupRepairs(ctx, baseRel, a);
        }
        return a;
    }

    static void AddBackupRepairs(SaveCtx ctx, string rel, Analysis a)
    {
        var h = For(rel);
        if (h is GenericHandler || h is XmlHandler || h is JsonHandler || h is PngHandler) { }
        foreach (var suf in new[] { ".bak", ".ext.bak" })
        {
            var brel = rel + suf;
            if (!File.Exists(ctx.Abs(brel))) continue;
            try
            {
                var bdata = ctx.ReadAll(brel);
                var ba = h.Analyze(ctx, rel, bdata);
                var fi = new FileInfo(ctx.Abs(brel));
                string st = ba.Worst >= Sev.Error ? "also damaged" : "looks healthy";
                if (ba.Worst < Sev.Error)
                    a.Repairs.Add(new RepairOption("restore" + suf, "Restore from " + suf + " (" + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + ", " + BinUtil.Size(bdata.Length) + ")",
                        "Backup " + st + ". The current file is backed up first.", a.Worst >= Sev.Error));
                a.Info["backup" + suf] = st;
            }
            catch { }
        }
    }

    public static string RestoreFrom(SaveCtx ctx, string rel, string suffix)
    {
        var src = rel + suffix;
        var data = ctx.ReadAll(src);
        var bak = ctx.WriteFile(rel, data, "restore" + suffix);
        return $"Restored {rel} from {src} ({BinUtil.Size(data.Length)}). Previous version saved to {bak}.";
    }
}
