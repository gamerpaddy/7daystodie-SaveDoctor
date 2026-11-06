using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>A tile entity that is not a composite one (workstations, vending machines, power blocks, lights …). Layouts follow the game's TileEntity*.read/write in persistency mode.</summary>
public interface ILegacyTe
{
    string Kind { get; }
    /// <summary>Writes everything after the common header (ushort 19, position, heap-map time).</summary>
    void WriteBody(BinaryWriter w);
}

public sealed class TraderEntryM
{
    public Stack Item { get; set; } = new();
    public int Markup { get; set; }           // sbyte: price markup steps (−4 … 100)
    public bool AddedByPlayer { get; set; }
}

/// <summary>TraderData – the stock of a trader NPC or vending machine.</summary>
public sealed class TraderDataM
{
    public int TraderId { get; set; } = -1;
    public ulong LastInventoryUpdate { get; set; }
    public int Version { get; set; } = 2;
    public List<TraderEntryM> Primary { get; set; } = new();
    public List<List<Stack>> Tiers { get; set; } = new();
    public int AvailableMoney { get; set; }

    public static TraderDataM Read(BinaryReader r, GameData g)
    {
        var t = new TraderDataM { TraderId = r.ReadInt32(), LastInventoryUpdate = r.ReadUInt64() };
        long at = r.Pos();
        t.Version = r.ReadByte();
        if (t.Version < 2) throw new ParseFail($"Legacy trader data version {t.Version} is not supported", at);
        int n = r.ReadInt32();
        if (n < 0 || n > 100000) throw new ParseFail("Implausible trader inventory size " + n, r.Pos() - 4);
        for (int i = 0; i < n; i++) t.Primary.Add(new TraderEntryM { Item = Stack.Read(r, g), Markup = r.ReadSByte(), AddedByPlayer = r.ReadBoolean() });
        int tiers = r.ReadByte();
        for (int i = 0; i < tiers; i++) t.Tiers.Add(Stack.ReadList(r, g));
        t.AvailableMoney = r.ReadInt32();
        return t;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(TraderId); w.Write(LastInventoryUpdate); w.Write((byte)2);
        w.Write(Primary.Count);
        foreach (var e in Primary) { e.Item.Write(w); w.Write((sbyte)e.Markup); w.Write(e.AddedByPlayer); }
        w.Write((byte)Tiers.Count);
        foreach (var t in Tiers) Stack.WriteList(w, t);
        w.Write(AvailableMoney);
    }
}

public sealed class VendingM : ILegacyTe
{
    public string Kind => "vending";
    public bool Locked { get; set; }
    public PlatformId? Owner { get; set; }
    public string PasswordHash { get; set; } = "";
    public List<PlatformId?> Allowed { get; set; } = new();
    public int RentalEndDay { get; set; }
    public TraderDataM Trader { get; set; } = new();
    public bool Rentable { get; set; }
    public ulong NextAutoBuy { get; set; }

    public void WriteBody(BinaryWriter w)
    {
        w.Write(3); w.Write(Locked); PlatformId.Write(w, Owner); w.Str(PasswordHash);
        w.Write(Allowed.Count); foreach (var a in Allowed) PlatformId.Write(w, a);
        w.Write(RentalEndDay);
        Trader.Write(w);
        if (Rentable) w.Write(NextAutoBuy);
    }
}

public sealed class CraftDoneM
{
    public int CrafterEntityId { get; set; }
    public Stack Item { get; set; } = new();
    public string RecipeName { get; set; } = "";
    public int Exp { get; set; }
    public int RecipeUsedCount { get; set; }
    public string ItemScrapped { get; set; } = "";
}

public sealed class WorkstationM : ILegacyTe
{
    public string Kind => "workstation";
    public ulong LastTick { get; set; }
    public List<Stack> Fuel { get; set; } = new();
    public List<Stack> Input { get; set; } = new();
    public List<Stack> Tools { get; set; } = new();
    public List<Stack> Output { get; set; } = new();
    public List<RecipeQueueM> Queue { get; set; } = new();
    public List<CraftDoneM> CraftComplete { get; set; } = new();
    public bool IsBurning { get; set; }
    public float BurnTimeLeft { get; set; }
    public List<float> MeltTimes { get; set; } = new();
    public bool PlayerPlaced { get; set; }
    public List<Stack> LastInput { get; set; } = new();

    static void Arr(BinaryWriter w, List<Stack> l) { w.Write((byte)l.Count); foreach (var s in l) s.Write(w); }
    static List<Stack> Arr(BinaryReader r, GameData g) { int n = r.ReadByte(); var l = new List<Stack>(n); for (int i = 0; i < n; i++) l.Add(Stack.Read(r, g)); return l; }

    public static WorkstationM Read(BinaryReader r, GameData g)
    {
        var m = new WorkstationM { LastTick = r.ReadUInt64() };
        m.Fuel = Arr(r, g); m.Input = Arr(r, g); m.Tools = Arr(r, g); m.Output = Arr(r, g);
        int q = r.ReadByte(); for (int i = 0; i < q; i++) m.Queue.Add(RecipeQueueM.Read(r, g));
        int c = r.ReadInt16();
        for (int i = 0; i < c; i++)
        {
            r.ReadUInt16();
            m.CraftComplete.Add(new CraftDoneM { CrafterEntityId = r.ReadInt32(), Item = Stack.Read(r, g), RecipeName = r.Str(), Exp = r.ReadInt32(), RecipeUsedCount = r.ReadUInt16(), ItemScrapped = r.Str() });
        }
        m.IsBurning = r.ReadBoolean(); m.BurnTimeLeft = r.ReadSingle();
        int mt = r.ReadByte(); for (int i = 0; i < mt; i++) m.MeltTimes.Add(r.ReadSingle());
        m.PlayerPlaced = r.ReadBoolean();
        m.LastInput = Arr(r, g);
        return m;
    }

    public void WriteBody(BinaryWriter w)
    {
        w.Write((byte)50);
        w.Write(LastTick);
        Arr(w, Fuel); Arr(w, Input); Arr(w, Tools); Arr(w, Output);
        w.Write((byte)Queue.Count); foreach (var q in Queue) q.Write(w);
        w.Write((short)CraftComplete.Count);
        foreach (var c in CraftComplete) { w.Write((ushort)1); w.Write(c.CrafterEntityId); c.Item.Write(w); w.Str(c.RecipeName); w.Write(c.Exp); w.Write((ushort)c.RecipeUsedCount); w.Str(c.ItemScrapped); }
        w.Write(IsBurning); w.Write(BurnTimeLeft);
        w.Write((byte)MeltTimes.Count); foreach (var f in MeltTimes) w.Write(f);
        w.Write(PlayerPlaced);
        Arr(w, LastInput);
    }
}

public sealed class ForgeM : ILegacyTe
{
    public string Kind => "forge";
    public ulong LastTick { get; set; }
    public List<Stack> Fuel { get; set; } = new();
    public List<Stack> Input { get; set; } = new();
    public Stack Mold { get; set; } = new();
    public Stack Output { get; set; } = new();
    public int FuelInForgeTicks { get; set; }
    public int MoldedMetalSoFar { get; set; }
    public int MetalInForge { get; set; }
    public ItemVal Burning { get; set; } = new();

    public static ForgeM Read(BinaryReader r, GameData g)
    {
        var m = new ForgeM { LastTick = r.ReadUInt64() };
        int nf = r.ReadByte(); for (int i = 0; i < nf; i++) m.Fuel.Add(Stack.Read(r, g));
        int ni = r.ReadByte(); for (int i = 0; i < ni; i++) m.Input.Add(Stack.Read(r, g));
        m.Mold = Stack.Read(r, g); m.Output = Stack.Read(r, g);
        m.FuelInForgeTicks = r.ReadInt32(); m.MoldedMetalSoFar = r.ReadInt16(); m.MetalInForge = r.ReadInt16();
        m.Burning = ItemVal.Read(r, g);
        return m;
    }

    public void WriteBody(BinaryWriter w)
    {
        w.Write((ushort)18); w.Write(LastTick);
        w.Write((byte)Fuel.Count); foreach (var s in Fuel) s.Write(w);
        w.Write((byte)Input.Count); foreach (var s in Input) s.Write(w);
        Mold.Write(w); Output.Write(w);
        w.Write(FuelInForgeTicks); w.Write((short)MoldedMetalSoFar); w.Write((short)MetalInForge);
        Burning.Write(w);
    }
}

public sealed class CollectorFillM { public string Key { get; set; } = ""; public uint Slot { get; set; } public uint FillTime { get; set; } public uint FillTimeLeft { get; set; } }
public sealed class CollectorFlagM { public string Key { get; set; } = ""; public bool Value { get; set; } }
public sealed class CollectorTimeM { public string Key { get; set; } = ""; public ulong Time { get; set; } }

/// <summary>Collector blocks (water collector, dew collector, beehive …).</summary>
public sealed class CollectorM : ILegacyTe
{
    public string Kind => "collector";
    public List<CollectorTimeM> LastTimes { get; set; } = new();
    public List<CollectorFillM> Fills { get; set; } = new();
    public bool Underwater { get; set; }
    public bool Blocked { get; set; }
    public List<CollectorFlagM> OutOfFuel { get; set; } = new();
    public List<CollectorFlagM> IsFull { get; set; } = new();
    public List<Stack> Items { get; set; } = new();
    public List<Stack> Mods { get; set; } = new();
    public List<Stack> FuelSlots { get; set; } = new();
    public List<Stack> Catalysts { get; set; } = new();

    static List<Stack> Shorts(BinaryReader r, GameData g) { int n = r.ReadInt16(); if (n < 0) throw new ParseFail("Negative slot count", r.Pos() - 2); var l = new List<Stack>(n); for (int i = 0; i < n; i++) l.Add(Stack.Read(r, g)); return l; }
    static void Shorts(BinaryWriter w, List<Stack> l) { w.Write((short)l.Count); foreach (var s in l) s.Write(w); }

    public static CollectorM Read(BinaryReader r, GameData g)
    {
        var m = new CollectorM();
        int n = r.ReadUInt16(); for (int i = 0; i < n; i++) m.LastTimes.Add(new CollectorTimeM { Key = r.Str(), Time = r.ReadUInt64() });
        n = r.ReadUInt16(); for (int i = 0; i < n; i++) m.Fills.Add(new CollectorFillM { Key = r.Str(), Slot = r.ReadUInt32(), FillTime = r.ReadUInt32(), FillTimeLeft = r.ReadUInt32() });
        m.Underwater = r.ReadBoolean(); m.Blocked = r.ReadBoolean();
        n = r.ReadUInt16(); for (int i = 0; i < n; i++) m.OutOfFuel.Add(new CollectorFlagM { Key = r.Str(), Value = r.ReadByte() != 0 });
        n = r.ReadUInt16(); for (int i = 0; i < n; i++) m.IsFull.Add(new CollectorFlagM { Key = r.Str(), Value = r.ReadByte() != 0 });
        m.Items = Shorts(r, g); m.Mods = Shorts(r, g); m.FuelSlots = Shorts(r, g); m.Catalysts = Shorts(r, g);
        return m;
    }

    public void WriteBody(BinaryWriter w)
    {
        w.Write((ushort)21);
        w.Write((ushort)LastTimes.Count); foreach (var t in LastTimes) { w.Str(t.Key); w.Write(t.Time); }
        w.Write((ushort)Fills.Count); foreach (var f in Fills) { w.Str(f.Key); w.Write(f.Slot); w.Write(f.FillTime); w.Write(f.FillTimeLeft); }
        w.Write(Underwater); w.Write(Blocked);
        w.Write((ushort)OutOfFuel.Count); foreach (var f in OutOfFuel) { w.Str(f.Key); w.Write((byte)(f.Value ? 1 : 0)); }
        w.Write((ushort)IsFull.Count); foreach (var f in IsFull) { w.Str(f.Key); w.Write((byte)(f.Value ? 1 : 0)); }
        Shorts(w, Items); Shorts(w, Mods); Shorts(w, FuelSlots); Shorts(w, Catalysts);
    }
}

/// <summary>Light / sleeper volume markers and power blocks that carry no editable item data (kept byte for byte).</summary>
public sealed class OpaqueLegacyM : ILegacyTe
{
    public string Kind { get; set; } = "";
    public Dictionary<string, object?> Info { get; set; } = new();
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public void WriteBody(BinaryWriter w) => w.Write(Body);
}

public static class LegacyTe
{
    public static string TypeName(int type) => type switch
    {
        3 => "Collector", 4 => "Land claim (old)", 5 => "Loot (old)", 6 => "Trader (old)", 7 => "Vending machine", 8 => "Forge", 9 => "Campfire (old)", 10 => "Secure loot (old)", 11 => "Secure door (old)",
        12 => "Workstation", 13 => "Sign (old)", 14 => "Gore block (old)", 15 => "Powered block", 16 => "Power source", 17 => "Ranged trap", 18 => "Light", 19 => "Power trigger", 20 => "Sleeper marker", 21 => "Melee trap", 22 => "Secure loot signed (old)", 25 => "Composite", 27 => "Taskboard (old)",
        _ => "type " + type
    };

    static void PoweredBase(BinaryReader r)
    {
        r.ReadInt32(); r.ReadBoolean(); r.ReadByte();
        int wires = r.ReadByte();
        r.Need(wires * 12L + 12 + 8, "power wires");
        r.BaseStream.Seek(wires * 12L + 12 + 8, SeekOrigin.Current);
    }

    /// <summary>Read one non-composite tile entity (the type int is already consumed). Returns null when the type is not a known layout.</summary>
    public static TeRec? Read(BinaryReader r, byte[] raw, long recStart, int type, GameData g)
    {
        if (!(type is 3 or 7 or 8 or 12 or 15 or 16 or 17 or 18 or 19 or 20 or 21)) return null;
        var rec = new TeRec { Type = type, HasPos = true };
        int bv = r.ReadUInt16();
        var p = r.Vec3i();
        rec.X = p.X; rec.Y = p.Y; rec.Z = p.Z;
        rec.HeapTime = r.ReadUInt64();
        if (bv != 19) throw new ParseFail($"tile entity type {type}: unsupported base version {bv}", recStart);
        long bodyStart = r.Pos();
        ILegacyTe? model = null;
        switch (type)
        {
            case 18: { int lv = r.ReadUInt16(); if (lv != 18) throw new ParseFail($"Light version {lv}", recStart); r.BaseStream.Seek(27, SeekOrigin.Current); break; }
            case 20: { int lv = r.ReadUInt16(); if (lv != 18) throw new ParseFail($"Sleeper version {lv}", recStart); r.BaseStream.Seek(12, SeekOrigin.Current); break; }
            case 15: case 16: { PoweredBase(r); int lv = r.ReadUInt16(); if (lv != 18) throw new ParseFail($"Power block version {lv}", recStart); break; }
            case 17: case 21: { PoweredBase(r); int a = r.ReadUInt16(), b = r.ReadUInt16(); if (a != 18 || b != 18) throw new ParseFail($"Trap version {a}/{b}", recStart); PlatformId.Read(r); break; }
            case 19:
                {
                    PoweredBase(r); int lv = r.ReadUInt16(); if (lv != 18) throw new ParseFail($"Trigger version {lv}", recStart);
                    int tt = r.ReadByte(); if (tt > 4) throw new ParseFail($"Unknown trigger type {tt}", recStart);
                    if (tt == 3) PlatformId.Read(r);
                    break;
                }
            case 7:
                {
                    int v = r.ReadInt32();
                    if (v != 3) throw new ParseFail($"Vending machine version {v} is not supported", recStart);
                    var m = new VendingM { Locked = r.ReadBoolean(), Owner = PlatformId.Read(r), PasswordHash = r.Str() };
                    int n = r.ReadInt32(); if (n < 0 || n > 10000) throw new ParseFail("Implausible allowed-user count", r.Pos());
                    for (int i = 0; i < n; i++) m.Allowed.Add(PlatformId.Read(r));
                    m.RentalEndDay = r.ReadInt32();
                    m.Trader = TraderDataM.Read(r, g);
                    m.Rentable = g.RentableTraders.Contains(m.Trader.TraderId);
                    if (m.Rentable) m.NextAutoBuy = r.ReadUInt64();
                    model = m; break;
                }
            case 12:
                {
                    int v = r.ReadByte(); if (v != 50) throw new ParseFail($"Workstation version {v} is not supported", recStart);
                    model = WorkstationM.Read(r, g); break;
                }
            case 8:
                {
                    int lv = r.ReadUInt16(); if (lv < 3) throw new ParseFail($"Forge version {lv} is not supported", recStart);
                    model = ForgeM.Read(r, g); break;
                }
            case 3:
                {
                    int lv = r.ReadUInt16(); if (lv != 21) throw new ParseFail($"Collector version {lv} is not supported", recStart);
                    model = CollectorM.Read(r, g); break;
                }
        }
        rec.Raw = new byte[r.Pos() - recStart];
        Array.Copy(raw, recStart, rec.Raw, 0, rec.Raw.Length);
        if (model != null)
        {
            // only trust the typed model when it reproduces the stored bytes exactly
            using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
            model.WriteBody(w); w.Flush();
            var body = ms.ToArray();
            int off = (int)(bodyStart - recStart);
            bool same = body.Length == rec.Raw.Length - off && body.AsSpan().SequenceEqual(rec.Raw.AsSpan(off));
            if (same) rec.Legacy = model;
            else rec.LegacyNote = "stored in an older item format – shown read-only";
        }
        return rec;
    }

    public static byte[] Encode(TeRec t)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(t.Type); w.Write((ushort)19); w.Write(t.X); w.Write(t.Y); w.Write(t.Z); w.Write(t.HeapTime);
        t.Legacy!.WriteBody(w); w.Flush();
        return ms.ToArray();
    }
}
