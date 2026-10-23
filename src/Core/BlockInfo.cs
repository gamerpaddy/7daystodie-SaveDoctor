using System.Xml.Linq;

namespace SaveDoctor.Core;

/// <summary>Where the game / dedicated server install lives (for icons and Data\Config\*.xml).</summary>
public static class GameInstall
{
    public static string? Override;       // path of Data\ItemIcons given by the user
    static string? dataDirCache;

    public static string? DataDir()
    {
        if (Override != null && Directory.Exists(Override)) return Path.GetDirectoryName(Override.TrimEnd('\\', '/'));
        if (dataDirCache != null && Directory.Exists(dataDirCache)) return dataDirCache;
        var roots = new List<string>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            foreach (var rel in new[] { "SteamLibrary\\SteamApps\\common", "SteamLibrary\\steamapps\\common", "Steam\\steamapps\\common", "Program Files (x86)\\Steam\\steamapps\\common", "Program Files\\Steam\\steamapps\\common", "SteamApps\\common" })
                roots.Add(Path.Combine(d.RootDirectory.FullName, rel));
        foreach (var r in roots.Where(Directory.Exists))
            foreach (var g in Directory.EnumerateDirectories(r, "7 Days to Die*"))
            {
                var p = Path.Combine(g, "Data");
                if (Directory.Exists(Path.Combine(p, "Config")) || Directory.Exists(Path.Combine(p, "ItemIcons"))) return dataDirCache = p;
            }
        return null;
    }

    public static string? IconDir()
    {
        if (Override != null && Directory.Exists(Override)) return Override;
        var d = DataDir();
        var p = d == null ? null : Path.Combine(d, "ItemIcons");
        return p != null && Directory.Exists(p) ? p : null;
    }

    public static string? ConfigFile(string name)
    {
        var d = DataDir();
        var p = d == null ? null : Path.Combine(d, "Config", name);
        return p != null && File.Exists(p) ? p : null;
    }
}

public sealed class BlockDef
{
    public string Name = "";
    public string? Extends;
    public Dictionary<string, string> Props = new(StringComparer.OrdinalIgnoreCase);
    public bool CompositeTe;
    public int[] Dim = { 1, 1, 1 };
    public string Shape = "";
    public string Class = "";
    public string Group = "";
    public string Material = "";
    public string? MapColor;
    public string? LootList;
    public bool Terrain => Shape.Equals("Terrain", StringComparison.OrdinalIgnoreCase);
    public bool MultiDim => Dim[0] * Dim[1] * Dim[2] > 1;
    public bool NeedsTileEntity;
}

/// <summary>Block properties read from the game's blocks.xml (optional – only used to protect the block editor from placing broken blocks).</summary>
public sealed class BlockCatalog
{
    static BlockCatalog? shared;
    public static BlockCatalog Shared => shared ??= Load();

    public Dictionary<string, BlockDef> ByName = new(StringComparer.OrdinalIgnoreCase);
    public bool Loaded;

    static readonly HashSet<string> TeClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CompositeTileEntity","Light","PoweredDoor","VendingMachine","Collector","Workstation","Sleeper","PressurePlate","Campfire","TripWire","TimerRelay",
        "Switch","Spotlight","SpeakerTrader","Speaker","SolarPanel","Ranged","PoweredLight","Powered","MotionSensor","Launcher","Generator","Forge","ElectricWire",
        "BatteryBank","BladeTrap","Trader","SecureLoot","Loot","Sign","LandClaim","Taskboard","SecureDoor","TriggerDowngrade"
    };

    public static BlockCatalog Load()
    {
        var c = new BlockCatalog();
        try
        {
            var f = GameInstall.ConfigFile("blocks.xml");
            if (f == null) return c;
            var doc = XDocument.Load(f);
            foreach (var b in doc.Descendants("block"))
            {
                var name = (string?)b.Attribute("name");
                if (name == null) continue;
                var d = new BlockDef { Name = name };
                foreach (var p in b.Elements("property"))
                {
                    var pn = (string?)p.Attribute("name");
                    var pc = (string?)p.Attribute("class");
                    if (pc == "CompositeFeatures") { d.CompositeTe = true; foreach (var s in p.Descendants("property").Where(x => (string?)x.Attribute("name") == "LootList")) d.LootList ??= (string?)s.Attribute("value"); }
                    if (pn == null) continue;
                    var v = (string?)p.Attribute("value") ?? "";
                    d.Props[pn] = v;
                    if (pn.Equals("Extends", StringComparison.OrdinalIgnoreCase)) { d.Extends = v; d.Props["__extendsExclude"] = (string?)p.Attribute("param1") ?? ""; }
                }
                c.ByName[name] = d;
            }
            foreach (var d in c.ByName.Values) c.Resolve(d, 0);
            c.Loaded = true;
        }
        catch { }
        return c;
    }

    void Resolve(BlockDef d, int depth)
    {
        string Get(string key)
        {
            var cur = d; int guard = 0;
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (cur != null && guard++ < 20)
            {
                if (cur.Props.TryGetValue(key, out var v) && !excluded.Contains(key)) return v;
                if (cur.Extends == null) break;
                foreach (var e in (cur.Props.GetValueOrDefault("__extendsExclude") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) excluded.Add(e.Trim());
                if (!ByName.TryGetValue(cur.Extends, out var parent)) break;
                cur = parent;
            }
            return "";
        }
        d.Shape = Get("Shape");
        d.Class = Get("Class");
        d.Group = Get("Group");
        d.Material = Get("Material");
        d.MapColor = Get("MapColor") is { Length: > 0 } mc ? mc : null;
        var dim = Get("MultiBlockDim").Split(',');
        if (dim.Length == 3 && int.TryParse(dim[0], out int dx) && int.TryParse(dim[1], out int dy) && int.TryParse(dim[2], out int dz)) d.Dim = new[] { Math.Max(1, dx), Math.Max(1, dy), Math.Max(1, dz) };
        if (!d.CompositeTe)
        {
            var cur = d; int guard = 0;
            while (cur != null && guard++ < 20 && !d.CompositeTe)
            {
                if (cur.CompositeTe) d.CompositeTe = true;
                if (cur.Extends == null || !ByName.TryGetValue(cur.Extends, out cur!)) break;
            }
        }
        d.NeedsTileEntity = d.CompositeTe || TeClasses.Contains(d.Class);
    }

    public BlockDef? Get(string name) => ByName.GetValueOrDefault(name);
}
