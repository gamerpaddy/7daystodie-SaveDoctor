using System.Xml;
using SaveDoctor.Formats;

namespace SaveDoctor.Core;

/// <summary>
/// Lookup data that lives inside the save itself: block/item name↔id tables (*.nim) and the configs the
/// server dumped into ConfigsDump (item modifiers, entity class names). All of it is optional – the parsers
/// degrade gracefully when something is missing.
/// </summary>
public sealed class GameData
{
    readonly SaveCtx ctx;
    public Dictionary<int, string> BlockNames { get; } = new();
    public Dictionary<int, string> ItemNames { get; } = new();   // ids are the full ids (>= 65536)
    public Dictionary<string, int> ItemIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> BlockIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ModifierItemNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> EntityClassNames { get; } = new();
    public HashSet<string> QuestIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ItemInfo> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasItemsXml, HasModifiers, HasEntityClasses, HasQuests;

    public sealed class ItemInfo
    {
        public string Name = "";
        public bool ShowQuality;
        public int[]? ModSlots;
        public int StackSize = 1;
        public string? CustomIcon;
    }

    /// <summary>Icon file name (without .png) for an id, honouring the item's CustomIcon property.</summary>
    public string IconName(int fullType)
    {
        var n = NameOf(fullType);
        if (Items.TryGetValue(n, out var it) && !string.IsNullOrEmpty(it.CustomIcon)) return it.CustomIcon!;
        return n;
    }

    public const int ItemsStartHere = 65536;

    public GameData(SaveCtx ctx)
    {
        this.ctx = ctx;
        try { LoadNim("blockmappings.nim", BlockNames, BlockIds); } catch { }
        try { LoadNim("itemmappings.nim", ItemNames, ItemIds); } catch { }
        try { LoadModifiers(); } catch { }
        try { LoadItems(); } catch { }
        try { LoadEntityClasses(); } catch { }
        try { LoadQuests(); } catch { }
    }

    void LoadNim(string rel, Dictionary<int, string> names, Dictionary<string, int> ids)
    {
        if (!File.Exists(ctx.Abs(rel))) return;
        var nim = NameIdMap.Parse(ctx.ReadAll(rel));
        foreach (var e in nim.Entries) { names[e.Id] = e.Name; ids[e.Name] = e.Id; }
    }

    void LoadModifiers()
    {
        var p = ctx.Abs("ConfigsDump/item_modifiers.xml");
        if (!File.Exists(p)) return;
        using var xr = XmlReader.Create(p, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
        while (xr.Read())
            if (xr.NodeType == XmlNodeType.Element && xr.Name == "item_modifier")
            {
                var n = xr.GetAttribute("name");
                if (n != null) ModifierItemNames.Add(n);
            }
        HasModifiers = ModifierItemNames.Count > 0;
    }

    void LoadItems()
    {
        var p = ctx.Abs("ConfigsDump/items.xml");
        if (!File.Exists(p)) return;
        using var xr = XmlReader.Create(p, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
        ItemInfo? cur = null; int itemDepth = -1;
        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Element)
            {
                if (xr.Name == "item" && xr.GetAttribute("name") is string n)
                {
                    cur = new ItemInfo { Name = n }; itemDepth = xr.Depth; Items[n] = cur;
                }
                else if (cur != null && xr.Name == "property" && xr.Depth == itemDepth + 1)
                {
                    var pn = xr.GetAttribute("name"); var pv = xr.GetAttribute("value");
                    if (pn == "ShowQuality") cur.ShowQuality = string.Equals(pv, "true", StringComparison.OrdinalIgnoreCase);
                    else if (pn == "Stacknumber" && int.TryParse(pv, out int sn)) cur.StackSize = sn;
                    else if (pn == "CustomIcon") cur.CustomIcon = pv;
                }
                else if (cur != null && xr.Name == "passive_effect" && xr.GetAttribute("name") == "ModSlots")
                {
                    var v = xr.GetAttribute("value");
                    if (v != null)
                    {
                        var parts = v.Split(',');
                        var arr = new int[parts.Length];
                        bool ok = true;
                        for (int i = 0; i < parts.Length; i++) ok &= int.TryParse(parts[i].Trim(), out arr[i]);
                        if (ok) cur.ModSlots = arr;
                    }
                }
            }
            else if (xr.NodeType == XmlNodeType.EndElement && xr.Name == "item" && xr.Depth == itemDepth) cur = null;
        }
        HasItemsXml = Items.Count > 0;
    }

    void LoadEntityClasses()
    {
        var p = ctx.Abs("ConfigsDump/entityclasses.xml");
        if (!File.Exists(p)) return;
        using var xr = XmlReader.Create(p, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
        while (xr.Read())
            if (xr.NodeType == XmlNodeType.Element && xr.Name == "entity_class")
            {
                var n = xr.GetAttribute("name");
                if (n != null) EntityClassNames[MonoHash(n)] = n;
            }
        foreach (var n in new[] { "item", "fallingBlock", "fallingBlocks", "fallingTree", "playerMale", "playerFemale", "playerNewMale", "entityJunkDrone" })
            EntityClassNames[MonoHash(n)] = n;
        HasEntityClasses = EntityClassNames.Count > 0;
    }

    void LoadQuests()
    {
        var p = ctx.Abs("ConfigsDump/quests.xml");
        if (!File.Exists(p)) return;
        using var xr = XmlReader.Create(p, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
        while (xr.Read())
            if (xr.NodeType == XmlNodeType.Element && xr.Name == "quest")
            {
                var n = xr.GetAttribute("id");
                if (n != null) QuestIds.Add(n);
            }
        HasQuests = QuestIds.Count > 0;
    }

    /// <summary>The game stores entity classes as string.GetHashCode() of their name; its runtime uses the (non-randomised) .NET Framework x64 algorithm.</summary>
    public static int MonoHash(string s)
    {
        unchecked
        {
            int hash1 = 5381, hash2 = hash1;
            int i = 0;
            while (i < s.Length)
            {
                hash1 = ((hash1 << 5) + hash1) ^ s[i];
                if (i + 1 >= s.Length) break;
                hash2 = ((hash2 << 5) + hash2) ^ s[i + 1];
                i += 2;
            }
            return hash1 + hash2 * 1566083941;
        }
    }

    public string EntityClassName(int id) => EntityClassNames.TryGetValue(id, out var n) ? n : "#" + id;

    /// <summary>Is this the id of an item that the game treats as a mod (ItemClassModifier)? Those have a shorter serialized form.</summary>
    public bool IsModifier(int fullType)
    {
        if (fullType >= ItemsStartHere && ItemNames.TryGetValue(fullType, out var n)) return ModifierItemNames.Contains(n);
        return false;
    }

    public string NameOf(int fullType)
    {
        if (fullType == 0) return "";
        if (fullType >= ItemsStartHere) return ItemNames.TryGetValue(fullType, out var n) ? n : "item#" + (fullType - ItemsStartHere);
        return BlockNames.TryGetValue(fullType, out var b) ? b : "block#" + fullType;
    }
}
