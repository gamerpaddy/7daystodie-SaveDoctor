using System.Text.Json;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class SpawnPointM
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Heading { get; set; }
    public int Team { get; set; }
    public int ActiveInGameMode { get; set; }
}

/// <summary>main.ttw – the world header (WorldState in Assembly-CSharp.dll, save version 23).</summary>
public sealed class WorldFile
{
    public uint Version { get; set; } = 23;
    public string GameVersionString { get; set; } = "";
    public int ReleaseType { get; set; }
    public int Major { get; set; }
    public int Minor { get; set; }
    public int Build { get; set; }
    public uint Pad1 { get; set; }
    public int ActiveGameMode { get; set; }
    public uint Pad2 { get; set; }
    public float WaterLevel { get; set; }
    public int ChunkSizeX { get; set; }
    public int ChunkSizeY { get; set; }
    public int ChunkSizeZ { get; set; }
    public int ChunkCount { get; set; }
    public int ProviderId { get; set; }
    public int Seed { get; set; }
    public ulong WorldTime { get; set; }
    public ulong TimeInTicks { get; set; }
    public int SpawnVersion { get; set; } = 2;
    public List<SpawnPointM> SpawnPoints { get; set; } = new();
    public int NextEntityId { get; set; }
    public long SaveDataLimit { get; set; } = -1;
    public byte[] DynamicSpawner { get; set; } = Array.Empty<byte>();
    public byte[] AiDirector { get; set; } = Array.Empty<byte>();
    public int SleeperVolumesVersion { get; set; } = 1;
    public byte[] SleeperVolumes { get; set; } = Array.Empty<byte>();
    public int TriggerVolumesVersion { get; set; } = 1;
    public byte[] TriggerVolumes { get; set; } = Array.Empty<byte>();
    public int WallVolumesVersion { get; set; } = 1;
    public byte[] WallVolumes { get; set; } = Array.Empty<byte>();
    public byte[] Weather { get; set; } = Array.Empty<byte>();
    public string Guid { get; set; } = "";
    public long TrailingBytes { get; set; }

    public static WorldFile Parse(byte[] data)
    {
        using var r = BinUtil.Reader(data);
        r.Need(9, "header");
        if (r.ReadByte() != 't' || r.ReadByte() != 't' || r.ReadByte() != 'w' || r.ReadByte() != 0)
            throw new ParseFail("Invalid magic bytes – not a world header ('ttw\\0' expected).", 0);
        var w = new WorldFile { Version = r.ReadUInt32() };
        if (w.Version != 23) throw new ParseFail($"World header version {w.Version}; the editor understands version 23 (the game loads older ones itself).", 4);
        w.GameVersionString = r.Str();
        w.ReleaseType = r.ReadInt32(); w.Major = r.ReadInt32(); w.Minor = r.ReadInt32(); w.Build = r.ReadInt32();
        w.Pad1 = r.ReadUInt32();
        w.ActiveGameMode = r.ReadInt32();
        w.Pad2 = r.ReadUInt32();
        w.WaterLevel = r.ReadSingle();
        w.ChunkSizeX = r.ReadInt32(); w.ChunkSizeY = r.ReadInt32(); w.ChunkSizeZ = r.ReadInt32();
        w.ChunkCount = r.ReadInt32();
        w.ProviderId = r.ReadInt32();
        w.Seed = r.ReadInt32();
        w.WorldTime = r.ReadUInt64();
        w.TimeInTicks = r.ReadUInt64();
        w.SpawnVersion = r.ReadByte();
        int n = r.ReadInt32();
        if (n < 0 || n > 100000) throw new ParseFail("Implausible spawn point count " + n, r.Pos() - 4);
        for (int i = 0; i < n; i++)
        {
            if (w.SpawnVersion == 2) r.ReadUInt16();
            w.SpawnPoints.Add(new SpawnPointM { X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(), Heading = r.ReadSingle(), Team = r.ReadInt32(), ActiveInGameMode = r.ReadInt32() });
        }
        w.NextEntityId = r.ReadInt32();
        w.SaveDataLimit = r.ReadInt64();
        w.DynamicSpawner = Chunk(r, "dynamic spawner state");
        w.AiDirector = Chunk(r, "AI director state");
        w.SleeperVolumesVersion = r.ReadInt32(); w.SleeperVolumes = Chunk(r, "sleeper volumes");
        w.TriggerVolumesVersion = r.ReadInt32(); w.TriggerVolumes = Chunk(r, "trigger volumes");
        w.WallVolumesVersion = r.ReadInt32(); w.WallVolumes = Chunk(r, "wall volumes");
        long wpos = r.Pos();
        int wlen = r.ReadInt32();
        if (wlen < 4 || wlen > r.Left() + 4) throw new ParseFail($"Weather section length {wlen} is invalid", wpos);
        w.Weather = r.ReadBytes(wlen - 4);
        w.Guid = r.Str();
        w.TrailingBytes = r.Left();
        return w;
    }

    static byte[] Chunk(BinaryReader r, string what)
    {
        long at = r.Pos();
        int n = r.ReadInt32();
        if (n < 0 || n > r.Left()) throw new ParseFail($"{what}: length {n} is invalid (only {r.Left()} bytes left)", at);
        return n == 0 ? Array.Empty<byte>() : r.ReadBytes(n);
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, BinUtil.Utf8, true);
        w.Write((byte)'t'); w.Write((byte)'t'); w.Write((byte)'w'); w.Write((byte)0);
        w.Write(23u);
        w.Str(GameVersionString);
        w.Write(ReleaseType); w.Write(Major); w.Write(Minor); w.Write(Build);
        w.Write(Pad1);
        w.Write(ActiveGameMode);
        w.Write(Pad2);
        w.Write(WaterLevel);
        w.Write(ChunkSizeX); w.Write(ChunkSizeY); w.Write(ChunkSizeZ);
        w.Write(ChunkCount); w.Write(ProviderId); w.Write(Seed);
        w.Write(WorldTime); w.Write(TimeInTicks);
        w.Write((byte)SpawnVersion);
        w.Write(SpawnPoints.Count);
        foreach (var s in SpawnPoints) { if (SpawnVersion == 2) w.Write((ushort)0); w.Write(s.X); w.Write(s.Y); w.Write(s.Z); w.Write(s.Heading); w.Write(s.Team); w.Write(s.ActiveInGameMode); }
        w.Write(NextEntityId);
        w.Write(SaveDataLimit);
        w.WriteBlob(DynamicSpawner);
        w.WriteBlob(AiDirector);
        w.Write(SleeperVolumesVersion); w.WriteBlob(SleeperVolumes);
        w.Write(TriggerVolumesVersion); w.WriteBlob(TriggerVolumes);
        w.Write(WallVolumesVersion); w.WriteBlob(WallVolumes);
        w.Write(Weather.Length + 4); w.Write(Weather);
        w.Str(Guid);
        w.Flush();
        return ms.ToArray();
    }
}

public sealed class WorldStateHandler : FormatHandler
{
    public override string Kind => "world";
    public override string Title => "World header (time, seed, game mode…)";
    public override bool Match(string rel) => rel.EndsWith("main.ttw", StringComparison.OrdinalIgnoreCase) && !rel.Contains('/') ;

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data); a.Editable = true;
        if (LooksWiped(data, out var why)) return a.Add(Sev.Error, "wiped", "This file is unusable: " + why + ". The game would fall back to main.ttw.bak and main.ttw.ext.bak.");
        WorldFile w;
        try { w = WorldFile.Parse(data); }
        catch (ParseFail pf) { a.Editable = false; return a.Add(Sev.Error, "parse", pf.Message + (pf.Offset >= 0 ? $" (offset 0x{pf.Offset:X})" : ""), pf.Offset >= 0 ? pf.Offset : null); }
        catch (EndOfStreamException) { a.Editable = false; return a.Add(Sev.Error, "truncated", "The file ends too early – it was cut off while being written."); }
        catch (Exception ex) { a.Editable = false; return a.Add(Sev.Error, "parse", ex.Message); }

        long totalMin = (long)w.WorldTime / 1000;
        long day = (long)w.WorldTime / 24000 + 1;
        long hour = (long)w.WorldTime % 24000 / 1000;
        a.Summary = $"{w.GameVersionString} · day {day} {hour:00}:00 · seed {w.Seed} · next entity id {w.NextEntityId}";
        a.Info["day"] = day; a.Info["gameVersion"] = w.GameVersionString; a.Info["seed"] = w.Seed;

        if (w.TrailingBytes > 0) a.Add(Sev.Warn, "trailing", $"{w.TrailingBytes} unexpected bytes after the GUID.", data.Length - w.TrailingBytes);
        if (!float.IsFinite(w.WaterLevel)) a.Add(Sev.Error, "water", "Water level is NaN/Infinity.");
        if (w.ProviderId < 0 || w.ProviderId > 7) a.Add(Sev.Error, "provider", $"Chunk provider id {w.ProviderId} is invalid.");
        if (w.WorldTime > 24000UL * 100000) a.Add(Sev.Warn, "time", $"World time {w.WorldTime} is absurdly large (day {day}).");
        if (w.TimeInTicks > 0 && w.WorldTime > 0 && w.TimeInTicks > w.WorldTime * 100 && w.TimeInTicks > 1_000_000_000UL) a.Add(Sev.Info, "ticks", "Tick counter is unusually high relative to world time.");
        if (string.IsNullOrWhiteSpace(w.Guid)) a.Add(Sev.Warn, "guid", "World GUID is empty (the game generates a new one).");
        if (w.NextEntityId < 171) a.Add(Sev.Warn, "next-id", $"Next entity id {w.NextEntityId} is below the game's minimum 171.");

        // cross-check with players: next entity id must be above every player's entity id
        try
        {
            int maxId = 0; string who = "";
            foreach (var f in Directory.EnumerateFiles(ctx.Abs("Player"), "*.ttp"))
            {
                try
                {
                    var p = PlayerFile.Parse(File.ReadAllBytes(f), ctx.Game);
                    if (p.Id > maxId) { maxId = p.Id; who = p.Ecd.EntityName; }
                }
                catch { }
            }
            if (maxId >= w.NextEntityId)
            {
                a.Add(Sev.Error, "entity-id-collision", $"Next entity id ({w.NextEntityId}) is not larger than player '{who}' entity id ({maxId}). New entities can receive the same id as that player.");
                a.Repairs.Add(new RepairOption("fixNextEntityId", "Raise next entity id", $"Sets it to {maxId + 1000}.", false));
            }
        }
        catch { }
        return a;
    }

    public override object? LoadModel(SaveCtx ctx, string rel, byte[] data) => new { World = WorldFile.Parse(data) };

    public override byte[] BuildFromModel(SaveCtx ctx, string rel, byte[] original, JsonElement model)
    {
        var pe = model.TryGetProperty("world", out var x) ? x : model;
        var w = Json.De<WorldFile>(pe) ?? throw new InvalidOperationException("Empty model");
        var bytes = w.ToBytes();
        WorldFile.Parse(bytes);
        return bytes;
    }

    public override string Repair(SaveCtx ctx, string rel, string action, JsonElement args)
    {
        if (action == "fixNextEntityId")
        {
            var w = WorldFile.Parse(ctx.ReadAll(rel));
            int maxId = 0;
            foreach (var f in Directory.EnumerateFiles(ctx.Abs("Player"), "*.ttp"))
                try { maxId = Math.Max(maxId, PlayerFile.Parse(File.ReadAllBytes(f), ctx.Game).Id); } catch { }
            w.NextEntityId = Math.Max(w.NextEntityId, maxId + 1000);
            ctx.WriteFile(rel, w.ToBytes(), "fix-next-entity-id");
            return $"Next entity id set to {w.NextEntityId}.";
        }
        return base.Repair(ctx, rel, action, args);
    }
}
