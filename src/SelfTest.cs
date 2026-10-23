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
            {
                // full chunk model: parse → encode must give the identical inflated bytes
                int n = 0, same2 = 0, tail = 0, hmOk = 0, tes = 0, storages = 0, storageOk = 0, comp = 0;
                var feats = new Dictionary<int, int>();
                string? firstBad = null;
                var notes = new Dictionary<string, int>();
                foreach (var s in sc.Slots.Where(s => s.Blob != null && s.Chunk is { Ok: true } && s.Chunk.Version == 47))
                {
                    n++;
                    try
                    {
                        var raw = BinUtil.Inflate(s.Blob!, 8, s.Blob!.Length - 8, out _, out _);
                        var doc = ChunkDoc.Parse(s.Blob!, g);
                        var enc = doc.EncodeInflated();
                        if (BinUtil.SameBytes(raw, enc)) same2++; else firstBad ??= $"chunk {s.ChunkX},{s.ChunkZ} first diff at {BinUtil.FirstDiff(raw, enc)}";
                        if (doc.TailParsed) tail++; else notes[doc.TailNote ?? "?"] = notes.GetValueOrDefault(doc.TailNote ?? "?") + 1;
                        var saved = (byte[])doc.Mid.Clone();
                        doc.RecalcHeights();
                        bool hm = true; for (int i = 0; i < 256; i++) if (saved[i] != doc.Mid[i]) { hm = false; break; }
                        if (hm) hmOk++;
                        foreach (var t in doc.Tes)
                        {
                            tes++;
                            if (!t.Composite) continue;
                            comp++;
                            if (!BinUtil.SameBytes(t.Raw, (new TeRec { Head = t.Head, BodyPre = t.BodyPre, Feats = t.Feats, Dirty = true }).Encode())) firstBad ??= "TE re-encode differs";
                            foreach (var f in t.Feats!)
                            {
                                feats[f.Hash] = feats.GetValueOrDefault(f.Hash) + 1;
                                if (f.Hash == StorageData.StorageHash)
                                {
                                    storages++;
                                    var sd = StorageData.TryRead(f.Payload, g);
                                    if (sd != null && BinUtil.SameBytes(sd.Write(), f.Payload)) storageOk++;
                                }
                            }
                        }
                    }
                    catch (Exception ex) { firstBad ??= $"chunk {s.ChunkX},{s.ChunkZ}: {ex.Message}"; }
                }
                Check(n == same2, $"chunk model roundtrip {rel}", $"{same2}/{n} identical, tail split {tail}, heightmap matches recalculation {hmOk}, tile entities {tes} ({comp} composite), storage payloads {storageOk}/{storages}{(firstBad != null ? ", " + firstBad : "")}");
                foreach (var nt in notes) Console.WriteLine($"      tail not split: {nt.Value}x {nt.Key}");
                if (feats.Count > 0) Console.WriteLine("      TE feature hashes: " + string.Join(", ", feats.OrderByDescending(k => k.Value).Take(14).Select(k => $"{k.Key:X8}x{k.Value}")));
            }
            // chunk tail diagnostics
            var deep =sc.Slots.Count(s => s.Deep);
            Console.WriteLine($"      deep-verified through entity header: {deep}/{sc.Slots.Count}; fully verified to the last byte: {sc.Slots.Count(s => s.Full)}; with entities: {sc.Slots.Count(s => s.Entities > 0)}; with tile entities: {sc.Slots.Count(s => s.TileEntities > 0)}");
            foreach (var n in sc.Slots.Where(s => s.TailNote != null).GroupBy(s => s.TailNote).Take(6)) Console.WriteLine($"      partial: {n.Count()} chunk(s) – {n.Key}");

        }

        WorldEditTest(ctx);

        foreach (var rel in ctx.ListFilesRel().Where(r => r.EndsWith(".7rm")))
        {
            var raw = MapRegionHandler.Decode(ctx.ReadAll(rel), out var err);
            Check(raw != null && raw.Length == MapRegionHandler.Expected, "map decode " + rel, err);
        }

        Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Edits blocks in a scratch copy (one region + id tables) and verifies the result with the independent chunk scanner.</summary>
    static void WorldEditTest(SaveCtx real)
    {
        var regions = real.ListFilesRel().Where(r => r.StartsWith("Region/") && r.EndsWith(".7rg")).OrderByDescending(r => new FileInfo(real.Abs(r)).Length).ToList();
        if (regions.Count == 0) return;
        var tmp = Path.Combine(Path.GetTempPath(), "SaveDoctorSelfTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "Region"));
            foreach (var f in new[] { "blockmappings.nim", "itemmappings.nim" }) if (File.Exists(real.Abs(f))) File.Copy(real.Abs(f), Path.Combine(tmp, f));
            var rel = regions[0];
            File.Copy(real.Abs(rel), Path.Combine(tmp, rel));
            var ctx = new SaveCtx(tmp) { AllowWhileRunning = true };
            ctx.BackupRoot = Path.Combine(tmp, "_bak");
            RegionIO.TryParseName(rel, out int rx, out int rz);
            var data0 = ctx.ReadAll(rel);
            var sc0 = RegionIO.Scan(data0, rx, rz, false, false);
            var slot = sc0.Slots.Where(s => s.Status != "bad" && s.Blob != null).OrderByDescending(s => s.Blob!.Length).Skip(2).First();
            var doc0 = ChunkDoc.Parse(slot.Blob!, ctx.Game);
            int cx = slot.ChunkX, cz = slot.ChunkZ;
            var g = ctx.Game;
            // multi-block child offset convention
            int children = 0, okParents = 0;
            var we4 = new WorldEdit(ctx);
            foreach (var s in sc0.Slots.Where(s => s.Blob != null && s.Status != "bad").Take(60))
            {
                var dd = ChunkDoc.Parse(s.Blob!, g);
                for (int i = 0; i < 65536 && children < 40; i++)
                {
                    uint v = dd.Blocks[i];
                    if (!ChunkDoc.IsChild(v) || ChunkDoc.TypeOf(v) == 0) continue;
                    int x = dd.X * 16 + (i & 15), z = dd.Z * 16 + ((i >> 4) & 15), y = i >> 8;
                    var (px, py, pz) = WorldEdit.ParentOffset(v);
                    children++;
                    if (we4.TryGet(x + px, y + py, z + pz, out uint pv) && ChunkDoc.TypeOf(pv) == ChunkDoc.TypeOf(v) && !ChunkDoc.IsChild(pv)) okParents++;
                    else Console.WriteLine($"      child {g.NameOf(ChunkDoc.TypeOf(v))} at {x},{y},{z} offset {px},{py},{pz}: found {(we4.TryGet(x + px, y + py, z + pz, out uint q) ? g.NameOf(ChunkDoc.TypeOf(q)) + (ChunkDoc.IsChild(q) ? " (child)" : "") : "no chunk")}");
                }
                if (children >= 40) break;
            }
            if (children > 0) Check(okParents == children, "multi-block children point at a parent block of the same type", $"{okParents}/{children}");
            // find a plain placeable block type
            var we0 = new WorldEdit(ctx);
            int placeType = g.BlockNames.Where(k => k.Key > 0 && we0.PlaceProblem(k.Key) == null && (we0.DefOf(k.Key)?.Terrain ?? false) == false && (k.Value.StartsWith("concrete") || k.Value.StartsWith("woodBlock") || k.Value.StartsWith("cobblestone")) ).Select(k => k.Key).FirstOrDefault();
            if (placeType == 0) placeType = g.BlockNames.Where(k => k.Key > 0 && we0.PlaceProblem(k.Key) == null).Select(k => k.Key).First();
            // an air cell above the ground in the middle of the chunk
            int wx = cx * 16 + 7, wz = cz * 16 + 7;
            int ground = doc0.HeightAt(7, 7);
            int wy = Math.Min(250, ground + 12);
            var we = new WorldEdit(ctx);
            Check(we.TryGet(wx, wy, wz, out uint before) && ChunkDoc.TypeOf(before) == 0, $"world edit: target cell {wx},{wy},{wz} is air");
            we.Place(wx, wy, wz, placeType, 3);
            we.Place(wx, wy + 1, wz, placeType, 0);
            string msg = we.Commit("selftest");
            var data1 = ctx.ReadAll(rel);
            var sc1 = RegionIO.Scan(data1, rx, rz, true, false, g);
            var d1 = ChunkDoc.Parse(sc1.Slots.First(s => s.Index == slot.Index).Blob!, g);
            int lx = 7, lz = 7;
            Check(sc1.Bad == 0 && d1.Blocks[d1.Idx(lx, wy, lz)] == ((uint)placeType | 3u << 16) && d1.Stability.Planes[0][d1.Idx(lx, wy, lz)] == 15 && d1.HeightAt(lx, lz) >= wy + 1 && d1.NeedsLight, "world edit: block placed, stability 15, height raised, relight flagged", msg);
            bool othersSame = sc0.Slots.Where(s => s.Index != slot.Index).All(s => BinUtil.SameBytes(s.Blob!, sc1.Slots.First(x => x.Index == s.Index).Blob!));
            Check(othersSame && sc1.Slots.Count == sc0.Slots.Count, "world edit: all other chunks untouched");
            var we2 = new WorldEdit(ctx);
            we2.Clear(wx, wy + 1, wz); we2.Clear(wx, wy, wz);
            we2.Commit("selftest");
            var d2 = ChunkDoc.Parse(RegionIO.Scan(ctx.ReadAll(rel), rx, rz, false, false).Slots.First(s => s.Index == slot.Index).Blob!, g);
            int firstDiff = -1;
            for (int i = 0; i < 65536; i++) if (d2.Blocks[i] != doc0.Blocks[i] && (ChunkDoc.TypeOf(d2.Blocks[i]) != 0 || ChunkDoc.TypeOf(doc0.Blocks[i]) != 0)) { firstDiff = i; break; }
            Check(firstDiff < 0 && d2.HeightAt(lx, lz) >= doc0.HeightAt(lx, lz) && d2.HeightAt(lx, lz) < wy, "world edit: removing the blocks restores the blocks and lowers the height map again", firstDiff >= 0 ? $"first difference at index {firstDiff}: {doc0.Blocks[firstDiff]:X} → {d2.Blocks[firstDiff]:X}" : $"column height {doc0.HeightAt(lx, lz)} (stored) → {d2.HeightAt(lx, lz)}");
            // tile entity / container handling on the same copy
            var withTe = RegionIO.Scan(ctx.ReadAll(rel), rx, rz, false, false).Slots.Where(s => s.Blob != null).Select(s => (s, d: ChunkDoc.Parse(s.Blob!, g))).FirstOrDefault(t => t.d.Tes.Any(e => e.Composite && e.Feats!.Any(f => f.Hash == StorageData.StorageHash)));
            if (withTe.d != null)
            {
                var te = withTe.d.Tes.First(e => e.Composite && e.Feats!.Any(f => f.Hash == StorageData.StorageHash));
                int bx = withTe.d.X * 16 + te.X, bz = withTe.d.Z * 16 + te.Z;
                var we3 = new WorldEdit(ctx);
                we3.Clear(bx, te.Y, bz);
                we3.Commit("selftest");
                var d3 = ChunkDoc.Parse(RegionIO.Scan(ctx.ReadAll(rel), rx, rz, false, false).Slots.First(s => s.Index == withTe.s.Index).Blob!, g);
                Check(d3.TileEntityAt(te.X, te.Y, te.Z) == null && d3.Tes.Count == withTe.d.Tes.Count - 1, "world edit: removing a container block also removes its tile entity", $"{g.NameOf(te.BlockId)} at {bx},{te.Y},{bz}");
            }
            // schematic copy → paste round trip (into free air at y=200 above a chunk that has tile entities)
            {
                var all = RegionIO.Scan(ctx.ReadAll(rel), rx, rz, false, false).Slots.Where(s => s.Blob != null).Select(s => ChunkDoc.Parse(s.Blob!, g)).Where(d => d.TailParsed && d.Tes.Count(e => e.HasPos) >= 2).OrderByDescending(d => d.Tes.Count).First();
                var t0 = all.Tes.First(e => e.HasPos);
                int bx = all.X * 16 + t0.X, bz = all.Z * 16 + t0.Z, by = t0.Y;
                var (sch, notes) = SchematicOps.Copy(ctx, bx - 4, by - 3, bz - 4, bx + 4, by + 5, bz + 4, "selftest");
                var back = Schematic.FromBytes(sch.ToBytes());
                Check(back.BlockCount == sch.BlockCount && back.Tes.Count == sch.Tes.Count && back.Raw.SequenceEqual(sch.Raw), "schematic file roundtrip", $"{sch.W}×{sch.H}×{sch.D}, {sch.BlockCount} blocks, {sch.Tes.Count} tile entities, palette {sch.Palette.Count}");
                var dry = SchematicOps.Paste(ctx, back, bx - 4, 200, bz - 4, false, true);
                var res = SchematicOps.Paste(ctx, back, bx - 4, 200, bz - 4, false, false);
                var wr = new WorldEdit(ctx);
                int bad = 0, same = 0;
                for (int yy = 0; yy < back.H; yy++) for (int zz = 0; zz < back.D; zz++) for (int xx = 0; xx < back.W; xx++)
                {
                    uint sv = back.Raw[(yy * back.D + zz) * back.W + xx]; int pi = (int)(sv & 0xFFFF);
                    wr.TryGet(bx - 4 + xx, 200 + yy, bz - 4 + zz, out uint nv);
                    uint expect = pi == 0 ? 0 : ((sv & 0xFFFF0000u) | (uint)g.BlockIds[back.Palette[pi]]);
                    if (nv == expect) same++; else bad++;
                }
                var dest = wr.ChunkAt(bx, bz, false)!;
                int teAtDest = back.Tes.Count(t => { var d2 = wr.ChunkAt(bx - 4 + t.X, bz - 4 + t.Z, false)!; return d2.TileEntityAt((bx - 4 + t.X) & 15, 200 + t.Y, (bz - 4 + t.Z) & 15) != null; });
                Check(bad == 0 && res.Placed == back.BlockCount && teAtDest == back.Tes.Count && dry.Placed == res.Placed, "schematic paste reproduces blocks and tile entities", $"{same} cells identical, {teAtDest}/{back.Tes.Count} tile entities, {res.Message}");
                var chk = RegionIO.Scan(ctx.ReadAll(rel), rx, rz, true, false, g);
                Check(chk.Bad == 0 && chk.Slots.All(s => s.Chunk is { Ok: true }), "region still valid after paste");
            }
        }
        catch (Exception ex) { Check(false, "world edit test", ex.Message + " @ " + ex.StackTrace); }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
