using System.Text;
using System.Text.Json;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>One stored game option of an .sdf file (SDF/SdfFile.cs of the game: tag type, name length, name, payload).</summary>
public sealed class SdfEntry
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "int";           // int | float | string | bool | binary | bytes
    public object? Value { get; set; }                   // number | number | text | bool | base64 text (binary) | base64 (bytes)
}

public static class SdfFile
{
    const byte TInt = 1, TString = 2, TBool = 3, TBytes = 4, TFloat = 6, TBinary = 7;

    public static List<SdfEntry> Read(byte[] data, out string? problem)
    {
        problem = null;
        var list = new List<SdfEntry>();
        using var r = BinUtil.Reader(data);
        try
        {
            while (r.Left() > 0)
            {
                long start = r.Pos();
                byte t = r.ReadByte(); r.ReadInt16();
                string name = r.Str();
                var e = new SdfEntry { Name = name };
                switch (t)
                {
                    case TInt: e.Type = "int"; e.Value = r.ReadInt32(); break;
                    case TFloat: e.Type = "float"; e.Value = (double)r.ReadSingle(); break;
                    case TBool: e.Type = "bool"; e.Value = r.ReadBoolean(); break;
                    case TString:
                        r.ReadInt16(); e.Type = "string";
                        try { e.Value = Encoding.UTF8.GetString(Convert.FromBase64String(r.Str())); }
                        catch (FormatException) { throw new ParseFail("Option " + name + " holds text that is not valid base64", start); }
                        break;
                    case TBinary: r.ReadInt16(); e.Type = "binary"; e.Value = r.Str(); break;
                    case TBytes: { int n = r.ReadInt16(); if (n < 0 || n > r.Left()) throw new ParseFail("Bad byte array length", start); e.Type = "bytes"; e.Value = Convert.ToBase64String(r.ReadBytes(n)); break; }
                    default: throw new ParseFail($"Unknown tag type {t} at offset {start}", start);
                }
                list.Add(e);
            }
        }
        catch (Exception ex) when (ex is ParseFail || ex is EndOfStreamException) { problem = ex.Message; }
        return list;
    }

    public static byte[] Write(IEnumerable<SdfEntry> entries)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, BinUtil.Utf8);
        foreach (var e in entries)
        {
            byte t = e.Type switch { "int" => TInt, "float" => TFloat, "bool" => TBool, "string" => TString, "binary" => TBinary, "bytes" => TBytes, _ => throw new InvalidOperationException("Unknown option type " + e.Type) };
            if (string.IsNullOrEmpty(e.Name)) throw new InvalidOperationException("An option has no name.");
            w.Write(t); w.Write((short)e.Name.Length); w.Str(e.Name);
            switch (t)
            {
                case TInt: w.Write(Convert.ToInt32(Num(e.Value))); break;
                case TFloat: w.Write((float)Num(e.Value)); break;
                case TBool: w.Write(e.Value is bool b ? b : Convert.ToBoolean(e.Value)); break;
                case TString: { var s = Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Value?.ToString() ?? "")); w.Write((short)s.Length); w.Str(s); break; }
                case TBinary: { var s = e.Value?.ToString() ?? ""; w.Write((short)s.Length); w.Str(s); break; }
                case TBytes: { var bytes = Convert.FromBase64String(e.Value?.ToString() ?? ""); w.Write((short)bytes.Length); w.Write(bytes); break; }
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    static double Num(object? v) => v switch { null => 0, double d => d, int i => i, long l => l, JsonElement je => je.GetDouble(), _ => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) };
}

/// <summary>gameOptions.sdf – the settings chosen when the savegame was created (land claims, PvP, sandbox options …).</summary>
public sealed class GameOptionsHandler : FormatHandler
{
    public override string Kind => "gameoptions";
    public override string Title => "Game options (sandbox settings)";
    public override bool Match(string rel) => rel.EndsWith(".sdf", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(rel).Contains("gameOptions", StringComparison.OrdinalIgnoreCase);

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data); a.Editable = true;
        if (LooksWiped(data, out var why)) return a.Add(Sev.Error, "wiped", "This file is unusable: " + why + ".");
        var list = SdfFile.Read(data, out var problem);
        if (problem != null) a.Add(Sev.Error, "parse", problem);
        a.Summary = list.Count + " options";
        a.Info["options"] = list.Count;
        foreach (var dup in list.GroupBy(e => e.Name).Where(g => g.Count() > 1)) a.Add(Sev.Warn, "dup", $"Option {dup.Key} is stored {dup.Count()} times – the game stops reading at the second one.");
        var code = list.FirstOrDefault(e => e.Name == "SandboxCode")?.Value as string;
        if (code != null && code.Length > 0)
        {
            bool ok = code[0] == 'A' && (code.Length - 1) % 3 == 0 && code.Skip(1).All(c => c >= 'A' && c <= 'Z');
            if (!ok) a.Add(Sev.Warn, "sandbox-code", "The sandbox code \"" + code + "\" is not in the format the game writes (letter A, then groups of three letters) – the game will fall back to the default sandbox options.");
        }
        return a;
    }

    public override object? LoadModel(SaveCtx ctx, string rel, byte[] data)
    {
        var list = SdfFile.Read(data, out var problem);
        if (problem != null && list.Count == 0) throw new InvalidOperationException(problem);
        return new { entries = list };
    }

    public override byte[] BuildFromModel(SaveCtx ctx, string rel, byte[] original, JsonElement model)
    {
        var list = new List<SdfEntry>();
        foreach (var e in model.GetProperty("entries").EnumerateArray())
        {
            var v = e.GetProperty("value");
            list.Add(new SdfEntry
            {
                Name = e.GetProperty("name").GetString() ?? "",
                Type = e.GetProperty("type").GetString() ?? "int",
                Value = v.ValueKind switch { JsonValueKind.Number => v.GetDouble(), JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.String => v.GetString(), _ => null },
            });
        }
        var bytes = SdfFile.Write(list);
        var back = SdfFile.Read(bytes, out var problem);
        if (problem != null || back.Count != list.Count) throw new InvalidOperationException("Internal check failed – the written options could not be read back.");
        return bytes;
    }
}
