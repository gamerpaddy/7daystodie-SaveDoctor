using SaveDoctor.Core;
using SaveDoctor.Formats;

namespace SaveDoctor;

/// <summary>Round-trip checks of the parsers against a real save (read-only, never writes).</summary>
public static class SelfTest
{
    static int fails;
    static void Check(bool ok, string what, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail != null ? " – " + detail : "")}");
        if (!ok) fails++;
    }

    public static int Run(string[] a)
    {
        var ctx = new SaveCtx(a[0]);
        Console.WriteLine("Self-test against " + ctx.Root);
        var g = ctx.Game;
        foreach (var rel in new[] { "power.dat", "vehicles.dat", "drones.dat" })
        {
            if (!File.Exists(ctx.Abs(rel))) continue;
            var data = ctx.ReadAll(rel);
            try
            {
                byte[] back;
                string what;
                if (rel == "power.dat") { var p = PowerFile.Parse(data, g); back = p.Build(); what = p.All().Count() + " power nodes"; }
                else
                {
                    var e = EntityListFile.Parse(data, g); back = e.Build(g); what = string.Join(", ", e.Entities.Select(x => g.EntityClassName(x.EntityClass)));
                    if (rel == "vehicles.dat")
                        foreach (var x in e.Entities)
                        {
                            var vd = VehicleDataM.TryParse(x.EntityData, g, out var verr);
                            if (vd == null) Console.WriteLine("      raw: " + x.EntityData.Length + " bytes: " + BinUtil.Hex(x.EntityData, 150));
                            Check(vd != null && BinUtil.SameBytes(vd.Write(), x.EntityData), $"vehicle detail roundtrip {g.EntityClassName(x.EntityClass)} #{x.Id}", vd == null ? verr : $"owner {vd.Owner?.UserId}, locked={vd.Locked}, bag {(vd.Bag?.Items.Count(i => i.Count > 0) ?? 0)} stacks, item {(vd.Item == null ? "-" : g.NameOf(vd.Item.Item.Type))}");
                        }
                }
                long d = BinUtil.FirstDiff(data, back);
                Check(d < 0, $"{rel} roundtrip ({what})", d < 0 ? $"{data.Length} bytes identical" : $"first diff at 0x{d:X} (orig {data.Length}, rebuilt {back.Length})");
            }
            catch (Exception ex) { Check(false, rel + " parse", ex.Message); }
        }
        {
            var grid = new Grid { Width = 8, Height = 1 };
            for (int i = 0; i < 8; i++) grid.Items.Add(new Stack { Count = i == 2 ? 5 : 0, Item = i == 2 ? new ItemVal { Type = 65536 + 20, Mods = new(), Cosmetics = new(), Quality = 3 } : new ItemVal() });
            var dd = new DroneDataM { Parsed = true, SyncFlags = 49515, Owner = new PlatformId { Platform = "EOS", UserId = "abc" }, Item = new ItemVal(), SyncOwner = new PlatformId { Platform = "EOS", UserId = "abc" }, Health = 77, Order = 1, SentryPos = new[] { 1f, 2f, 3f }, State = 3, UserRequestedHeal = true, Bag = grid, LockOwner = null, QuietMode = true };
            var bytes = dd.Write();
            var back = DroneDataM.TryParse(bytes, g, out var derr);
            Check(back != null && back.Parsed && BinUtil.SameBytes(back.Write(), bytes) && back.Bag!.Items[2].Count == 5 && back.Health == 77, "drone data roundtrip (synthetic, with cargo)", derr);
        }
        Console.WriteLine($"  game data: {g.BlockNames.Count} blocks, {g.ItemNames.Count} items, {g.ModifierItemNames.Count} modifiers, {g.EntityClassNames.Count} entity classes, {g.QuestIds.Count} quests");

        foreach (var rel in ctx.ListFilesRel().Where(r => r.StartsWith("Player/") && r.Contains(".ttp") && !r.EndsWith(".meta")))
        {
            var data = ctx.ReadAll(rel);
            try
            {
                var p = PlayerFile.Parse(data, g);
                var back = p.Build(g);
                long d = BinUtil.FirstDiff(data, back);
                Check(d < 0, $"player roundtrip {rel}", d < 0 ? $"{data.Length} bytes identical" : $"first diff at 0x{d:X} (orig {data.Length}, rebuilt {back.Length})");
                Check(p.Ecd.EntityClass == GameData.MonoHash("playerMale") || p.Ecd.EntityClass == GameData.MonoHash("playerFemale"),
                    "entity class hash matches playerMale/Female", $"class id {p.Ecd.EntityClass}, name '{g.EntityClassName(p.Ecd.EntityClass)}'");
                Console.WriteLine($"      inventory {p.Inventory.Grid.Items.Count} slots, bag {p.Bag.Grid.Items.Count}, equipment {p.Equipment.Grid.Items.Count}, quests {p.Quests.Quests.Count}, challenges {p.Challenges.Challenges.Count}, waypoints {p.Waypoints.Count}, recipes {p.UnlockedRecipes.Count}");
                var inv = p.Inventory.Grid.Items.Where(s => s.Count > 0).Select(s => $"{g.NameOf(s.Item.Type)}x{s.Count}").Take(8);
                Console.WriteLine("      toolbelt: " + string.Join(", ", inv));
            }
            catch (Exception ex) { Check(false, "player parse " + rel, ex.Message); }
        }

        foreach (var rel in ctx.ListFilesRel().Where(r => r.StartsWith("main.ttw")))
        {
            var data = ctx.ReadAll(rel);
            try
            {
                var w = WorldFile.Parse(data);
                var back = w.ToBytes();
                long d = BinUtil.FirstDiff(data, back);
                Check(d < 0, $"world roundtrip {rel}", d < 0 ? $"{data.Length} bytes identical" : $"first diff at 0x{d:X}");
            }
            catch (Exception ex) { Check(false, "world parse " + rel, ex.Message); }
        }

        foreach (var rel in ctx.ListFilesRel().Where(r => r.EndsWith(".nim")))
        {
            var data = ctx.ReadAll(rel);
            var n = NameIdMap.Parse(data);
            var back = n.Build();
            Check(BinUtil.SameBytes(data, back), $"nim roundtrip {rel}", $"{n.Entries.Count} entries");
        }

        foreach (var rel in ctx.ListFilesRel().Where(r => r.EndsWith(".7rg")))
        {
            RegionIO.TryParseName(rel, out int rx, out int rz);
            var data = ctx.ReadAll(rel);
            var sc = RegionIO.Scan(data, rx, rz, true, true, g);
            Check(sc.Bad == 0, $"region scan {rel}", $"{sc.Slots.Count} chunks, versions {string.Join(",", sc.Slots.Select(s => s.Version).Distinct())}, bad {sc.Bad}");
            var rebuilt = RegionIO.Build(sc.Slots.Where(s => s.Blob != null).Select(s => (s.Index, s.Blob!, s.Timestamp)));
            var sc2 = RegionIO.Scan(rebuilt, rx, rz, true, false, g);
            bool same = sc.Slots.Count == sc2.Slots.Count && sc.Slots.Zip(sc2.Slots).All(z => z.First.Index == z.Second.Index && BinUtil.SameBytes(z.First.Blob!, z.Second.Blob!) && z.First.Timestamp == z.Second.Timestamp);
            Check(same && sc2.Bad == 0, $"region rebuild keeps every chunk {rel}", $"{BinUtil.Size(data.Length)} → {BinUtil.Size(rebuilt.Length)}");
            var s0 = sc.Slots.FirstOrDefault(s => s.Chunk is { Ok: true, Census: not null } && s.Chunk.Census.Count > 1);
            if (s0 != null)
            {
                var census = s0.Chunk!.Census!;
                int from = census.OrderByDescending(kv => kv.Value).First().Key;
                int to = from == 1 ? 2 : 1;
                var nb = ChunkCodec.ReplaceBlocks(s0.Blob!, s0.Chunk, id => id == from ? to : null, out int changed);
                var ci = nb == null ? null : ChunkCodec.Inspect(nb, true);
                bool ok = ci is { Ok: true } && ci.Census![to] == census.GetValueOrDefault(to) + census[from] && !ci.Census.ContainsKey(from) && ci.X == s0.ChunkX && ci.Z == s0.ChunkZ;
                Check(ok, $"block replace in chunk ({s0.ChunkX},{s0.ChunkZ}) id {from}→{to}", $"{changed} blocks changed");
                var none = ChunkCodec.ReplaceBlocks(s0.Blob!, s0.Chunk, id => null, out int ch0);
                Check(none == null && ch0 == 0, "no-op replace leaves chunk untouched");
            }
            // chunk tail diagnostics
            var deep = sc.Slots.Count(s => s.Deep);
            Console.WriteLine($"      deep-verified through entity header: {deep}/{sc.Slots.Count}; fully verified to the last byte: {sc.Slots.Count(s => s.Full)}; with entities: {sc.Slots.Count(s => s.Entities > 0)}; with tile entities: {sc.Slots.Count(s => s.TileEntities > 0)}");
            foreach (var n in sc.Slots.Where(s => s.TailNote != null).GroupBy(s => s.TailNote).Take(6)) Console.WriteLine($"      partial: {n.Count()} chunk(s) – {n.Key}");

        }

        foreach (var rel in ctx.ListFilesRel().Where(r => r.EndsWith(".7rm")))
        {
            var raw = MapRegionHandler.Decode(ctx.ReadAll(rel), out var err);
            Check(raw != null && raw.Length == MapRegionHandler.Expected, "map decode " + rel, err);
        }

        Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
