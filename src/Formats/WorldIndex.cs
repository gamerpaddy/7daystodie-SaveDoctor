using System.Collections.Concurrent;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class ContainerRec
{
    public int X, Y, Z;               // world position
    public int BlockId;
    public string? Loot;
    public int W, H, Used;
    public bool Touched, PlayerOwned;
    public List<(int type, int count, int quality)> Items = new();
}

/// <summary>Something worth listing in the Objects tab: vending machines, workstations, signs, locked doors, entities lying around …</summary>
public sealed class ObjRec
{
    public string Kind = "", Name = "", Detail = "";
    public int X, Y, Z, Cx, Cz, Index = -1, Count;
    public string? Owner;
    public bool Flag;
}

public sealed class ChunkStat
{
    public int Cx, Cz, Solid, Entities, TileEntities, Constructed, OwnedContainers, Bytes;
    public int[] Types = Array.Empty<int>();
    public int[] Counts = Array.Empty<int>();
    public bool TailSplit;
}

/// <summary>World-wide index built from every intact chunk: block census, containers with their items, entities. Used for search, statistics and heat maps.</summary>
public sealed class WorldIndex
{
    public string Stamp = "";
    public bool Done;
    public int Total, DoneCount;
    public string? Error;
    public DateTime Built;
    public long Millis;
    public int Regions, Chunks, BadChunks, UnsplitChunks;
    public List<ChunkStat> Stats = new();
    public List<ContainerRec> Containers = new();
    public List<ObjRec> Objects = new();
    public Dictionary<int, long> BlockTotals = new();
    public Dictionary<int, int> TeBlocks = new();       // block id → tile entity count
    public List<(int cls, float x, float y, float z)> Entities = new();
}

public static class WorldIndexer
{
    static readonly object gate = new();
    static WorldIndex? cur;
    static string curRoot = "";

    public static string StampOf(SaveCtx ctx)
    {
        var sb = new System.Text.StringBuilder(ctx.Root);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(ctx.Root, "Region"), "r.*.7rg").OrderBy(x => x))
        {
            var fi = new FileInfo(f);
            sb.Append('|').Append(fi.Name).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks);
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Returns the current index (finished or still building). Starts a build when none exists or the save changed.</summary>
    public static WorldIndex Get(SaveCtx ctx, bool force = false)
    {
        lock (gate)
        {
            string stamp = Directory.Exists(Path.Combine(ctx.Root, "Region")) ? StampOf(ctx) : "";
            if (!force && cur != null && curRoot == ctx.Root && cur.Stamp == stamp) return cur;
            if (cur != null && !cur.Done && curRoot == ctx.Root && !force) return cur;
            var idx = new WorldIndex { Stamp = stamp };
            cur = idx; curRoot = ctx.Root;
            Task.Run(() => Build(ctx, idx));
            return idx;
        }
    }

    public static void Invalidate() { lock (gate) cur = null; }

    static void Build(SaveCtx ctx, WorldIndex idx)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var g = ctx.Game;
            var natural = new bool[65536];
            var natRx = new System.Text.RegularExpressions.Regex("^(terr|tree|tallgrass|deadgrass|grass|bush|shrub|cactus|rock|boulder|bedrock|snow|ice|mushroom|flower|fern|brush|stump|bamboo|reed|moss|leaves|vine|ore|water|plantedBerry)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var kv in g.BlockNames) if (kv.Key > 0 && kv.Key < 65536 && natRx.IsMatch(kv.Value)) natural[kv.Key] = true;
            var files = Directory.Exists(Path.Combine(ctx.Root, "Region")) ? Directory.EnumerateFiles(Path.Combine(ctx.Root, "Region"), "r.*.7rg").Where(f => RegionIO.TryParseName(f, out _, out _)).OrderBy(f => f).ToList() : new List<string>();
            idx.Total = files.Count;
            var stats = new ConcurrentBag<ChunkStat>();
            var conts = new ConcurrentBag<ContainerRec>();
            var objs = new ConcurrentBag<ObjRec>();
            var ents = new ConcurrentBag<(int, float, float, float)>();
            var teBlocks = new ConcurrentDictionary<int, int>();
            var totals = new ConcurrentDictionary<int, long>();
            int bad = 0, unsplit = 0, chunks = 0;
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, file =>
            {
                RegionIO.TryParseName(file, out int rx, out int rz);
                byte[] data;
                try { data = File.ReadAllBytes(file); } catch { Interlocked.Increment(ref bad); return; }
                var sc = RegionIO.Scan(data, rx, rz, false, false);
                var counts = new int[65536];
                foreach (var s in sc.Slots)
                {
                    if (s.Blob == null || s.Status == "bad") { Interlocked.Increment(ref bad); continue; }
                    ChunkDoc d;
                    try { d = ChunkDoc.Parse(s.Blob, g); }
                    catch { Interlocked.Increment(ref bad); continue; }
                    Interlocked.Increment(ref chunks);
                    Array.Clear(counts);
                    var bl = d.Blocks;
                    for (int i = 0; i < bl.Length; i++) counts[bl[i] & 0xFFFF]++;
                    var types = new List<int>(); var cnt = new List<int>(); int solid = 0, built = 0;
                    for (int t = 1; t < 65536; t++) if (counts[t] > 0) { types.Add(t); cnt.Add(counts[t]); solid += counts[t]; if (!natural[t]) built += counts[t]; totals.AddOrUpdate(t, counts[t], (_, o) => o + counts[t]); }
                    var cs = new ChunkStat { Cx = d.X, Cz = d.Z, Solid = solid, Constructed = built, Bytes = s.Blob.Length, Types = types.ToArray(), Counts = cnt.ToArray(), TailSplit = d.TailParsed, Entities = d.TailParsed ? d.EntCount : 0, TileEntities = d.TailParsed ? d.Tes.Count : 0 };
                    stats.Add(cs);
                    if (!d.TailParsed) { Interlocked.Increment(ref unsplit); continue; }
                    foreach (var e in d.Ents) ents.Add((e.cls, e.x, e.y, e.z));
                    for (int ei = 0; ei < d.EntRecs.Count; ei++)
                    {
                        var er = d.EntRecs[ei];
                        var det = EntityTools.Describe(er, ei, g);
                        string kind = det.Trader != null ? "trader" : det.DroppedItem != null ? "drop" : det.Kind == "EntityBackpack" ? "backpack" : det.Kind == "EntitySupplyCrate" ? "airdrop" : det.Kind == "EntityLootContainer" ? "lootbag" : det.Inventory != null ? "carrying" : "";
                        if (kind == "") continue;
                        int used = det.Inventory?.Items.Count(x => x.Count > 0) ?? 0;
                        string detail = kind == "drop" ? g.NameOf(det.DroppedItem!.Item.Type) + " ×" + det.DroppedItem.Count : kind == "trader" ? $"{det.Trader!.Primary.Count} stock entries, {det.Trader.AvailableMoney} coins" : $"{used}/{det.Inventory?.Items.Count ?? 0} slots used";
                        objs.Add(new ObjRec { Kind = kind, Name = det.Name, Detail = detail, X = (int)Math.Floor(det.X), Y = (int)Math.Floor(det.Y), Z = (int)Math.Floor(det.Z), Cx = d.X, Cz = d.Z, Index = ei, Count = kind == "drop" ? det.DroppedItem!.Count : used, Flag = !er.Editable });
                    }
                    foreach (var te in d.Tes)
                    {
                        if (te.Legacy != null)
                        {
                            int wx = d.X * 16 + te.X, wz = d.Z * 16 + te.Z;
                            switch (te.Legacy)
                            {
                                case VendingM vm: objs.Add(new ObjRec { Kind = "vending", Name = "trader " + vm.Trader.TraderId, Detail = $"{vm.Trader.Primary.Count} stock entries, {vm.Trader.AvailableMoney} coins" + (vm.Rentable ? ", rentable" : "") + (vm.Locked ? ", locked" : ""), X = wx, Y = te.Y, Z = wz, Count = vm.Trader.Primary.Count, Owner = vm.Owner?.UserId, Flag = vm.Locked }); break;
                                case WorkstationM wm: objs.Add(new ObjRec { Kind = "workstation", Name = g.NameOf(d.Blocks[d.Idx(te.X, te.Y, te.Z)] is var bv ? (int)(bv & 0xFFFF) : 0), Detail = $"{wm.Input.Count(x => x.Count > 0) + wm.Output.Count(x => x.Count > 0) + wm.Fuel.Count(x => x.Count > 0)} items, {(wm.IsBurning ? "burning" : "idle")}, queue {wm.Queue.Count(q => q.HasRecipe)}", X = wx, Y = te.Y, Z = wz, Count = wm.Input.Count(x => x.Count > 0) + wm.Output.Count(x => x.Count > 0), Flag = wm.IsBurning }); break;
                                case ForgeM fm: objs.Add(new ObjRec { Kind = "forge", Name = g.NameOf((int)(d.Blocks[d.Idx(te.X, te.Y, te.Z)] & 0xFFFF)), Detail = $"metal {fm.MetalInForge}", X = wx, Y = te.Y, Z = wz, Count = fm.Input.Count(x => x.Count > 0) + fm.Fuel.Count(x => x.Count > 0) }); break;
                                case CollectorM cm: objs.Add(new ObjRec { Kind = "collector", Name = g.NameOf((int)(d.Blocks[d.Idx(te.X, te.Y, te.Z)] & 0xFFFF)), Detail = $"{cm.Items.Count(x => x.Count > 0)} items", X = wx, Y = te.Y, Z = wz, Count = cm.Items.Count(x => x.Count > 0) }); break;
                            }
                        }
                        if (!te.Composite) { teBlocks.AddOrUpdate(-te.Type, 1, (_, o) => o + 1); continue; }
                        foreach (var ft in te.Feats!)
                        {
                            var kind = TeFeatures.KindOf(ft.Hash);
                            if (kind is not ("signable" or "canvas" or "lockable" or "landclaim")) continue;
                            var fv = TeFeatures.Decode(ft, g);
                            int wx = d.X * 16 + te.X, wz = d.Z * 16 + te.Z;
                            string bn = g.NameOf(te.BlockId);
                            switch (fv.Data)
                            {
                                case SignableM sg when sg.HasText && sg.Text.Length > 0: objs.Add(new ObjRec { Kind = "sign", Name = bn, Detail = sg.Text, X = wx, Y = te.Y, Z = wz, Owner = sg.Author?.UserId }); break;
                                case CanvasM cv: objs.Add(new ObjRec { Kind = "canvas", Name = bn, Detail = cv.Library + " / " + cv.Guid, X = wx, Y = te.Y, Z = wz, Flag = cv.Library != "[D]" && !char.IsLetter(cv.Library.FirstOrDefault()) }); break;
                                case LockableM lk when lk.Locked || lk.PasswordHash.Length > 0: objs.Add(new ObjRec { Kind = "lock", Name = bn, Detail = (lk.Locked ? "locked" : "unlocked") + (lk.PasswordHash.Length > 0 ? ", password" : "") + $", {lk.Allowed.Count} allowed", X = wx, Y = te.Y, Z = wz, Flag = lk.Locked, Owner = te.GetOwner()?.UserId }); break;
                                case LandClaimM: objs.Add(new ObjRec { Kind = "claim", Name = bn, Detail = "land claim block", X = wx, Y = te.Y, Z = wz, Owner = te.GetOwner()?.UserId }); break;
                            }
                        }
                        teBlocks.AddOrUpdate(te.BlockId, 1, (_, o) => o + 1);
                        var f = te.Feats!.FirstOrDefault(x => x.Hash == StorageData.StorageHash);
                        if (f == null) continue;
                        var sd = StorageData.TryRead(f.Payload, g);
                        if (sd == null) continue;
                        var rec = new ContainerRec { X = d.X * 16 + te.X, Y = te.Y, Z = d.Z * 16 + te.Z, BlockId = te.BlockId, Loot = sd.LootList, W = sd.Grid.Width, H = sd.Grid.Height, Touched = sd.Grid.TimeTouched != 0, PlayerOwned = sd.Grid.PlayerOwned };
                        foreach (var st in sd.Grid.Items) if (st.Count > 0 && st.Item.Type != 0) { rec.Used++; rec.Items.Add((st.Item.Type, st.Count, st.Item.Quality)); }
                        conts.Add(rec);
                        if (rec.PlayerOwned) cs.OwnedContainers++;
                    }
                }
                lock (idx) idx.DoneCount++;
            });
            idx.Stats = stats.OrderBy(s => s.Cz).ThenBy(s => s.Cx).ToList();
            idx.Objects = objs.OrderBy(o => o.Kind).ThenBy(o => o.X).ThenBy(o => o.Z).ToList();
            idx.Containers = conts.OrderBy(c => c.X).ThenBy(c => c.Z).ThenBy(c => c.Y).ToList();
            idx.Entities = ents.Select(e => (e.Item1, e.Item2, e.Item3, e.Item4)).ToList();
            idx.TeBlocks = teBlocks.ToDictionary(kv => kv.Key, kv => kv.Value);
            idx.BlockTotals = totals.ToDictionary(kv => kv.Key, kv => kv.Value);
            idx.Regions = files.Count; idx.Chunks = chunks; idx.BadChunks = bad; idx.UnsplitChunks = unsplit;
            idx.Built = DateTime.Now;
        }
        catch (Exception ex) { idx.Error = ex.Message; }
        idx.Millis = sw.ElapsedMilliseconds;
        idx.Done = true;
    }
}
