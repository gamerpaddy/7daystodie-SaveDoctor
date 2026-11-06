using SaveDoctor.Core;
using SaveDoctor.Formats;

namespace SaveDoctor;

public static partial class SelfTest
{
    static TeRec? RoundTrip(TeRec t, GameData g)
    {
        var bytes = LegacyTe.Encode(t);
        using var r = BinUtil.Reader(bytes);
        var back = TeRec.ReadOne(r, bytes, 0, g);
        return back.Legacy != null && BinUtil.SameBytes(LegacyTe.Encode(back), bytes) && r.Left() == 0 ? back : null;
    }

    static byte[] PoweredBlob(int type, params Action<BinaryWriter>[] tail)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(type); w.Write((ushort)19); w.Write(3); w.Write(70); w.Write(4); w.Write(0UL);
        w.Write(1); w.Write(true); w.Write((byte)3); w.Write((byte)2);
        for (int i = 0; i < 2; i++) { w.Write(1); w.Write(2); w.Write(3); }
        w.Write(5); w.Write(6); w.Write(7); w.Write(0.5f); w.Write(1.5f);
        foreach (var t in tail) t(w);
        w.Flush(); return ms.ToArray();
    }

    static byte[] SignBlob(string name, string text)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        void Common(string n) { w.Str(n); w.Write(0f); w.Write(0f); w.Write(15f); w.Write(0.5f); w.Write(0.5f); w.Write(1f); w.Write(0.5f); w.Write(0.25f); w.Write(1f); w.Write((byte)0); w.Write(0); }
        w.Write(3); w.Write(Guid.NewGuid().ToByteArray()); w.Str(name); w.Write(DateTime.UtcNow.Ticks); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write((byte)0);
        w.Write(1);                                                      // one top-level layer: a group …
        w.Write((byte)0); w.Write((byte)0); w.Write(0f); w.Write(0f); w.Write((byte)0); w.Write(2);
        w.Write((byte)1); w.Str("LiberationSans"); w.Str(text); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f); Common("t");     // … holding a text layer
        w.Write((byte)2); w.Write(6); w.Write(0f); w.Write(0.3f); w.Write(0f); w.Write(0f); w.Write(5f); w.Write((byte)0);           // … and a polygon
        w.Str("p"); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(1f); w.Write(0.2f); w.Write(0.4f); w.Write(0.6f); w.Write(1f); w.Write((byte)0);
        w.Write(1); w.Write((byte)3); w.Write(0f); w.Write(0f); w.Write(3); w.Write(0f); w.Write(1f);                                // with a kaleido warp
        // the group's own name / transform / colour / (no) warps follow its children
        w.Str("g"); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(1f); w.Write(1f); w.Write(1f); w.Write(1f); w.Write(1f); w.Write((byte)0); w.Write(0);
        w.Flush(); return ms.ToArray();
    }

    /// <summary>Formats the real saves do not contain (yet): workstation, forge, collector, power blocks, traps, signs – built in the game's own write order and read back.</summary>
    static void SyntheticTests(GameData g)
    {
        Console.WriteLine("  synthetic formats (no sample data needed):");
        Stack S(string name, int n, int q = 0) { var id = g.ItemIds.TryGetValue(name, out var v) ? v : g.ItemIds.Values.First(); return new Stack { Count = n, Item = new ItemVal { Type = id, Quality = q, Mods = new(), Cosmetics = new() } }; }
        var ws = new WorkstationM { LastTick = 12345, PlayerPlaced = true, IsBurning = true, BurnTimeLeft = 33.5f, MeltTimes = new() { 1f, 2f }, Fuel = { S("resourceWood", 20), new Stack() }, Input = { S("resourceScrapIron", 50), new Stack() }, Tools = { new Stack() }, Output = { S("resourceForgedIron", 3) }, LastInput = { new Stack() } };
        ws.Queue.Add(new RecipeQueueM { Multiplier = 2, IsCrafting = true, TimeLeft = 4.5f, Quality = 3, StartingEntityId = 171, OneItemCraftTime = 2f, HasRecipe = true, RecipeItemType = 70000, RecipeCount = 1, RecipeCraftTime = 3f, RecipeExp = 5, RecipeArea = "workbench", Ingredients = { S("resourceNail", 10) } });
        ws.CraftComplete.Add(new CraftDoneM { CrafterEntityId = 171, Item = S("resourceNail", 4), RecipeName = "resourceNail", Exp = 3, RecipeUsedCount = 2, ItemScrapped = "" });
        var wt = RoundTrip(new TeRec { Type = 12, X = 1, Y = 50, Z = 2, HeapTime = 9, Legacy = ws }, g);
        Check(wt?.Legacy is WorkstationM w2 && w2.Input[0].Count == 50 && w2.Queue.Count == 1 && w2.CraftComplete.Count == 1 && w2.MeltTimes.Count == 2, "workstation tile entity encodes and reads back");
        var fg = new ForgeM { LastTick = 7, Fuel = { S("resourceCoal", 9) }, Input = { S("resourceIronOre", 4), new Stack() }, Mold = S("resourceClayLump", 1), Output = new Stack(), FuelInForgeTicks = 400, MoldedMetalSoFar = 10, MetalInForge = 30, Burning = S("resourceCoal", 1).Item };
        var ft = RoundTrip(new TeRec { Type = 8, X = 4, Y = 60, Z = 5, Legacy = fg }, g);
        Check(ft?.Legacy is ForgeM f2 && f2.MetalInForge == 30 && f2.Fuel[0].Count == 9, "forge tile entity encodes and reads back");
        var col = new CollectorM { LastTimes = { new CollectorTimeM { Key = "water", Time = 99 } }, Fills = { new CollectorFillM { Key = "water", Slot = 0, FillTime = 100, FillTimeLeft = 50 } }, OutOfFuel = { new CollectorFlagM { Key = "water", Value = true } }, IsFull = { new CollectorFlagM { Key = "water" } }, Items = { S("drinkJarRiverWater", 2) }, Mods = { new Stack() }, FuelSlots = { new Stack() }, Catalysts = { new Stack() } };
        var ct = RoundTrip(new TeRec { Type = 3, X = 8, Y = 40, Z = 9, Legacy = col }, g);
        Check(ct?.Legacy is CollectorM c2 && c2.Items[0].Count == 2 && c2.OutOfFuel[0].Value, "collector tile entity encodes and reads back");
        var vm = new VendingM { Locked = true, Owner = new PlatformId { Platform = "EOS", UserId = "abc" }, PasswordHash = "x", Allowed = { new PlatformId { Platform = "Steam", UserId = "76561" } }, RentalEndDay = 12, Rentable = true, NextAutoBuy = 777, Trader = new TraderDataM { TraderId = 5, AvailableMoney = 40, Primary = { new TraderEntryM { Item = S("resourceNail", 100), Markup = -3, AddedByPlayer = true } }, Tiers = { new() { S("resourceNail", 1) } } } };
        var vt = RoundTrip(new TeRec { Type = 7, X = 0, Y = 40, Z = 1, Legacy = vm }, g);
        Check(vt?.Legacy is VendingM v2 && v2.Rentable && v2.NextAutoBuy == 777 && v2.Trader.Primary[0].Markup == -3 && v2.Allowed.Count == 1, "rentable vending machine encodes and reads back");
        var cases = new List<(int type, Action<BinaryWriter>[] tail)>
        {
            (15, new Action<BinaryWriter>[] { w => w.Write((ushort)18) }),
            (16, new Action<BinaryWriter>[] { w => w.Write((ushort)18) }),
            (17, new Action<BinaryWriter>[] { w => w.Write((ushort)18), w => w.Write((ushort)18), w => PlatformId.Write(w, new PlatformId { Platform = "EOS", UserId = "u" }) }),
            (21, new Action<BinaryWriter>[] { w => w.Write((ushort)18), w => w.Write((ushort)18), w => PlatformId.Write(w, null) }),
            (19, new Action<BinaryWriter>[] { w => w.Write((ushort)18), w => w.Write((byte)3), w => PlatformId.Write(w, new PlatformId { Platform = "EOS", UserId = "u" }) }),
            (19, new Action<BinaryWriter>[] { w => w.Write((ushort)18), w => w.Write((byte)0) }),
        };
        foreach (var (type, tail) in cases)
        {
            var bytes = PoweredBlob(type, tail);
            try { using var r = BinUtil.Reader(bytes); var t = TeRec.ReadOne(r, bytes, 0, g); Check(t.Raw.Length == bytes.Length && r.Left() == 0 && t.X == 3 && t.Y == 70, $"{LegacyTe.TypeName(type)} tile entity is walked to its exact end"); }
            catch (Exception ex) { Check(false, $"{LegacyTe.TypeName(type)} walk", ex.Message); }
        }
        var sg = new SignableM { HasText = true, Text = "Hello\nWorld", Author = new PlatformId { Platform = "EOS", UserId = "me" } };
        var sgBytes = TeFeatures.Encode("signable", sg, g)!;
        var sgBack = TeFeatures.Decode(new TeFeat { Hash = GameData.MonoHash("TEFeatureSignable"), Payload = sgBytes }, g);
        Check(sgBack.Editable && sgBack.Data is SignableM sm && sm.Text == sg.Text && sm.Author?.UserId == "me", "sign text feature encodes and reads back");
        var lk = new LockableM { Locked = true, Allowed = { new PlatformId { Platform = "EOS", UserId = "a" }, null }, PasswordHash = "h" };
        Check(TeFeatures.Decode(new TeFeat { Hash = GameData.MonoHash("TEFeatureLockable"), Payload = TeFeatures.Encode("lockable", lk, g)! }, g).Editable, "lock feature with allowed users encodes and reads back");
        try
        {
            var blobs = new List<byte[]> { SignBlob("First", "Hello"), SignBlob("Second", "Zombies ahead") };
            var store = Signs.BuildStore(blobs);
            var back = Signs.ReadStore(store);
            var m = Signs.ReadBlob(back[1]);
            Check(back.Count == 2 && m.Name == "Second" && m.Texts.Contains("Zombies ahead") && m.LayerCount == 3 && Signs.ToSvg(m).StartsWith("<svg"), "drawn sign store (binary) builds, validates and reads back", $"{m.LayerCount} layers");
            store[40] ^= 0xFF;
            bool rejected = false; try { Signs.ReadStore(store); } catch (ParseFail) { rejected = true; }
            Check(rejected, "a damaged sign store is rejected by the checksum");
        }
        catch (Exception ex) { Check(false, "drawn sign store", ex.Message); }
    }
}
