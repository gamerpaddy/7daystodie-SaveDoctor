using System.Text.Json.Serialization;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

// Faithful ports of the (de)serialisation code in the game's Assembly-CSharp.dll (7DTD 3.x / V 3.3.0 b18).
// Every Read has a matching Write so that Write(Read(bytes)) can be verified byte-for-byte.

public sealed class MetaEntry
{
    public string Key { get; set; } = "";
    public int Tag { get; set; }              // 1=float 2=int 3=string
    public float? F { get; set; }
    public int? I { get; set; }
    public string? S { get; set; }
}

public sealed class StatEntry
{
    public int Type { get; set; }             // PassiveEffects (byte)
    public int Value { get; set; }            // short
    public int Added { get; set; }            // short
}

public sealed class ItemVal
{
    public int Type { get; set; }             // full id: < 65536 block, >= 65536 item. 0 = empty
    public float UseTimes { get; set; }
    public int Quality { get; set; }
    public int Meta { get; set; } = -1;
    public List<MetaEntry> Metadata { get; set; } = new();
    public List<StatEntry>? Stats { get; set; }
    public List<ItemVal?>? Mods { get; set; }
    public List<ItemVal?>? Cosmetics { get; set; }
    public int Flags { get; set; }
    public int Ammo { get; set; }
    public int Seed { get; set; }
    public long? Texture { get; set; }

    public static ItemVal Empty() => new();

    public static ItemVal? ReadOrNull(BinaryReader r, GameData g)
    {
        long at = r.Pos();
        byte b = r.ReadByte();
        if (b == 0) return null;
        return ReadData(r, g, b, at);
    }

    public static ItemVal Read(BinaryReader r, GameData g)
    {
        long at = r.Pos();
        byte b = r.ReadByte();
        if (b == 0) return new ItemVal();
        return ReadData(r, g, b, at);
    }

    static ItemVal ReadData(BinaryReader r, GameData g, int version, long at)
    {
        if (version < 5 || version > 9) throw new ParseFail($"Unsupported item format version {version}", at);
        var v = new ItemVal();
        int f = 0;
        if (version >= 8) f = r.ReadByte();
        int type = r.ReadUInt16();
        if ((f & 1) > 0) type += GameData.ItemsStartHere;
        if (version < 8 && type >= 32768) type += 32768;
        v.Type = type;
        v.UseTimes = version > 5 ? r.ReadSingle() : r.ReadUInt16();
        v.Quality = r.ReadUInt16();
        int meta = r.ReadUInt16();
        v.Meta = meta >= 65535 ? -1 : meta;
        if (version > 6)
        {
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                var me = new MetaEntry { Key = r.Str() };
                long tagAt = r.Pos();
                me.Tag = r.ReadInt32();
                switch (me.Tag)
                {
                    case 1: me.F = r.ReadSingle(); break;
                    case 2: me.I = r.ReadInt32(); break;
                    case 3: me.S = r.Str(); break;
                    case 0: break;
                    default: throw new ParseFail($"Unknown metadata type tag {me.Tag}", tagAt);
                }
                v.Metadata.Add(me);
            }
        }
        if ((f & 2) > 0)
        {
            int n = r.ReadByte();
            v.Stats = new();
            for (int i = 0; i < n; i++) v.Stats.Add(new StatEntry { Type = r.ReadByte(), Value = r.ReadInt16(), Added = r.ReadInt16() });
        }
        if (!g.IsModifier(type))
        {
            v.Mods = ReadModList(r, g);
            v.Cosmetics = ReadModList(r, g);
        }
        v.Flags = r.ReadByte();
        if (version > 2) v.Ammo = r.ReadByte();
        if (version > 3)
        {
            v.Seed = r.ReadUInt16();
            if (type == 0) v.Seed = 0;
        }
        if (version > 8 && r.ReadBoolean()) v.Texture = r.ReadInt64();
        return v;
    }

    static List<ItemVal?> ReadModList(BinaryReader r, GameData g)
    {
        int n = r.ReadByte();
        var l = new List<ItemVal?>();
        for (int i = 0; i < n; i++) l.Add(r.ReadBoolean() ? Read(r, g) : null);
        return l;
    }

    public void Write(BinaryWriter w)
    {
        if (Type == 0) { w.Write((byte)0); return; }
        w.Write((byte)9);
        int num = Type; byte b = 0;
        if (Type >= GameData.ItemsStartHere) { b = 1; num -= GameData.ItemsStartHere; }
        if (Stats != null) b |= 2;
        w.Write(b);
        w.Write((ushort)num);
        w.Write(UseTimes);
        w.Write((ushort)Quality);
        w.Write((ushort)(Meta < 0 ? 65535 : Meta));
        w.Write((byte)Metadata.Count);
        foreach (var m in Metadata)
        {
            w.Str(m.Key);
            w.Write(m.Tag);
            switch (m.Tag) { case 1: w.Write(m.F ?? 0f); break; case 2: w.Write(m.I ?? 0); break; case 3: w.Str(m.S ?? ""); break; }
        }
        if (Stats != null)
        {
            w.Write((byte)Stats.Count);
            foreach (var s in Stats) { w.Write((byte)s.Type); w.Write((short)s.Value); w.Write((short)s.Added); }
        }
        if (Mods != null || Cosmetics != null)
        {
            WriteModList(w, Mods);
            WriteModList(w, Cosmetics);
        }
        w.Write((byte)Flags);
        w.Write((byte)Ammo);
        w.Write((ushort)Seed);
        if (Texture.HasValue && Texture.Value != 0) { w.Write(true); w.Write(Texture.Value); } else w.Write(false);
    }

    static void WriteModList(BinaryWriter w, List<ItemVal?>? l)
    {
        l ??= new();
        w.Write((byte)l.Count);
        foreach (var m in l)
        {
            bool has = m != null && m.Type != 0;
            w.Write(has);
            if (has) m!.Write(w);
        }
    }
}

public sealed class Stack
{
    public int Count { get; set; }
    public ItemVal Item { get; set; } = new();

    public static Stack Read(BinaryReader r, GameData g)
    {
        var s = new Stack { Count = r.ReadUInt16() };
        s.Item = s.Count > 0 ? ItemVal.Read(r, g) : new ItemVal();
        return s;
    }

    public void Write(BinaryWriter w)
    {
        int n = Math.Min(Count, 65535);
        w.Write((ushort)n);
        if (Count != 0) Item.Write(w);
    }

    public static List<Stack> ReadList(BinaryReader r, GameData g)
    {
        int n = r.ReadUInt16();
        var l = new List<Stack>(n);
        for (int i = 0; i < n; i++) l.Add(Read(r, g));
        return l;
    }

    public static void WriteList(BinaryWriter w, List<Stack> l)
    {
        w.Write((ushort)l.Count);
        foreach (var s in l) s.Write(w);
    }
}

public sealed class Pref
{
    public int PlayerId { get; set; }
    public List<Stack>? Toolbelt { get; set; }
    public List<ItemVal?>? Equipment { get; set; }
    public List<Stack>? Bag { get; set; }

    public static Pref Read(BinaryReader r, GameData g)
    {
        var p = new Pref { PlayerId = r.ReadInt32() };
        if (r.ReadBoolean()) p.Toolbelt = Stack.ReadList(r, g);
        if (r.ReadBoolean())
        {
            int n = r.ReadUInt16();
            p.Equipment = new();
            for (int i = 0; i < n; i++) p.Equipment.Add(r.ReadBoolean() ? ItemVal.Read(r, g) : null);
        }
        if (r.ReadBoolean()) p.Bag = Stack.ReadList(r, g);
        return p;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(PlayerId);
        bool a = Toolbelt != null && Toolbelt.Count > 0; w.Write(a); if (a) Stack.WriteList(w, Toolbelt!);
        bool b = Equipment != null && Equipment.Count > 0; w.Write(b);
        if (b)
        {
            w.Write((ushort)Equipment!.Count);
            foreach (var e in Equipment) { w.Write(e != null); e?.Write(w); }
        }
        bool c = Bag != null && Bag.Count > 0; w.Write(c); if (c) Stack.WriteList(w, Bag!);
    }
}

/// <summary>ItemStackGrid – the container behind inventory, backpack and equipment.</summary>
public sealed class Grid
{
    public int Version { get; set; } = 2;
    public int Width { get; set; }
    public int Height { get; set; }
    public List<Stack> Items { get; set; } = new();
    public List<bool>? Locks { get; set; }
    public Pref? Prefs { get; set; }
    public ulong TimeTouched { get; set; }
    public bool PlayerOwned { get; set; }

    public static Grid Read(BinaryReader r, GameData g)
    {
        var gr = new Grid();
        long at = r.Pos();
        gr.Version = r.ReadUInt16();
        gr.Width = r.ReadInt32(); gr.Height = r.ReadInt32();
        int n = r.ReadInt16();
        if (gr.Width < 0 || gr.Height < 0 || n != (long)gr.Width * gr.Height)
            throw new ParseFail($"Item grid slot count {n} does not match container size {gr.Width}x{gr.Height}", at);
        for (int i = 0; i < n; i++) gr.Items.Add(Stack.Read(r, g));
        if (r.ReadBoolean())
        {
            int len = r.Read7();
            if (len < 0 || len > 1_000_000) throw new ParseFail("Implausible lock array length " + len, r.Pos());
            var bytes = r.ReadBytes((len + 7) / 8);
            gr.Locks = new List<bool>(len);
            for (int i = 0; i < len; i++) gr.Locks.Add((bytes[i >> 3] & (1 << (i & 7))) != 0);
        }
        if (r.ReadBoolean()) gr.Prefs = Pref.Read(r, g);
        if (gr.Version >= 2) { gr.TimeTouched = r.ReadUInt64(); gr.PlayerOwned = r.ReadBoolean(); }
        return gr;
    }

    public void Write(BinaryWriter w)
    {
        w.Write((ushort)2);
        w.Write(Width); w.Write(Height);
        w.Write((short)Items.Count);
        foreach (var s in Items) s.Write(w);
        w.Write(Locks != null);
        if (Locks != null)
        {
            w.Write7(Locks.Count);
            var bytes = new byte[(Locks.Count + 7) / 8];
            for (int i = 0; i < Locks.Count; i++) if (Locks[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
            w.Write(bytes);
        }
        w.Write(Prefs != null);
        Prefs?.Write(w);
        w.Write(TimeTouched);
        w.Write(PlayerOwned);
    }
}

public sealed class InventoryBlob
{
    public int Version { get; set; } = 1;
    public Grid Grid { get; set; } = new();
    public int SelectedSlot { get; set; }

    public static InventoryBlob Read(byte[] blob, GameData g)
    {
        if (blob.Length == 0) return new InventoryBlob();
        using var r = BinUtil.Reader(blob);
        var i = new InventoryBlob { Version = r.ReadByte() };
        i.Grid = Grid.Read(r, g);
        i.SelectedSlot = r.ReadByte();
        if (r.Left() != 0) throw new ParseFail($"{r.Left()} unexpected bytes after inventory", r.Pos());
        return i;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, BinUtil.Utf8, true);
        w.Write((byte)1); Grid.Write(w); w.Write((byte)SelectedSlot); w.Flush();
        return ms.ToArray();
    }
}

public sealed class BagBlob
{
    public Grid Grid { get; set; } = new();
    public static BagBlob Read(byte[] blob, GameData g)
    {
        if (blob.Length == 0) return new BagBlob();
        using var r = BinUtil.Reader(blob);
        byte v = r.ReadByte();
        if (v < 2) throw new ParseFail($"Legacy backpack format v{v} is not supported by the editor", 0);
        var b = new BagBlob { Grid = Grid.Read(r, g) };
        if (r.Left() != 0) throw new ParseFail($"{r.Left()} unexpected bytes after backpack", r.Pos());
        return b;
    }
    public byte[] Write()
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, BinUtil.Utf8, true);
        w.Write((byte)2); Grid.Write(w); w.Flush();
        return ms.ToArray();
    }
}

public sealed class EquipmentBlob
{
    public Grid Grid { get; set; } = new();
    public byte[] Tail { get; set; } = Array.Empty<byte>();   // cosmetic slots + unlocked cosmetics (kept verbatim)
    public static EquipmentBlob Read(byte[] blob, GameData g)
    {
        if (blob.Length == 0) return new EquipmentBlob();
        using var r = BinUtil.Reader(blob);
        byte v = r.ReadByte();
        if (v < 5) throw new ParseFail($"Legacy equipment format v{v} is not supported by the editor", 0);
        var e = new EquipmentBlob { Grid = Grid.Read(r, g) };
        e.Tail = r.ReadBytes((int)r.Left());
        return e;
    }
    public byte[] Write()
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, BinUtil.Utf8, true);
        w.Write((byte)6); Grid.Write(w); w.Write(Tail); w.Flush();
        return ms.ToArray();
    }
}

// ───────────────────────── Entities ─────────────────────────

public sealed class StatM
{
    public float Value { get; set; }
    public float MaxModifier { get; set; }
    public float BaseMax { get; set; }
    public float OriginalBaseMax { get; set; }
    public float OriginalValue { get; set; }

    public static StatM Read(BinaryReader r)
    {
        int ver = r.ReadInt32();
        var s = new StatM { Value = r.ReadSingle(), MaxModifier = r.ReadSingle() };
        if (ver <= 5) r.ReadSingle();
        s.BaseMax = r.ReadSingle(); s.OriginalBaseMax = r.ReadSingle(); s.OriginalValue = r.ReadSingle();
        return s;
    }
    public void Write(BinaryWriter w)
    {
        w.Write(6); w.Write(Value); w.Write(MaxModifier); w.Write(BaseMax); w.Write(OriginalBaseMax); w.Write(OriginalValue);
    }
}

public sealed class EntityStatsM
{
    public bool IsPlayer { get; set; }
    public StatM Health { get; set; } = new();
    public StatM? Stamina { get; set; }
    public StatM? Water { get; set; }
    public StatM? Food { get; set; }
    public int CoreTemp { get; set; }      // sbyte, stored value (game multiplies by 2)

    public static EntityStatsM Read(BinaryReader r, bool player)
    {
        var s = new EntityStatsM { IsPlayer = player };
        int ver = r.ReadInt32();
        s.Health = StatM.Read(r);
        if (player)
        {
            s.Stamina = StatM.Read(r);
            if (ver <= 10) StatM.Read(r);
            s.Water = StatM.Read(r);
            s.Food = StatM.Read(r);
            if (ver >= 11) s.CoreTemp = r.ReadSByte();
        }
        return s;
    }
    public void Write(BinaryWriter w)
    {
        w.Write(11);
        Health.Write(w);
        if (IsPlayer)
        {
            (Stamina ?? new StatM()).Write(w);
            (Water ?? new StatM()).Write(w);
            (Food ?? new StatM()).Write(w);
            w.Write((sbyte)CoreTemp);
        }
    }
}

public sealed class ProfileM
{
    public string Archetype { get; set; } = "";
    public bool IsMale { get; set; }
    public string Race { get; set; } = "";
    public int Variant { get; set; }
    public string Hair { get; set; } = "";
    public string HairColor { get; set; } = "";
    public string Mustache { get; set; } = "";
    public string Chops { get; set; } = "";
    public string Beard { get; set; } = "";
    public string EyeColor { get; set; } = "Blue01";

    public static ProfileM Read(BinaryReader r)
    {
        var p = new ProfileM();
        int v = r.ReadInt32();
        p.Archetype = r.Str(); p.IsMale = r.ReadBoolean(); p.Race = r.Str(); p.Variant = r.ReadByte();
        if (v > 1) p.Hair = r.Str();
        if (v > 2) p.HairColor = r.Str();
        if (v > 3) { p.Mustache = r.Str(); p.Chops = r.Str(); p.Beard = r.Str(); }
        if (v > 4) p.EyeColor = r.Str();
        return p;
    }
    public void Write(BinaryWriter w)
    {
        w.Write(5); w.Str(Archetype); w.Write(IsMale); w.Str(Race); w.Write((byte)Variant);
        w.Str(Hair); w.Str(HairColor); w.Str(Mustache); w.Str(Chops); w.Str(Beard); w.Str(EyeColor);
    }
}

/// <summary>EntityCreationData – how the game stores any entity (players, vehicles, drones, zombies, dropped items…).</summary>
public sealed class Ecd
{
    public int FileVersion { get; set; } = 38;
    public int EntityClass { get; set; }
    public int Id { get; set; }
    public float Lifetime { get; set; }
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public float RotX { get; set; }
    public float RotY { get; set; }
    public float RotZ { get; set; }
    public bool OnGround { get; set; }
    public int BodyVer { get; set; } = 4;
    public int DamageType { get; set; }
    public uint BodyFlags { get; set; }
    public EntityStatsM? Stats { get; set; }
    public int DeathTime { get; set; }
    public byte[]? Bag { get; set; }
    public int HomeX { get; set; }
    public int HomeY { get; set; }
    public int HomeZ { get; set; }
    public int HomeRange { get; set; } = -1;
    public int SpawnerSource { get; set; }
    // item entity
    public int BelongsPlayerId { get; set; } = -1;
    public int ClientEntityId { get; set; }
    public Stack? ItemStack { get; set; }
    public int ItemTrailer { get; set; }
    // falling block(s)/tree
    public List<uint>? BlockValues { get; set; }
    public List<int[]>? BlockPositions { get; set; }
    public List<long>? Textures { get; set; }
    public int[]? TreePos { get; set; }
    public float[]? TreeDir { get; set; }
    // player
    public ItemVal? HoldingItem { get; set; }
    public int TeamNumber { get; set; }
    public string EntityName { get; set; } = "";
    public string SkinTexture { get; set; } = "";
    public ProfileM? Profile { get; set; }
    public byte[] EntityData { get; set; } = Array.Empty<byte>();
    // drone
    public int OrderState { get; set; } = -1;
    public float Stress { get; set; }
    public long RequestedBy { get; set; } = -1;
    public byte[] RequestKey { get; set; } = new byte[16];

    public bool IsPlayerClass(GameData g) => g.EntityClassNames.TryGetValue(EntityClass, out var n) ? (n == "playerMale" || n == "playerFemale") : false;

    static int H(string s) => GameData.MonoHash(s);

    public static Ecd Read(BinaryReader r, GameData g, bool forcePlayer)
    {
        long start = r.Pos();
        var e = new Ecd();
        e.FileVersion = r.ReadByte();
        int b = e.FileVersion;
        if (b < 36 || b > 38) throw new ParseFail($"Unsupported entity data version {b}", start);
        e.EntityClass = r.ReadInt32();
        bool player = forcePlayer || e.EntityClass == H("playerMale") || e.EntityClass == H("playerFemale");
        e.Id = r.ReadInt32();
        e.Lifetime = r.ReadSingle();
        e.PosX = r.ReadSingle(); e.PosY = r.ReadSingle(); e.PosZ = r.ReadSingle();
        e.RotX = r.ReadSingle(); e.RotY = r.ReadSingle(); e.RotZ = r.ReadSingle();
        e.OnGround = r.ReadBoolean();
        e.BodyVer = r.ReadInt32();
        if (e.BodyVer < 4) throw new ParseFail($"Unsupported body damage version {e.BodyVer}", r.Pos() - 4);
        e.DamageType = r.ReadInt32(); e.BodyFlags = r.ReadUInt32();
        if (r.ReadBoolean()) e.Stats = EntityStatsM.Read(r, player);
        e.DeathTime = r.ReadInt16();
        if (b >= 38)
        {
            if (r.ReadBoolean()) e.Bag = r.Blob("entity bag");
        }
        else if (b >= 35)
        {
            if (r.ReadBoolean()) throw new ParseFail("Legacy entity bag format is not supported", r.Pos());
        }
        e.HomeX = r.ReadInt32(); e.HomeY = r.ReadInt32(); e.HomeZ = r.ReadInt32(); e.HomeRange = r.ReadInt16();
        e.SpawnerSource = r.ReadByte();
        int ec = e.EntityClass;
        if (ec == H("item"))
        {
            e.BelongsPlayerId = r.ReadInt32();
            e.ClientEntityId = r.ReadInt32();
            e.ItemStack = Stack.Read(r, g);
            e.ItemTrailer = r.ReadSByte();
        }
        else if (ec == H("fallingBlock"))
        {
            e.BlockValues = new() { r.ReadUInt32() };
            e.Textures = new() { r.ReadInt64() };
        }
        else if (ec == H("fallingBlocks"))
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 100000) throw new ParseFail("Implausible falling block count " + n, r.Pos() - 4);
            e.BlockValues = new(); e.BlockPositions = new(); e.Textures = new();
            for (int i = 0; i < n; i++) e.BlockValues.Add(r.ReadUInt32());
            for (int i = 0; i < n; i++) e.BlockPositions.Add(new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() });
            for (int i = 0; i < n; i++) e.Textures.Add(r.ReadInt64());
        }
        else if (ec == H("fallingTree"))
        {
            e.TreePos = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
            e.TreeDir = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
        }
        else if (player)
        {
            e.HoldingItem = ItemVal.Read(r, g);
            e.TeamNumber = r.ReadByte();
            e.EntityName = r.Str();
            e.SkinTexture = r.Str();
            if (r.ReadBoolean()) e.Profile = ProfileM.Read(r);
        }
        int edl = r.ReadUInt16();
        e.EntityData = edl > 0 ? r.ReadBytes(edl) : Array.Empty<byte>();
        if (r.ReadBoolean()) throw new ParseFail("Entities with trader data are not supported by the editor", r.Pos());
        if (ec == H("entityJunkDrone"))
        {
            e.BelongsPlayerId = r.ReadInt32();
            e.OrderState = r.ReadInt32();
        }
        e.Stress = r.ReadSingle();
        e.RequestedBy = r.ReadInt64();
        e.RequestKey = r.ReadBytes(16);
        return e;
    }

    public void Write(BinaryWriter w, GameData g)
    {
        int ec = EntityClass;
        bool player = HoldingItem != null || ec == H("playerMale") || ec == H("playerFemale");
        w.Write((byte)38);
        w.Write(ec); w.Write(Id); w.Write(Lifetime);
        w.Write(PosX); w.Write(PosY); w.Write(PosZ);
        w.Write(RotX); w.Write(RotY); w.Write(RotZ);
        w.Write(OnGround);
        w.Write(4); w.Write(DamageType); w.Write(BodyFlags);
        w.Write(Stats != null);
        Stats?.Write(w);
        w.Write((short)DeathTime);
        w.Write(Bag != null);
        if (Bag != null) w.WriteBlob(Bag);
        w.Write(HomeX); w.Write(HomeY); w.Write(HomeZ); w.Write((short)HomeRange);
        w.Write((byte)SpawnerSource);
        if (ec == H("item"))
        {
            w.Write(BelongsPlayerId); w.Write(ClientEntityId);
            (ItemStack ?? new Stack()).Write(w);
            w.Write((sbyte)ItemTrailer);
        }
        else if (ec == H("fallingBlock"))
        {
            w.Write(BlockValues![0]); w.Write(Textures![0]);
        }
        else if (ec == H("fallingBlocks"))
        {
            w.Write(BlockValues!.Count);
            foreach (var v in BlockValues) w.Write(v);
            foreach (var p in BlockPositions!) { w.Write(p[0]); w.Write(p[1]); w.Write(p[2]); }
            foreach (var t in Textures!) w.Write(t);
        }
        else if (ec == H("fallingTree"))
        {
            foreach (var v in TreePos!) w.Write(v);
            foreach (var v in TreeDir!) w.Write(v);
        }
        else if (player)
        {
            (HoldingItem ?? new ItemVal()).Write(w);
            w.Write((byte)TeamNumber);
            w.Str(EntityName); w.Str(SkinTexture);
            w.Write(Profile != null);
            Profile?.Write(w);
        }
        w.Write((ushort)EntityData.Length);
        if (EntityData.Length > 0) w.Write(EntityData);
        w.Write(false); // trader data
        if (ec == H("entityJunkDrone")) { w.Write(BelongsPlayerId); w.Write(OrderState); }
        w.Write(Stress);
        w.Write(RequestedBy);
        w.Write(RequestKey);
    }
}
