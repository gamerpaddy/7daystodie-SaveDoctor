using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>What the editor shows / changes of an entity stored in a chunk.</summary>
public sealed class EntityDetail
{
    public int Index { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";             // EntityLootContainer, EntityBackpack, EntityItem, EntityZombie …
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public bool Editable { get; set; }
    public string? Note { get; set; }
    public Stack? DroppedItem { get; set; }             // dropped item lying on the ground
    public Grid? Inventory { get; set; }                // loot bag / backpack / zombie loot
    public string? OverrideLoot { get; set; }
    public string? OverrideName { get; set; }
    public int? RefPlayerId { get; set; }               // backpack: the player it belongs to
    public bool HasMirror { get; set; }                 // container entities store their inventory twice (bag + entity data) – both are kept in sync
    public TraderDataM? Trader { get; set; }
    public float Lifetime { get; set; }
    public int BelongsPlayerId { get; set; }
    public ulong SpawnTime { get; set; }
}

public sealed class EntityEdit
{
    public float? X { get; set; }
    public float? Y { get; set; }
    public float? Z { get; set; }
    public Stack? DroppedItem { get; set; }
    public Grid? Inventory { get; set; }
    public string? OverrideLoot { get; set; }
    public string? OverrideName { get; set; }
    public TraderDataM? Trader { get; set; }
    public float? Lifetime { get; set; }
}

public static class EntityTools
{
    static readonly System.Text.RegularExpressions.Regex ContainerNameRx = new("^(Backpack|DroppedLootContainer|DroppedVehicleContainer|EntityLootContainer)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string KindOf(Ecd e, GameData g)
    {
        if (g.EntityKinds.TryGetValue(e.EntityClass, out var k)) return k;
        var n = g.EntityClassName(e.EntityClass);
        if (n == "item") return "EntityItem";
        if (n.StartsWith("Backpack", StringComparison.OrdinalIgnoreCase)) return "EntityBackpack";
        if (ContainerNameRx.IsMatch(n)) return "EntityLootContainer";
        return "";
    }

    /// <summary>Splits a container entity's data blob into (header bytes, loot names / player ref, bag bytes). Null when the layout does not match.</summary>
    sealed class ContainerData { public string? Loot, Name; public int? RefPlayer; public bool IsBackpack; }

    static int HeadLen(byte[] d) { int sp = d[0]; return 1 + (sp == 1 ? 12 : 0) + 8; }

    /// <summary>Container entities (loot containers, backpacks) repeat their bag inside the entity data: Entity.Write, then [loot list, name | player id], then the bag.</summary>
    static ContainerData? ParseContainerData(Ecd e, string kind, GameData g, out Grid? mirror)
    {
        mirror = null;
        if (kind is not ("EntityLootContainer" or "EntityBackpack") || e.EntityData.Length < 9) return null;
        try
        {
            int hl = HeadLen(e.EntityData);
            using var r = BinUtil.Reader(e.EntityData);
            r.BaseStream.Position = hl;
            var c = new ContainerData { IsBackpack = kind == "EntityBackpack" };
            if (c.IsBackpack) c.RefPlayer = r.ReadInt32();
            else { c.Loot = r.Str(); c.Name = r.Str(); }
            mirror = BagBlob.Read(e.EntityData.AsSpan((int)r.Pos()).ToArray(), g).Grid;
            return c;
        }
        catch (Exception ex) when (ex is ParseFail || ex is EndOfStreamException || ex is ArgumentOutOfRangeException) { mirror = null; return null; }
    }

    public static EntityDetail Describe(EntRec rec, int index, GameData g)
    {
        var e = rec.E;
        var d = new EntityDetail { Index = index, Id = e.Id, Name = g.EntityClassName(e.EntityClass), Kind = KindOf(e, g), X = e.PosX, Y = e.PosY, Z = e.PosZ, Editable = rec.Editable, Lifetime = e.Lifetime, BelongsPlayerId = e.BelongsPlayerId };
        if (!rec.Editable) d.Note = "this entity is stored in a layout SaveDoctor cannot reproduce byte for byte – shown read-only";
        if (e.ItemStack != null) d.DroppedItem = e.ItemStack;
        if (e.Trader != null) d.Trader = e.Trader;
        if (e.Bag != null)
        {
            try { d.Inventory = BagBlob.Read(e.Bag, g).Grid; }
            catch (ParseFail pf) { d.Note = (d.Note == null ? "" : d.Note + "; ") + "inventory: " + pf.Message; }
        }
        var cd = ParseContainerData(e, d.Kind, g, out var mirror);
        if (cd != null)
        {
            d.OverrideLoot = cd.Loot; d.OverrideName = cd.Name; d.RefPlayerId = cd.RefPlayer; d.HasMirror = true;
            d.Inventory ??= mirror;
        }
        return d;
    }

    public static void Apply(EntRec rec, EntityEdit ed, GameData g)
    {
        if (!rec.Editable) throw new InvalidOperationException("This entity cannot be edited safely (its stored layout is not reproduced exactly).");
        var e = rec.E;
        if (ed.X.HasValue) e.PosX = ed.X.Value;
        if (ed.Y.HasValue) e.PosY = ed.Y.Value;
        if (ed.Z.HasValue) e.PosZ = ed.Z.Value;
        if (ed.Lifetime.HasValue) e.Lifetime = ed.Lifetime.Value;
        if (ed.DroppedItem != null)
        {
            if (e.ItemStack == null) throw new InvalidOperationException("This entity is not a dropped item.");
            if (ed.DroppedItem.Count <= 0 || ed.DroppedItem.Item.Type == 0) throw new InvalidOperationException("A dropped item needs an item and a count above 0 – delete the entity instead.");
            e.ItemStack = ed.DroppedItem;
        }
        if (ed.Trader != null)
        {
            if (e.Trader == null) throw new InvalidOperationException("This entity has no trader data.");
            e.Trader = ed.Trader;
        }
        if (ed.Inventory != null)
        {
            var kind0 = KindOf(e, g);
            ParseContainerData(e, kind0, g, out var mirror0);
            Grid oldGrid = e.Bag != null ? BagBlob.Read(e.Bag, g).Grid : mirror0 ?? throw new InvalidOperationException("This entity has no inventory.");
            if (ed.Inventory.Items.Count != oldGrid.Items.Count || ed.Inventory.Width != oldGrid.Width || ed.Inventory.Height != oldGrid.Height)
                throw new InvalidOperationException($"The inventory has {oldGrid.Width}×{oldGrid.Height} slots – the edited grid has a different size.");
            foreach (var st in ed.Inventory.Items) { if (st.Count < 0 || st.Count > 65535) throw new InvalidOperationException("Item count out of range."); if (st.Count == 0 || st.Item.Type == 0) { st.Count = 0; st.Item = new ItemVal(); } }
            ed.Inventory.Locks = oldGrid.Locks; ed.Inventory.Prefs = oldGrid.Prefs; ed.Inventory.PlayerOwned = oldGrid.PlayerOwned;
            ed.Inventory.TimeTouched = oldGrid.TimeTouched;
            var oldBytes = new BagBlob { Grid = oldGrid }.Write();
            // an unchanged inventory is left alone – in particular an unopened bag stays unopened (the game rolls its loot on first opening)
            if (BinUtil.SameBytes(oldBytes, new BagBlob { Grid = ed.Inventory }.Write())) { ed.Inventory = null; }
            else if (ed.Inventory.TimeTouched == 0) ed.Inventory.TimeTouched = 1UL;
        }
        if (ed.Inventory != null)
        {
            var kind = KindOf(e, g);
            var cd = ParseContainerData(e, kind, g, out var mirror);
            var bag = new BagBlob { Grid = ed.Inventory }.Write();
            if (e.Bag != null) e.Bag = bag;
            if (cd != null)
            {
                // mirror inside the entity data: head, [loot strings | player id], bag
                using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
                if (cd.IsBackpack) { w.Write(e.EntityData.AsSpan(0, HeadLen(e.EntityData))); w.Write(cd.RefPlayer ?? 0); }
                else { w.Write(e.EntityData.AsSpan(0, HeadLen(e.EntityData))); w.Str(ed.OverrideLoot ?? cd.Loot ?? ""); w.Str(ed.OverrideName ?? cd.Name ?? ""); }
                w.Write(bag); w.Flush();
                e.EntityData = ms.ToArray();
            }
        }
        else if (ed.OverrideLoot != null || ed.OverrideName != null)
        {
            var cd = ParseContainerData(e, KindOf(e, g), g, out var mirror);
            if (cd == null || cd.IsBackpack || mirror == null) throw new InvalidOperationException("Only loot containers have a loot list / name.");
            using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
            w.Write(e.EntityData.AsSpan(0, HeadLen(e.EntityData))); w.Str(ed.OverrideLoot ?? cd.Loot ?? ""); w.Str(ed.OverrideName ?? cd.Name ?? "");
            w.Write(new BagBlob { Grid = mirror }.Write()); w.Flush();
            e.EntityData = ms.ToArray();
        }
        rec.Dirty = true;
    }
}
