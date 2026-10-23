using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class PlatformId
{
    public int Flag { get; set; } = 1;
    public string Platform { get; set; } = "";
    public string UserId { get; set; } = "";

    public static PlatformId? Read(BinaryReader r)
    {
        if (!r.ReadBoolean()) return null;
        var p = new PlatformId { Flag = r.ReadByte(), Platform = r.Str(), UserId = r.Str() };
        return p;
    }
    public static void Write(BinaryWriter w, PlatformId? p)
    {
        if (p == null) { w.Write((byte)0); return; }
        w.Write((byte)1); w.Write((byte)1); w.Str(p.Platform); w.Str(p.UserId);
    }
}

public sealed class WaypointM
{
    public int[] Pos { get; set; } = new int[3];
    public string Icon { get; set; } = "";
    public bool HasName { get; set; }
    public string Name { get; set; } = "";
    public PlatformId? NameAuthor { get; set; }
    public bool Tracked { get; set; }
    public bool HiddenOnCompass { get; set; }
    public PlatformId? Owner { get; set; }
    public int LastKnownEntityId { get; set; } = -1;
    public bool IsAuto { get; set; }
    public bool UsesLocalization { get; set; }
    public int InviterEntityId { get; set; } = -1;
    public bool HiddenOnMap { get; set; }
    public int EntityType { get; set; }

    public static WaypointM Read(BinaryReader r, int version)
    {
        var w = new WaypointM { Pos = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() }, Icon = r.Str() };
        w.HasName = r.ReadBoolean();
        if (w.HasName) { w.Name = r.Str(); w.NameAuthor = PlatformId.Read(r); }
        w.Tracked = r.ReadBoolean();
        if (version > 2) w.HiddenOnCompass = r.ReadBoolean();
        if (version > 1) { w.Owner = PlatformId.Read(r); w.LastKnownEntityId = r.ReadInt32(); }
        if (version > 3) { w.IsAuto = r.ReadBoolean(); w.UsesLocalization = r.ReadBoolean(); }
        if (version > 4) w.InviterEntityId = r.ReadInt32();
        if (version > 5) w.HiddenOnMap = r.ReadBoolean();
        if (version > 6) w.EntityType = r.ReadInt32();
        else if (w.LastKnownEntityId > -1) w.EntityType = 1;
        return w;
    }
    public void Write(BinaryWriter w)
    {
        w.Write(Pos[0]); w.Write(Pos[1]); w.Write(Pos[2]); w.Str(Icon);
        if (!HasName) w.Write((byte)0);
        else { w.Write((byte)1); w.Str(Name); PlatformId.Write(w, NameAuthor); }
        w.Write(Tracked); w.Write(HiddenOnCompass);
        PlatformId.Write(w, Owner); w.Write(LastKnownEntityId);
        w.Write(IsAuto); w.Write(UsesLocalization); w.Write(InviterEntityId); w.Write(HiddenOnMap); w.Write(EntityType);
    }
}

public sealed class QuestEntryM
{
    public string Id { get; set; } = "";
    public int QuestVersion { get; set; }
    public int State { get; set; }
    public byte[] Rest { get; set; } = Array.Empty<byte>();     // everything after the id string (verbatim)
}

public sealed class TraderPoiM
{
    public int Key { get; set; }
    public List<float[]> Points { get; set; } = new();
}

public sealed class QuestTraderM
{
    public float[] Poi { get; set; } = new float[2];
    public List<TraderPoiM> CompletedByTier { get; set; } = new();
    public List<float[]> SentTo { get; set; } = new();
    public int ResetDay { get; set; }
}

public sealed class QuestJournalM
{
    public int Version { get; set; } = 6;
    public List<float[]> TraderPois { get; set; } = new();
    public List<TraderPoiM> TradersByFaction { get; set; } = new();
    public List<QuestEntryM> Quests { get; set; } = new();
    public List<QuestTraderM> TraderData { get; set; } = new();

    public static QuestJournalM Read(BinaryReader r)
    {
        var q = new QuestJournalM();
        long at = r.Pos();
        q.Version = r.ReadByte();
        if (q.Version < 5) throw new ParseFail($"Quest journal version {q.Version} is too old for the editor", at);
        int n = r.ReadByte();
        for (int i = 0; i < n; i++) q.TraderPois.Add(new[] { r.ReadSingle(), r.ReadSingle() });
        n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            var t = new TraderPoiM { Key = r.ReadInt32() };
            int m = r.ReadInt32();
            if (m < 0 || m > 100000) throw new ParseFail("Implausible trader list size " + m, r.Pos() - 4);
            for (int j = 0; j < m; j++) t.Points.Add(new[] { r.ReadSingle(), r.ReadSingle() });
            q.TradersByFaction.Add(t);
        }
        int quests = r.ReadInt16();
        if (quests < 0) throw new ParseFail("Negative quest count", r.Pos() - 2);
        for (int i = 0; i < quests; i++)
        {
            long mstart = r.Pos();
            int size = r.ReadUInt16();
            if (size < 3 || mstart + size > r.Len()) throw new ParseFail($"Quest #{i} has an impossible size ({size})", mstart);
            var qe = new QuestEntryM();
            long bodyStart = r.Pos();
            qe.Id = r.Str();
            long restLen = mstart + size - r.Pos();
            if (restLen < 0) throw new ParseFail($"Quest #{i} id string overruns its record", mstart);
            qe.Rest = r.ReadBytes((int)restLen);
            if (qe.Rest.Length >= 1) qe.QuestVersion = qe.Rest[0];
            if (qe.Rest.Length >= 3) qe.State = qe.Rest[2];
            q.Quests.Add(qe);
        }
        if (q.Version > 3)
        {
            n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                var t = new QuestTraderM { Poi = new[] { r.ReadSingle(), r.ReadSingle() } };
                int k = r.ReadByte();
                for (int j = 0; j < k; j++)
                {
                    var tp = new TraderPoiM { Key = r.ReadByte() };
                    int m = r.ReadInt32();
                    if (m < 0 || m > 100000) throw new ParseFail("Implausible POI list size " + m, r.Pos() - 4);
                    for (int x = 0; x < m; x++) tp.Points.Add(new[] { r.ReadSingle(), r.ReadSingle() });
                    t.CompletedByTier.Add(tp);
                }
                k = r.ReadByte();
                for (int j = 0; j < k; j++) t.SentTo.Add(new[] { r.ReadSingle(), r.ReadSingle() });
                t.ResetDay = r.ReadInt32();
                q.TraderData.Add(t);
            }
        }
        return q;
    }

    public void Write(BinaryWriter w)
    {
        w.Write((byte)Version);
        w.Write((byte)TraderPois.Count);
        foreach (var p in TraderPois) { w.Write(p[0]); w.Write(p[1]); }
        w.Write((byte)TradersByFaction.Count);
        foreach (var t in TradersByFaction)
        {
            w.Write(t.Key); w.Write(t.Points.Count);
            foreach (var p in t.Points) { w.Write(p[0]); w.Write(p[1]); }
        }
        w.Write((short)Quests.Count);
        foreach (var q in Quests)
        {
            using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms, BinUtil.Utf8, true);
            bw.Str(q.Id); bw.Write(q.Rest); bw.Flush();
            int size = (int)ms.Length + 2;
            if (size > 65535) throw new InvalidOperationException("Quest record too large");
            w.Write((ushort)size); w.Write(ms.ToArray());
        }
        if (Version > 3)
        {
            w.Write((byte)TraderData.Count);
            foreach (var t in TraderData)
            {
                w.Write(t.Poi[0]); w.Write(t.Poi[1]);
                w.Write((byte)t.CompletedByTier.Count);
                foreach (var tp in t.CompletedByTier)
                {
                    w.Write((byte)tp.Key); w.Write(tp.Points.Count);
                    foreach (var p in tp.Points) { w.Write(p[0]); w.Write(p[1]); }
                }
                w.Write((byte)t.SentTo.Count);
                foreach (var p in t.SentTo) { w.Write(p[0]); w.Write(p[1]); }
                w.Write(t.ResetDay);
            }
        }
    }
}

public sealed class ChallengeObjM
{
    public int Type { get; set; }
    public int Current { get; set; }
    public float? Time { get; set; }
}

public sealed class ChallengeM
{
    public int FileVersion { get; set; } = 2;
    public string Name { get; set; } = "";
    public int State { get; set; }                  // 0 Active 1 Completed 2 Redeemed (approx.)
    public bool AutoCompleted { get; set; }
    public int ObjVersion { get; set; }
    public List<ChallengeObjM> Objectives { get; set; } = new();
}

public sealed class ChallengeGroupM { public string Name { get; set; } = ""; public int LastUpdateDay { get; set; } }

public sealed class ChallengeJournalM
{
    public int Version { get; set; } = 2;
    public List<ChallengeM> Challenges { get; set; } = new();
    public List<ChallengeGroupM> Groups { get; set; } = new();
    public string Tracked { get; set; } = "";

    public static ChallengeJournalM Read(BinaryReader r)
    {
        var j = new ChallengeJournalM { Version = r.ReadByte() };
        if (j.Version == 1) return j;
        int n = r.ReadInt32();
        if (n < 0 || n > 100000) throw new ParseFail("Implausible challenge count " + n, r.Pos() - 4);
        for (int i = 0; i < n; i++)
        {
            var c = new ChallengeM { FileVersion = r.ReadByte(), Name = r.Str(), State = r.ReadByte() };
            if (c.FileVersion >= 2) c.AutoCompleted = r.ReadBoolean();
            c.ObjVersion = r.ReadByte();
            int m = r.ReadInt32();
            if (m < 0 || m > 1000) throw new ParseFail("Implausible objective count " + m, r.Pos() - 4);
            for (int k = 0; k < m; k++)
            {
                long at = r.Pos();
                var o = new ChallengeObjM { Type = r.ReadByte() };
                if (o.Type == 0 || o.Type > 27) throw new ParseFail($"Unknown challenge objective type {o.Type}", at);
                o.Current = r.ReadInt32();
                if (o.Type == 25) o.Time = r.ReadSingle();
                c.Objectives.Add(o);
            }
            j.Challenges.Add(c);
        }
        n = r.ReadInt32();
        if (n < 0 || n > 100000) throw new ParseFail("Implausible challenge group count " + n, r.Pos() - 4);
        for (int i = 0; i < n; i++) j.Groups.Add(new ChallengeGroupM { Name = r.Str(), LastUpdateDay = r.ReadInt32() });
        j.Tracked = r.Str();
        return j;
    }

    public void Write(BinaryWriter w)
    {
        w.Write((byte)2);
        w.Write(Challenges.Count);
        foreach (var c in Challenges)
        {
            w.Write((byte)2);
            w.Str(c.Name); w.Write((byte)c.State); w.Write(c.AutoCompleted);
            w.Write((byte)1);
            w.Write(c.Objectives.Count);
            foreach (var o in c.Objectives)
            {
                w.Write((byte)o.Type); w.Write(o.Current);
                if (o.Type == 25) w.Write(o.Time ?? 0f);
            }
        }
        w.Write(Groups.Count);
        foreach (var g in Groups) { w.Str(g.Name); w.Write(g.LastUpdateDay); }
        w.Str(Tracked);
    }
}

public sealed class RecipeQueueM
{
    public int Multiplier { get; set; }
    public bool IsCrafting { get; set; }
    public float TimeLeft { get; set; }
    public ItemVal? RepairItem { get; set; }
    public int AmountToRepair { get; set; }
    public bool HasRepair { get; set; }
    public int Quality { get; set; }
    public int StartingEntityId { get; set; }
    public float OneItemCraftTime { get; set; }
    public bool HasRecipe { get; set; }
    public int RecipeItemType { get; set; }
    public int RecipeCount { get; set; }
    public bool RecipeIsScrap { get; set; }
    public float RecipeCraftTime { get; set; }
    public int RecipeExp { get; set; }
    public string RecipeArea { get; set; } = "";
    public List<Stack> Ingredients { get; set; } = new();
}

public sealed class OwnedEntityM
{
    public int Id { get; set; }
    public int ClassId { get; set; }
    public int Flags { get; set; }
    public int[]? LastPos { get; set; }
}

public sealed class SkillM
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int Cost { get; set; }
}

/// <summary>Progression blob: player level, XP and every skill / perk level.</summary>
public sealed class ProgressionM
{
    public int Version { get; set; } = 3;
    public int Level { get; set; }
    public int ExpToNextLevel { get; set; }
    public int SkillPoints { get; set; }
    public List<SkillM> Skills { get; set; } = new();
    public int ExpDeficit { get; set; }

    public static ProgressionM? TryParse(byte[] blob)
    {
        if (blob.Length < 12) return null;
        try
        {
            using var r = BinUtil.Reader(blob);
            var p = new ProgressionM { Version = r.ReadByte(), Level = r.ReadUInt16(), ExpToNextLevel = r.ReadInt32(), SkillPoints = r.ReadUInt16() };
            int n = r.ReadInt32();
            if (n < 0 || n > 10000) return null;
            for (int i = 0; i < n; i++)
            {
                r.ReadByte();
                p.Skills.Add(new SkillM { Name = r.Str(), Level = r.ReadByte(), Cost = r.ReadInt32() });
            }
            if (p.Version > 2) p.ExpDeficit = r.ReadInt32();
            if (r.Left() != 0) return null;
            return p;
        }
        catch { return null; }
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, BinUtil.Utf8, true);
        w.Write((byte)3); w.Write((ushort)Level); w.Write(ExpToNextLevel); w.Write((ushort)SkillPoints);
        w.Write(Skills.Count);
        foreach (var s in Skills) { w.Write((byte)1); w.Str(s.Name); w.Write((byte)s.Level); w.Write(s.Cost); }
        w.Write(ExpDeficit);
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>Complete model of a Player/*.ttp file (format version 62).</summary>
public sealed class PlayerFile
{
    public int Version { get; set; } = 62;
    public Ecd Ecd { get; set; } = new();
    public InventoryBlob Inventory { get; set; } = new();
    public BagBlob Bag { get; set; } = new();
    public List<Stack> DragAndDrop { get; set; } = new();
    public List<string> AlreadyCrafted { get; set; } = new();
    public List<int[]> SpawnPoints { get; set; } = new();
    public long SelectedSpawnPointKey { get; set; }
    public bool Flag1 { get; set; } = true;
    public int Short1 { get; set; }
    public bool Loaded { get; set; }
    public int[] LastSpawnPos { get; set; } = new int[3];
    public float LastSpawnHeading { get; set; }
    public int Id { get; set; }
    public int PlayerKills { get; set; }
    public int ZombieKills { get; set; }
    public int Deaths { get; set; }
    public int Score { get; set; }
    public EquipmentBlob Equipment { get; set; } = new();
    public List<string> UnlockedRecipes { get; set; } = new();
    public int UShort1 { get; set; } = 1;
    public int[] MarkerPosition { get; set; } = new int[3];
    public bool MarkerHidden { get; set; }
    public bool CrouchLocked { get; set; }
    public int CraftingVersion { get; set; } = 2;
    public List<RecipeQueueM> CraftQueue { get; set; } = new();
    public List<string> FavoriteRecipes { get; set; } = new();
    public uint TotalItemsCrafted { get; set; }
    public float DistanceWalked { get; set; }
    public float LongestLife { get; set; }
    public ulong GameStageBornAtWorldTime { get; set; }
    public List<WaypointM> Waypoints { get; set; } = new();
    public QuestJournalM Quests { get; set; } = new();
    public int DeathUpdateTime { get; set; }
    public float CurrentLife { get; set; }
    public bool Dead { get; set; }
    public int Marker88 { get; set; } = 88;
    public bool Modded { get; set; }
    public ChallengeJournalM Challenges { get; set; } = new();
    public int[] RentedVMPosition { get; set; } = new int[3];
    public int RentalEndDay { get; set; }
    public byte[] Progression { get; set; } = Array.Empty<byte>();
    public ProgressionM? Skills { get; set; }
    public byte[] Buffs { get; set; } = Array.Empty<byte>();
    public byte[] Stealth { get; set; } = Array.Empty<byte>();
    public List<int> FavoriteCreative { get; set; } = new();
    public List<string> FavoriteShapes { get; set; } = new();
    public List<OwnedEntityM> Owned { get; set; } = new();
    public float TotalTimePlayed { get; set; }
    public long TrailingBytes { get; set; }

    static List<string> Strings(BinaryReader r)
    {
        int n = r.ReadUInt16();
        var l = new List<string>(n);
        for (int i = 0; i < n; i++) l.Add(r.Str());
        return l;
    }
    static void WriteStrings(BinaryWriter w, List<string> l) { w.Write((ushort)l.Count); foreach (var s in l) w.Str(s); }

    public static PlayerFile Parse(byte[] data, GameData g)
    {
        using var r = BinUtil.Reader(data);
        r.Need(6, "header");
        if (r.ReadByte() != 't' || r.ReadByte() != 't' || r.ReadByte() != 'p' || r.ReadByte() != 0)
            throw new ParseFail("Bad magic – this is not a player file ('ttp\\0' expected).", 0);
        var p = new PlayerFile { Version = r.ReadByte() };
        if (p.Version != 62) throw new ParseFail($"Player file version {p.Version} – the editor only understands the current version 62.", 4);
        p.Ecd = Ecd.Read(r, g, true);
        p.Inventory = InventoryBlob.Read(r.Blob("inventory"), g);
        p.Bag = BagBlob.Read(r.Blob("backpack"), g);
        p.DragAndDrop = Stack.ReadList(r, g);
        int n = r.ReadUInt16();
        for (int i = 0; i < n; i++) p.AlreadyCrafted.Add(r.Str());
        n = r.ReadByte();
        for (int i = 0; i < n; i++) p.SpawnPoints.Add(new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() });
        p.SelectedSpawnPointKey = r.ReadInt64();
        p.Flag1 = r.ReadBoolean();
        p.Short1 = r.ReadInt16();
        p.Loaded = r.ReadBoolean();
        p.LastSpawnPos = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
        p.LastSpawnHeading = r.ReadSingle();
        p.Id = r.ReadInt32();
        p.PlayerKills = r.ReadInt32(); p.ZombieKills = r.ReadInt32(); p.Deaths = r.ReadInt32(); p.Score = r.ReadInt32();
        p.Equipment = EquipmentBlob.Read(r.Blob("equipment"), g);
        p.UnlockedRecipes = Strings(r);
        p.UShort1 = r.ReadUInt16();
        p.MarkerPosition = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
        p.MarkerHidden = r.ReadBoolean();
        p.CrouchLocked = r.ReadBoolean();
        // crafting data
        p.CraftingVersion = r.ReadUInt16();
        n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            long at = r.Pos();
            int rv = r.ReadUInt16();
            if (rv < 2) throw new ParseFail("Legacy crafting queue entry", at);
            var q = new RecipeQueueM { Multiplier = r.ReadInt16(), IsCrafting = r.ReadBoolean(), TimeLeft = r.ReadSingle() };
            q.HasRepair = r.ReadBoolean();
            if (q.HasRepair) { q.RepairItem = ItemVal.ReadOrNull(r, g); q.AmountToRepair = r.ReadUInt16(); }
            q.Quality = r.ReadByte(); q.StartingEntityId = r.ReadInt32(); q.OneItemCraftTime = r.ReadSingle();
            q.HasRecipe = r.ReadBoolean();
            if (q.HasRecipe)
            {
                r.ReadUInt16();
                q.RecipeItemType = r.ReadInt32(); q.RecipeCount = r.ReadInt32(); q.RecipeIsScrap = r.ReadBoolean();
                q.RecipeCraftTime = r.ReadSingle(); q.RecipeExp = r.ReadInt32(); q.RecipeArea = r.Str();
                int ing = r.ReadInt32();
                if (ing < 0 || ing > 1000) throw new ParseFail("Implausible ingredient count " + ing, r.Pos() - 4);
                for (int k = 0; k < ing; k++) q.Ingredients.Add(Stack.Read(r, g));
            }
            p.CraftQueue.Add(q);
        }
        p.FavoriteRecipes = Strings(r);
        p.TotalItemsCrafted = r.ReadUInt32();
        p.DistanceWalked = r.ReadSingle(); p.LongestLife = r.ReadSingle();
        p.GameStageBornAtWorldTime = r.ReadUInt64();
        // waypoints
        int wv = r.ReadByte();
        n = r.ReadUInt16();
        for (int i = 0; i < n; i++) p.Waypoints.Add(WaypointM.Read(r, wv));
        p.Quests = QuestJournalM.Read(r);
        p.DeathUpdateTime = r.ReadInt32();
        p.CurrentLife = r.ReadSingle();
        p.Dead = r.ReadBoolean();
        p.Marker88 = r.ReadByte();
        p.Modded = r.ReadBoolean();
        p.Challenges = ChallengeJournalM.Read(r);
        p.RentedVMPosition = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
        p.RentalEndDay = r.ReadInt32();
        p.Progression = r.Blob("progression");
        p.Skills = ProgressionM.TryParse(p.Progression);
        if (p.Skills != null && !BinUtil.SameBytes(p.Skills.Write(), p.Progression)) p.Skills = null;   // only trust the parse when it round-trips
        p.Buffs = r.Blob("buffs");
        p.Stealth = r.Blob("stealth");
        n = r.ReadUInt16();
        for (int i = 0; i < n; i++) p.FavoriteCreative.Add(r.ReadUInt16());
        p.FavoriteShapes = Strings(r);
        n = r.ReadUInt16();
        for (int i = 0; i < n; i++)
        {
            var o = new OwnedEntityM { Id = r.ReadInt32(), ClassId = r.ReadInt32(), Flags = r.ReadUInt16() };
            if ((o.Flags & 1) > 0) o.LastPos = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
            p.Owned.Add(o);
        }
        p.TotalTimePlayed = r.ReadSingle();
        p.TrailingBytes = r.Left();
        return p;
    }

    public byte[] Build(GameData g)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, BinUtil.Utf8, true);
        w.Write((byte)'t'); w.Write((byte)'t'); w.Write((byte)'p'); w.Write((byte)0); w.Write((byte)62);
        Ecd.Write(w, g);
        w.WriteBlob(Inventory.Write());
        w.WriteBlob(Bag.Write());
        Stack.WriteList(w, DragAndDrop);
        w.Write((ushort)AlreadyCrafted.Count); foreach (var s in AlreadyCrafted) w.Str(s);
        w.Write((byte)SpawnPoints.Count);
        foreach (var s in SpawnPoints) { w.Write(s[0]); w.Write(s[1]); w.Write(s[2]); }
        w.Write(SelectedSpawnPointKey);
        w.Write(Flag1); w.Write((short)Short1); w.Write(Loaded);
        w.Write(LastSpawnPos[0]); w.Write(LastSpawnPos[1]); w.Write(LastSpawnPos[2]); w.Write(LastSpawnHeading);
        w.Write(Id);
        w.Write(PlayerKills); w.Write(ZombieKills); w.Write(Deaths); w.Write(Score);
        w.WriteBlob(Equipment.Write());
        WriteStrings(w, UnlockedRecipes);
        w.Write((ushort)UShort1);
        w.Write(MarkerPosition[0]); w.Write(MarkerPosition[1]); w.Write(MarkerPosition[2]);
        w.Write(MarkerHidden); w.Write(CrouchLocked);
        w.Write((ushort)2);
        w.Write((byte)CraftQueue.Count);
        foreach (var q in CraftQueue)
        {
            w.Write((ushort)2); w.Write((short)q.Multiplier); w.Write(q.IsCrafting); w.Write(q.TimeLeft);
            w.Write(q.HasRepair);
            if (q.HasRepair) { if (q.RepairItem == null) w.Write((byte)0); else q.RepairItem.Write(w); w.Write((ushort)q.AmountToRepair); }
            w.Write((byte)q.Quality); w.Write(q.StartingEntityId); w.Write(q.OneItemCraftTime);
            w.Write(q.HasRecipe);
            if (q.HasRecipe)
            {
                w.Write((ushort)1);
                w.Write(q.RecipeItemType); w.Write(q.RecipeCount); w.Write(q.RecipeIsScrap); w.Write(q.RecipeCraftTime); w.Write(q.RecipeExp); w.Str(q.RecipeArea);
                w.Write(q.Ingredients.Count);
                foreach (var s in q.Ingredients) s.Write(w);
            }
        }
        WriteStrings(w, FavoriteRecipes);
        w.Write(TotalItemsCrafted); w.Write(DistanceWalked); w.Write(LongestLife); w.Write(GameStageBornAtWorldTime);
        w.Write((byte)7); w.Write((ushort)Waypoints.Count);
        foreach (var wp in Waypoints) wp.Write(w);
        Quests.Write(w);
        w.Write(DeathUpdateTime); w.Write(CurrentLife); w.Write(Dead); w.Write((byte)Marker88); w.Write(Modded);
        Challenges.Write(w);
        w.Write(RentedVMPosition[0]); w.Write(RentedVMPosition[1]); w.Write(RentedVMPosition[2]);
        w.Write(RentalEndDay);
        w.WriteBlob(Skills != null ? Skills.Write() : Progression); w.WriteBlob(Buffs); w.WriteBlob(Stealth);
        w.Write((ushort)FavoriteCreative.Count); foreach (var f in FavoriteCreative) w.Write((ushort)f);
        WriteStrings(w, FavoriteShapes);
        w.Write((ushort)Owned.Count);
        foreach (var o in Owned)
        {
            w.Write(o.Id); w.Write(o.ClassId);
            int fl = o.LastPos != null ? (o.Flags | 1) : (o.Flags & ~1);
            w.Write((ushort)fl);
            if ((fl & 1) > 0) { w.Write(o.LastPos![0]); w.Write(o.LastPos[1]); w.Write(o.LastPos[2]); }
        }
        w.Write(TotalTimePlayed);
        w.Flush();
        return ms.ToArray();
    }
}
