using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class StateField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "bool";     // bool | int
    public int Value { get; set; }
    public int Min { get; set; }
    public int Max { get; set; } = 1;
    public string? Hint { get; set; }
}

/// <summary>Block value layout: type (16 bits), rotation (5), meta3 (1), meta1 (4), meta2 (4), child (1), decal (1) – plus what the game's block classes do with the meta bits.</summary>
public static class BlockState
{
    public static int Rotation(uint v) => (int)((v >> 16) & 31);
    public static int Meta(uint v) => (int)((v >> 22) & 15);
    public static int Meta2(uint v) => (int)((v >> 26) & 15);
    public static int Meta3(uint v) => (int)((v >> 21) & 1);
    public static bool Child(uint v) => (v & 0x40000000u) != 0;
    public static bool Decal(uint v) => (v & 0x80000000u) != 0;

    public static uint With(uint v, int? rot = null, int? meta = null, int? meta2 = null, int? meta3 = null, bool? decal = null)
    {
        if (rot.HasValue) v = (v & ~(31u << 16)) | ((uint)(rot.Value & 31) << 16);
        if (meta3.HasValue) v = (v & ~(1u << 21)) | ((uint)(meta3.Value & 1) << 21);
        if (meta.HasValue) v = (v & ~(15u << 22)) | ((uint)(meta.Value & 15) << 22);
        if (meta2.HasValue) v = (v & ~(15u << 26)) | ((uint)(meta2.Value & 15) << 26);
        if (decal.HasValue) v = (v & 0x7FFFFFFFu) | (decal.Value ? 0x80000000u : 0);
        return v;
    }

    static StateField B(string key, string label, int meta, int bit, string? hint = null) => new() { Key = key, Label = label, Kind = "bool", Value = (meta >> bit) & 1, Hint = hint };

    /// <summary>The named meaning of the meta bits for this block class (from the game's Block*.cs). Empty when the block does not use them.</summary>
    public static List<StateField> Describe(string cls, uint v)
    {
        int m = Meta(v), m2 = Meta2(v);
        var l = new List<StateField>();
        switch (cls)
        {
            case "Switch": l.Add(B("m0", "Receiving power", m, 0)); l.Add(B("m1", "Switched on", m, 1)); break;
            case "Light": case "Hazard": l.Add(B("m0", "Powered", m, 0)); l.Add(B("m1", "Light on", m, 1)); break;
            case "PoweredLight": case "Spotlight": case "Speaker": case "SpeakerTrader": case "Launcher": case "Ranged": case "BladeTrap": case "Generator": case "SolarPanel": case "BatteryBank": case "PowerSource":
                l.Add(B("m1", "On", m, 1)); break;
            case "MotionSensor": l.Add(B("m0", "Sensor active", m, 0)); l.Add(B("m1", "On", m, 1)); break;
            case "PoweredDoor": l.Add(B("m0", "Open", m, 0)); l.Add(B("m1", "Flag 2 (door state)", m, 1)); break;
            case "TrapDoor": l.Add(new StateField { Key = "meta", Label = "Open (1 = open)", Kind = "int", Value = m, Max = 15 }); break;
            case "PressurePlate": l.Add(B("m1", "Triggered", m, 1)); break;
            case "ActivateSwitch": l.Add(B("m0", "Active", m, 0)); l.Add(B("m1", "Already used (single use)", m, 1)); break;
            case "ActivateSingle": l.Add(B("m1", "Already used", m, 1)); break;
            case "Forge": case "Workstation": case "Campfire": l.Add(new StateField { Key = "meta", Label = "Burning / running", Kind = "int", Value = m, Max = 15, Hint = "0 = off, anything else = lit" }); break;
            case "PlantGrowing": l.Add(new StateField { Key = "meta", Label = "Growth stage", Kind = "int", Value = m, Max = 15 }); break;
            case "Sign": l.Add(B("m0", "Initialised", m, 0)); break;
            case "LiquidSource": case "Liquidv2": l.Add(new StateField { Key = "meta", Label = "Liquid state", Kind = "int", Value = m, Max = 15 }); l.Add(new StateField { Key = "meta2", Label = "Flow amount", Kind = "int", Value = m2, Max = 15 }); break;
            case "Torch": case "TorchHeatMap": case "PlacementTorch": l.Add(new StateField { Key = "meta", Label = "Burn time used (low 4 bits)", Kind = "int", Value = m, Max = 15 }); l.Add(new StateField { Key = "meta2", Label = "Burn time used (high bits)", Kind = "int", Value = m2, Max = 15 }); break;
            case "Deadgrass": case "Tallgrass": l.Add(new StateField { Key = "meta", Label = "Variant", Kind = "int", Value = m, Max = 15 }); break;
            case "Barbed": l.Add(new StateField { Key = "meta", Label = "Damage counter", Kind = "int", Value = m, Max = 15 }); break;
            case "SpawnEntity": l.Add(new StateField { Key = "meta", Label = "Spawn entry index", Kind = "int", Value = m, Max = 15 }); break;
            case "QuestActivate": l.Add(B("m0", "Activated", m, 0)); l.Add(new StateField { Key = "meta2", Label = "Generator state", Kind = "int", Value = m2, Max = 15 }); break;
            case "ElectricWire": l.Add(B("m1", "Broken wire", m, 1)); break;
        }
        return l;
    }

    /// <summary>Apply edited named fields back into the meta bits.</summary>
    public static uint Apply(uint v, string cls, Dictionary<string, int> fields)
    {
        int m = Meta(v), m2 = Meta2(v);
        foreach (var (k, val) in fields)
        {
            if (k == "m0") m = (m & ~1) | (val != 0 ? 1 : 0);
            else if (k == "m1") m = (m & ~2) | (val != 0 ? 2 : 0);
            else if (k == "meta") m = val & 15;
            else if (k == "meta2") m2 = val & 15;
        }
        return With(v, meta: m, meta2: m2);
    }
}
