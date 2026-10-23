using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class PowerNodeM
{
    public int Type { get; set; }                       // PowerItemTypes
    public int BlockId { get; set; }
    public int[] Pos { get; set; } = new int[3];
    public int[]? ParentPos { get; set; }               // only meaningful for top-level nodes (normally null)
    public int CurrentPower { get; set; }
    public bool IsOn { get; set; }
    public List<Stack>? Slots { get; set; }
    public int CurrentFuel { get; set; }
    public int SunLight { get; set; }
    public bool Toggled { get; set; }
    public int TriggerType { get; set; }                // 0 switch 1 plate 2 timer 3 motion 4 tripwire
    public bool Flag { get; set; }                      // isTriggered (switch) / isActive
    public int Delay { get; set; }
    public int Duration { get; set; }
    public float DelayStart { get; set; }
    public float PowerTime { get; set; }
    public int TargetType { get; set; }
    public int StartTime { get; set; }
    public int EndTime { get; set; }
    public bool Locked { get; set; }
    public List<PowerNodeM> Children { get; set; } = new();

    public const int None = 0, Consumer = 1, ConsumerToggle = 2, Trigger = 3, Timer = 4, Generator = 5, Solar = 6, Battery = 7, RangedTrap = 8, WireRelay = 9, TripWire = 10, Plate = 11;
    public static bool IsSource(int t) => t is Generator or Solar or Battery;
    public static bool IsTriggerLike(int t) => t is Trigger or Timer or TripWire or Plate;
    public static string TypeName(int t) => t switch
    {
        0 => "Generic", 1 => "Consumer", 2 => "Toggle consumer", 3 => "Trigger / switch", 4 => "Timer relay", 5 => "Generator", 6 => "Solar panel",
        7 => "Battery bank", 8 => "Ranged trap", 9 => "Electric wire relay", 10 => "Trip-wire relay", 11 => "Pressure plate", _ => "Type " + t
    };
}

/// <summary>power.dat – the whole electrical grid (PowerManager in Assembly-CSharp.dll).</summary>
public sealed class PowerFile
{
    public int Version { get; set; } = 2;
    public List<PowerNodeM> Circuits { get; set; } = new();
    public long TrailingBytes { get; set; }

    public static PowerFile Parse(byte[] data, GameData g)
    {
        using var r = BinUtil.Reader(data);
        r.Need(5, "header");
        var p = new PowerFile { Version = r.ReadByte() };
        if (p.Version < 1 || p.Version > 2) throw new ParseFail($"Unsupported power file version {p.Version}", 0);
        int n = r.ReadInt32();
        if (n < 0 || n > 1_000_000) throw new ParseFail("Implausible circuit count " + n, 1);
        for (int i = 0; i < n; i++)
        {
            int t = r.ReadByte();
            p.Circuits.Add(ReadNode(r, g, t, p.Version, 0));
        }
        p.TrailingBytes = r.Left();
        return p;
    }

    static PowerNodeM ReadNode(BinaryReader r, GameData g, int type, int version, int depth)
    {
        if (depth > 200) throw new ParseFail("Power node nesting is absurdly deep", r.Pos());
        long at = r.Pos();
        if (type < 0 || type > 11) throw new ParseFail($"Unknown power item type {type}", at - 1);
        var n = new PowerNodeM { Type = type };
        r.Need(2 + 12 + 1 + 1, "power node");
        n.BlockId = r.ReadUInt16();
        n.Pos = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
        if (r.ReadBoolean()) n.ParentPos = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
        int kids = r.ReadByte();
        for (int i = 0; i < kids; i++) n.Children.Add(ReadNode(r, g, r.ReadByte(), version, depth + 1));
        if (PowerNodeM.IsSource(type))
        {
            n.CurrentPower = r.ReadUInt16();
            n.IsOn = r.ReadBoolean();
            n.Slots = Stack.ReadList(r, g);
            if (type == PowerNodeM.Generator) n.CurrentFuel = r.ReadUInt16();
            else if (type == PowerNodeM.Solar && version >= 2) n.SunLight = r.ReadByte();
        }
        else if (type == PowerNodeM.ConsumerToggle) n.Toggled = r.ReadBoolean();
        else if (PowerNodeM.IsTriggerLike(type))
        {
            n.TriggerType = r.ReadByte();
            if (n.TriggerType > 4) throw new ParseFail($"Unknown trigger type {n.TriggerType}", r.Pos() - 1);
            n.Flag = r.ReadBoolean();
            if (n.TriggerType != 0)
            {
                n.Delay = r.ReadByte(); n.Duration = r.ReadByte();
                n.DelayStart = r.ReadSingle(); n.PowerTime = r.ReadSingle();
            }
            if (n.TriggerType == 3) n.TargetType = r.ReadInt32();
            if (type == PowerNodeM.Timer) { n.StartTime = r.ReadByte(); n.EndTime = r.ReadByte(); }
        }
        else if (type == PowerNodeM.RangedTrap)
        {
            n.Locked = r.ReadBoolean();
            n.Slots = Stack.ReadList(r, g);
            n.TargetType = r.ReadInt32();
        }
        return n;
    }

    public byte[] Build()
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, BinUtil.Utf8, true);
        w.Write((byte)Version);
        w.Write(Circuits.Count);
        foreach (var c in Circuits) { w.Write((byte)c.Type); WriteNode(w, c, null); }
        w.Flush();
        return ms.ToArray();
    }

    void WriteNode(BinaryWriter w, PowerNodeM n, PowerNodeM? parent)
    {
        w.Write((ushort)n.BlockId);
        w.Write(n.Pos[0]); w.Write(n.Pos[1]); w.Write(n.Pos[2]);
        var pp = parent != null ? parent.Pos : n.ParentPos;
        w.Write(pp != null);
        if (pp != null) { w.Write(pp[0]); w.Write(pp[1]); w.Write(pp[2]); }
        w.Write((byte)n.Children.Count);
        foreach (var c in n.Children) { w.Write((byte)c.Type); WriteNode(w, c, n); }
        int type = n.Type;
        if (PowerNodeM.IsSource(type))
        {
            w.Write((ushort)n.CurrentPower); w.Write(n.IsOn); Stack.WriteList(w, n.Slots ?? new());
            if (type == PowerNodeM.Generator) w.Write((ushort)n.CurrentFuel);
            else if (type == PowerNodeM.Solar) w.Write((byte)n.SunLight);
        }
        else if (type == PowerNodeM.ConsumerToggle) w.Write(n.Toggled);
        else if (PowerNodeM.IsTriggerLike(type))
        {
            w.Write((byte)n.TriggerType); w.Write(n.Flag);
            if (n.TriggerType != 0) { w.Write((byte)n.Delay); w.Write((byte)n.Duration); w.Write(n.DelayStart); w.Write(n.PowerTime); }
            if (n.TriggerType == 3) w.Write(n.TargetType);
            if (type == PowerNodeM.Timer) { w.Write((byte)n.StartTime); w.Write((byte)n.EndTime); }
        }
        else if (type == PowerNodeM.RangedTrap)
        {
            w.Write(n.Locked); Stack.WriteList(w, n.Slots ?? new()); w.Write(n.TargetType);
        }
    }

    public IEnumerable<PowerNodeM> All()
    {
        var st = new Stack<PowerNodeM>(Circuits.AsEnumerable().Reverse());
        while (st.Count > 0)
        {
            var n = st.Pop(); yield return n;
            foreach (var c in Enumerable.Reverse(n.Children)) st.Push(c);
        }
    }
}

/// <summary>drones.dat / vehicles.dat: 'vda\0', version 1, list of stored entities.</summary>
public sealed class EntityListFile
{
    public List<Ecd> Entities { get; set; } = new();
    public long TrailingBytes { get; set; }

    public static EntityListFile Parse(byte[] data, GameData g)
    {
        using var r = BinUtil.Reader(data);
        r.Need(9, "header");
        if (r.ReadByte() != 'v' || r.ReadByte() != 'd' || r.ReadByte() != 'a' || r.ReadByte() != 0) throw new ParseFail("Wrong signature (expected 'vda')", 0);
        if (r.ReadByte() != 1) throw new ParseFail("Unsupported version (expected 1)", 4);
        int n = r.ReadInt32();
        if (n < 0 || n > 1_000_000) throw new ParseFail("Implausible entity count " + n, 5);
        var f = new EntityListFile();
        for (int i = 0; i < n; i++) f.Entities.Add(Ecd.Read(r, g, false));
        f.TrailingBytes = r.Left();
        return f;
    }

    public byte[] Build(GameData g)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, BinUtil.Utf8, true);
        w.Write(new byte[] { (byte)'v', (byte)'d', (byte)'a', 0, 1 });
        w.Write(Entities.Count);
        foreach (var e in Entities) e.Write(w, g);
        w.Flush();
        return ms.ToArray();
    }
}
