using System.Text.Json;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>What the inspector shows of one feature of a composite tile entity.</summary>
public sealed class FeatureView
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";        // storage | lockable | door | landclaim | signable | canvas | marker | raw
    public int Hash { get; set; }
    public bool Editable { get; set; }
    public object? Data { get; set; }
    public int PayloadBytes { get; set; }
}

public sealed class LockableM { public int Version { get; set; } = 18; public bool Locked { get; set; } public List<PlatformId?> Allowed { get; set; } = new(); public string PasswordHash { get; set; } = ""; }
public sealed class DoorM { public int Version { get; set; } = 18; public bool IsOpen { get; set; } }
public sealed class LandClaimM { public int Version { get; set; } = 18; public bool ShowBounds { get; set; } }
public sealed class SignableM { public int Version { get; set; } = 18; public bool HasText { get; set; } public string Text { get; set; } = ""; public PlatformId? Author { get; set; } }
public sealed class CanvasM
{
    public int Version { get; set; } = 21;
    public string Library { get; set; } = "[D]";
    public string Guid { get; set; } = System.Guid.Empty.ToString();
    public int Blend { get; set; }                 // 0 cutout, 1 alpha blend
    public int Rotation { get; set; }              // 0 none, 1 90°, 2 180°, 3 270°
    public bool ShowOnImposter { get; set; }
}
public sealed class MarkerM { public int Version { get; set; } }

/// <summary>Codec for the features of composite tile entities (TEFeature*.Read / Write in persistency mode).</summary>
public static class TeFeatures
{
    static readonly Dictionary<int, (string name, string kind)> Known = new()
    {
        [GameData.MonoHash("TEFeatureStorage")] = ("Storage", "storage"),
        [GameData.MonoHash("TEFeatureLockable")] = ("Lock / password", "lockable"),
        [GameData.MonoHash("TEFeatureLockPickable")] = ("Lock pickable", "marker"),
        [GameData.MonoHash("TEFeatureDoor")] = ("Door", "door"),
        [GameData.MonoHash("TEFeatureLandClaim")] = ("Land claim", "landclaim"),
        [GameData.MonoHash("TEFeatureSignable")] = ("Sign text", "signable"),
        [GameData.MonoHash("TEFeatureCanvas")] = ("Drawn sign (canvas)", "canvas"),
        [GameData.MonoHash("TEFeaturePickup")] = ("Pick-up", "marker"),
        [GameData.MonoHash("TEFeatureExplodable")] = ("Explodable", "marker"),
        [GameData.MonoHash("TEFeatureAreaRepair")] = ("Area repair", "marker"),
        [GameData.MonoHash("TEFeatureCombine")] = ("Combine", "raw"),
    };

    public static string NameOf(int hash) => Known.TryGetValue(hash, out var k) ? k.name : "feature " + hash.ToString("X8");
    public static string KindOf(int hash) => Known.TryGetValue(hash, out var k) ? k.kind : "raw";

    static JsonSerializerOptions Opt => new(JsonSerializerDefaults.Web);

    /// <summary>Decode a feature payload. The model is only offered for editing when re-encoding reproduces the stored bytes.</summary>
    public static FeatureView Decode(TeFeat f, GameData g)
    {
        var v = new FeatureView { Name = NameOf(f.Hash), Kind = KindOf(f.Hash), Hash = f.Hash, PayloadBytes = f.Payload.Length };
        try
        {
            object? model = v.Kind switch
            {
                "storage" => StorageData.TryRead(f.Payload, g),
                "lockable" => ReadLockable(f.Payload),
                "door" => Simple(f.Payload, r => new DoorM { IsOpen = r.ReadBoolean() }, (m, ver) => m.Version = ver),
                "landclaim" => Simple(f.Payload, r => new LandClaimM { ShowBounds = r.ReadBoolean() }, (m, ver) => m.Version = ver),
                "signable" => ReadSignable(f.Payload),
                "canvas" => ReadCanvas(f.Payload),
                "marker" => f.Payload.Length == 2 ? new MarkerM { Version = BitConverter.ToUInt16(f.Payload, 0) } : null,
                _ => null,
            };
            if (model == null) { v.Kind = "raw"; return v; }
            v.Data = model;
            v.Editable = BinUtil.SameBytes(Encode(v.Kind, model, g) ?? Array.Empty<byte>(), f.Payload);
            if (!v.Editable) v.Kind = "raw";
        }
        catch (Exception ex) when (ex is ParseFail || ex is EndOfStreamException || ex is InvalidDataException) { v.Kind = "raw"; v.Data = null; }
        return v;
    }

    static T? Simple<T>(byte[] p, Func<BinaryReader, T> read, Action<T, int> setVer) where T : class
    {
        using var r = BinUtil.Reader(p);
        int ver = r.ReadUInt16(); var m = read(r); setVer(m, ver);
        return r.Left() == 0 ? m : null;
    }

    static LockableM? ReadLockable(byte[] p)
    {
        using var r = BinUtil.Reader(p);
        var m = new LockableM { Version = r.ReadUInt16(), Locked = r.ReadBoolean() };
        int n = r.ReadInt32(); if (n < 0 || n > 10000) return null;
        for (int i = 0; i < n; i++) m.Allowed.Add(PlatformId.Read(r));
        m.PasswordHash = r.Str();
        return r.Left() == 0 ? m : null;
    }

    static SignableM? ReadSignable(byte[] p)
    {
        using var r = BinUtil.Reader(p);
        var m = new SignableM { Version = r.ReadUInt16() };
        m.HasText = r.ReadBoolean();
        if (m.HasText) { m.Text = r.Str(); m.Author = PlatformId.Read(r); }
        return r.Left() == 0 ? m : null;
    }

    static CanvasM? ReadCanvas(byte[] p)
    {
        using var r = BinUtil.Reader(p);
        var m = new CanvasM { Version = r.ReadUInt16() };
        if (m.Version < 21) return null;
        m.Library = r.Str();
        m.Guid = new Guid(r.ReadBytes(16)).ToString();
        m.Blend = r.ReadByte(); m.Rotation = r.ReadByte(); m.ShowOnImposter = r.ReadBoolean();
        return r.Left() == 0 ? m : null;
    }

    public static byte[]? Encode(string kind, object model, GameData g)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        switch (model)
        {
            case StorageData s: return s.Write();
            case LockableM l:
                w.Write((ushort)l.Version); w.Write(l.Locked); w.Write(l.Allowed.Count); foreach (var a in l.Allowed) PlatformId.Write(w, a); w.Str(l.PasswordHash); break;
            case DoorM d: w.Write((ushort)d.Version); w.Write(d.IsOpen); break;
            case LandClaimM c: w.Write((ushort)c.Version); w.Write(c.ShowBounds); break;
            case SignableM s:
                w.Write((ushort)s.Version); w.Write(s.HasText);
                if (s.HasText) { w.Str(s.Text); PlatformId.Write(w, s.Author); }
                break;
            case CanvasM c:
                w.Write((ushort)c.Version); w.Str(c.Library); w.Write(System.Guid.Parse(c.Guid).ToByteArray()); w.Write((byte)c.Blend); w.Write((byte)c.Rotation); w.Write(c.ShowOnImposter); break;
            case MarkerM m: w.Write((ushort)m.Version); break;
            default: return null;
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Apply an edited model (JSON from the UI) to a feature of a tile entity.</summary>
    public static void Apply(TeFeat f, JsonElement data, GameData g)
    {
        var kind = KindOf(f.Hash);
        object? model = kind switch
        {
            "storage" => null,        // storage is edited through the container editor
            "lockable" => data.Deserialize<LockableM>(Opt),
            "door" => data.Deserialize<DoorM>(Opt),
            "landclaim" => data.Deserialize<LandClaimM>(Opt),
            "signable" => data.Deserialize<SignableM>(Opt),
            "canvas" => data.Deserialize<CanvasM>(Opt),
            _ => null,
        };
        if (model == null) throw new InvalidOperationException("The feature \"" + NameOf(f.Hash) + "\" cannot be edited here.");
        var bytes = Encode(kind, model, g) ?? throw new InvalidOperationException("Could not encode the feature.");
        // validate: the result must decode back to something editable
        var probe = Decode(new TeFeat { Hash = f.Hash, Payload = bytes }, g);
        if (!probe.Editable) throw new InvalidOperationException("The edited values could not be written back safely.");
        f.Payload = bytes;
    }
}
