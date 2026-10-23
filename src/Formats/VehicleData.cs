using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>Decoded EntityCreationData.entityData of a stored vehicle (EntityVehicle.Write, persistency mode).</summary>
public sealed class VehicleDataM
{
    public int SpawnerSource { get; set; }
    public int SpawnerBiomeHash { get; set; }
    public long SpawnerChunkKey { get; set; }
    public ulong WorldTimeBorn { get; set; }
    public int DeathHealth { get; set; }
    public float Stress { get; set; }
    public int SyncFlags { get; set; }
    public int SyncVersion { get; set; } = 3;
    public bool Headlight { get; set; }
    public List<int>? Attached { get; set; }
    public bool HasLock { get; set; }
    public bool InteractionLocked { get; set; }
    public bool Locked { get; set; }
    public PlatformId? Owner { get; set; }
    public string PasswordHash { get; set; } = "";
    public List<PlatformId?> AllowedUsers { get; set; } = new();
    public bool HasItem { get; set; }
    public Stack? Item { get; set; }
    public bool HasBag { get; set; }
    public Grid? Bag { get; set; }
    public int BagVersion { get; set; } = 2;

    public static VehicleDataM? TryParse(byte[] data, GameData g, out string? error)
    {
        error = null;
        try
        {
            using var r = BinUtil.Reader(data);
            var v = new VehicleDataM { SpawnerSource = r.ReadByte() };
            if (v.SpawnerSource == 1) { v.SpawnerBiomeHash = r.ReadInt32(); v.SpawnerChunkKey = r.ReadInt64(); }
            v.WorldTimeBorn = r.ReadUInt64();
            v.DeathHealth = r.ReadInt32();
            v.Stress = r.ReadSingle();
            v.SyncFlags = r.ReadUInt16();
            v.SyncVersion = r.ReadByte();
            if ((v.SyncFlags & 0x8000) != 0) { error = "live-motion sync data is not expected in a save file"; return null; }
            if ((v.SyncFlags & 0x4000) != 0) v.Headlight = r.ReadBoolean();
            if ((v.SyncFlags & 1) != 0) { int n = r.ReadByte(); v.Attached = new(); for (int i = 0; i < n; i++) v.Attached.Add(r.ReadInt32()); }
            if ((v.SyncFlags & 2) != 0)
            {
                v.HasLock = true;
                int b = r.ReadByte(); v.InteractionLocked = (b & 1) != 0; v.Locked = (b & 2) != 0;
                v.Owner = PlatformId.Read(r);
                v.PasswordHash = r.Str();
                int n = r.ReadByte();
                for (int i = 0; i < n; i++) v.AllowedUsers.Add(PlatformId.Read(r));
            }
            if ((v.SyncFlags & 4) != 0) { v.HasItem = true; r.ReadByte(); v.Item = Stack.Read(r, g); }
            if ((v.SyncFlags & 8) != 0)
            {
                v.HasBag = true; v.BagVersion = r.ReadByte();
                if (v.BagVersion < 2) { error = "legacy storage format"; return null; }
                v.Bag = Grid.Read(r, g);
            }
            if (r.Left() != 0) { error = r.Left() + " unexpected trailing bytes"; return null; }
            return v;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, BinUtil.Utf8, true);
        w.Write((byte)SpawnerSource);
        if (SpawnerSource == 1) { w.Write(SpawnerBiomeHash); w.Write(SpawnerChunkKey); }
        w.Write(WorldTimeBorn); w.Write(DeathHealth); w.Write(Stress);
        int flags = SyncFlags & ~(1 | 2 | 4 | 8 | 0x4000);
        if ((SyncFlags & 0x4000) != 0) flags |= 0x4000;
        if (Attached != null) flags |= 1;
        if (HasLock) flags |= 2;
        if (HasItem) flags |= 4;
        if (HasBag) flags |= 8;
        w.Write((ushort)flags); w.Write((byte)SyncVersion);
        if ((flags & 0x4000) != 0) w.Write(Headlight);
        if ((flags & 1) != 0) { w.Write((byte)Attached!.Count); foreach (var a in Attached) w.Write(a); }
        if ((flags & 2) != 0)
        {
            w.Write((byte)((InteractionLocked ? 1 : 0) | (Locked ? 2 : 0)));
            PlatformId.Write(w, Owner); w.Str(PasswordHash);
            w.Write((byte)AllowedUsers.Count); foreach (var u in AllowedUsers) PlatformId.Write(w, u);
        }
        if ((flags & 4) != 0) { w.Write((byte)1); (Item ?? new Stack()).Write(w); }
        if ((flags & 8) != 0) { w.Write((byte)2); (Bag ?? new Grid()).Write(w); }
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>Decoded EntityCreationData.entityData of a stored drone (EntityDrone.Write): header, owner/item and the sync block with orders, lock and cargo storage.</summary>
public sealed class DroneDataM
{
    public int SpawnerSource { get; set; }
    public int SpawnerBiomeHash { get; set; }
    public long SpawnerChunkKey { get; set; }
    public ulong WorldTimeBorn { get; set; }
    public int DeathHealth { get; set; }
    public float Stress { get; set; }
    public int Marker { get; set; } = 1;
    public PlatformId? Owner { get; set; }
    public ItemVal? Item { get; set; }

    /// <summary>True when the sync block was decoded; otherwise <see cref="Tail"/> holds it verbatim.</summary>
    public bool Parsed { get; set; }
    public byte[] Tail { get; set; } = Array.Empty<byte>();
    public int SyncFlags { get; set; }
    public int SyncVersion { get; set; } = 3;
    public PlatformId? SyncOwner { get; set; }
    public int Health { get; set; }
    public int Order { get; set; }                 // 0 follow, 1 stay
    public float[] SentryPos { get; set; } = new float[3];
    public int State { get; set; }                 // 3 = heal
    public bool UserRequestedHeal { get; set; }
    public bool InteractionLocked { get; set; }
    public bool Locked { get; set; }
    public PlatformId? LockOwner { get; set; }
    public string PasswordHash { get; set; } = "";
    public List<PlatformId?> AllowedUsers { get; set; } = new();
    public Grid? Bag { get; set; }
    public bool QuietMode { get; set; }
    public bool Flashlight { get; set; }
    public bool HealAllies { get; set; }
    public ItemVal? SyncItem { get; set; }

    public static DroneDataM? TryParse(byte[] data, GameData g, out string? error)
    {
        error = null;
        try
        {
            using var r = BinUtil.Reader(data);
            var d = new DroneDataM { SpawnerSource = r.ReadByte() };
            if (d.SpawnerSource == 1) { d.SpawnerBiomeHash = r.ReadInt32(); d.SpawnerChunkKey = r.ReadInt64(); }
            d.WorldTimeBorn = r.ReadUInt64(); d.DeathHealth = r.ReadInt32(); d.Stress = r.ReadSingle();
            d.Marker = r.ReadInt32();
            d.Owner = PlatformId.Read(r);
            d.Item = ItemVal.Read(r, g);
            long syncStart = r.Pos();
            try
            {
                d.SyncFlags = r.ReadUInt16();
                d.SyncVersion = r.ReadByte();
                if (d.SyncVersion < 3) throw new ParseFail("old drone sync format", r.Pos());
                int f = d.SyncFlags;
                if ((f & 1) != 0) { d.SyncOwner = PlatformId.Read(r); d.Health = r.ReadInt32(); }
                if ((f & 0x4000) != 0) { d.Order = r.ReadByte(); if (d.Order == 1) d.SentryPos = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() }; }
                if ((f & 0x8000) != 0) { d.State = r.ReadByte(); if (d.State == 3) d.UserRequestedHeal = r.ReadBoolean(); }
                if ((f & 2) != 0)
                {
                    int b = r.ReadByte(); d.InteractionLocked = (b & 1) != 0; d.Locked = (b & 2) != 0;
                    d.LockOwner = PlatformId.Read(r); d.PasswordHash = r.Str();
                    int n = r.ReadByte(); for (int i = 0; i < n; i++) d.AllowedUsers.Add(PlatformId.Read(r));
                }
                if ((f & 8) != 0)
                {
                    int bv = r.ReadByte(); if (bv < 2) throw new ParseFail("legacy storage", r.Pos());
                    d.Bag = Grid.Read(r, g);
                }
                if ((f & 0x20) != 0) d.QuietMode = r.ReadBoolean();
                if ((f & 0x40) != 0) d.Flashlight = r.ReadBoolean();
                if ((f & 0x100) != 0) d.HealAllies = r.ReadBoolean();
                if ((f & 0x80) != 0) d.SyncItem = ItemVal.Read(r, g);
                if (r.Left() != 0) throw new ParseFail(r.Left() + " trailing bytes", r.Pos());
                d.Parsed = true;
            }
            catch (Exception ex) when (ex is ParseFail or EndOfStreamException)
            {
                error = "drone sync block not decoded: " + ex.Message;
                r.BaseStream.Position = syncStart;
                d.Parsed = false; d.Bag = null;
                d.Tail = r.ReadBytes((int)r.Left());
            }
            return d;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, BinUtil.Utf8, true);
        w.Write((byte)SpawnerSource);
        if (SpawnerSource == 1) { w.Write(SpawnerBiomeHash); w.Write(SpawnerChunkKey); }
        w.Write(WorldTimeBorn); w.Write(DeathHealth); w.Write(Stress);
        w.Write(Marker); PlatformId.Write(w, Owner); (Item ?? new ItemVal()).Write(w);
        if (!Parsed) { w.Write(Tail); w.Flush(); return ms.ToArray(); }
        int f = SyncFlags;
        w.Write((ushort)f); w.Write((byte)3);
        if ((f & 1) != 0) { PlatformId.Write(w, SyncOwner); w.Write(Health); }
        if ((f & 0x4000) != 0) { w.Write((byte)Order); if (Order == 1) foreach (var v in SentryPos) w.Write(v); }
        if ((f & 0x8000) != 0) { w.Write((byte)State); if (State == 3) w.Write(UserRequestedHeal); }
        if ((f & 2) != 0)
        {
            w.Write((byte)((InteractionLocked ? 1 : 0) | (Locked ? 2 : 0)));
            PlatformId.Write(w, LockOwner); w.Str(PasswordHash);
            w.Write((byte)AllowedUsers.Count); foreach (var u in AllowedUsers) PlatformId.Write(w, u);
        }
        if ((f & 8) != 0) { w.Write((byte)2); (Bag ?? new Grid()).Write(w); }
        if ((f & 0x20) != 0) w.Write(QuietMode);
        if ((f & 0x40) != 0) w.Write(Flashlight);
        if ((f & 0x100) != 0) w.Write(HealAllies);
        if ((f & 0x80) != 0) (SyncItem ?? new ItemVal()).Write(w);
        w.Flush();
        return ms.ToArray();
    }
}
