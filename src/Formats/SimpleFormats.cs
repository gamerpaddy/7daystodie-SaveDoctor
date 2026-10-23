using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

// ───────────────────────────── *.nim (name ↔ id tables) ─────────────────────────────

public sealed class NameIdMap
{
    public int Version { get; set; } = 1;
    public List<NimEntry> Entries { get; set; } = new();
    public int DeclaredCount { get; set; }
    public long TrailingBytes { get; set; }

    List<(int, string)>? tuples;
    public IEnumerable<(int Id, string Name)> EntriesTuple => Entries.Select(e => (e.Id, e.Name));

    public static NameIdMap Parse(byte[] data)
    {
        var m = new NameIdMap();
        using var r = BinUtil.Reader(data);
        r.Need(8, "header");
        m.Version = r.ReadInt32();
        m.DeclaredCount = r.ReadInt32();
        if (m.DeclaredCount < 0 || m.DeclaredCount > 4_000_000) throw new ParseFail("Implausible entry count " + m.DeclaredCount, 4);
        for (int i = 0; i < m.DeclaredCount; i++)
        {
            long at = r.Pos();
            r.Need(5, "entry " + i);
            int id = r.ReadInt32();
            string s = r.Str();
            m.Entries.Add(new NimEntry { Id = id, Name = s });
        }
        m.TrailingBytes = r.Left();
        return m;
    }

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, BinUtil.Utf8, true);
        w.Write(Version);
        w.Write(Entries.Count);
        foreach (var e in Entries) { w.Write(e.Id); w.Str(e.Name); }
        w.Flush();
        return ms.ToArray();
    }
}

public sealed class NimEntry { public int Id { get; set; } public string Name { get; set; } = ""; }

public sealed class NameIdMapHandler : FormatHandler
{
    public override string Kind => "nim";
    public override string Title => "Name ↔ ID mapping";
    public override bool Match(string rel) => rel.EndsWith(".nim", StringComparison.OrdinalIgnoreCase);

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data); a.Editable = true;
        if (LooksWiped(data, out var why)) return a.Add(Sev.Error, "wiped", "This file is unusable: " + why + ".");
        NameIdMap m;
        try { m = NameIdMap.Parse(data); }
        catch (ParseFail pf) { return a.Add(Sev.Error, "parse", pf.Message, pf.Offset); }
        catch (Exception ex) { return a.Add(Sev.Error, "parse", "Cannot parse: " + ex.Message); }

        a.Summary = $"{m.Entries.Count} entries";
        a.Info["entries"] = m.Entries.Count;
        if (m.Version != 1) a.Add(Sev.Warn, "version", $"Unexpected file version {m.Version} (game writes 1).");
        if (m.TrailingBytes > 0) a.Add(Sev.Warn, "trailing", $"{m.TrailingBytes} unexpected bytes after the last entry.", data.Length - m.TrailingBytes);

        var ids = new Dictionary<int, string>();
        var names = new Dictionary<string, int>();
        bool isItems = rel.Contains("item", StringComparison.OrdinalIgnoreCase);
        int dupId = 0, dupName = 0, bad = 0;
        foreach (var e in m.Entries)
        {
            if (e.Id < 0 || e.Id > (isItems ? 65536 * 2 : 65535)) { bad++; if (bad <= 5) a.Add(Sev.Error, "id-range", $"Entry '{e.Name}' has out-of-range id {e.Id}."); }
            if (string.IsNullOrWhiteSpace(e.Name)) { bad++; if (bad <= 5) a.Add(Sev.Error, "empty-name", $"Id {e.Id} has an empty name."); }
            if (!ids.TryAdd(e.Id, e.Name)) { dupId++; if (dupId <= 5) a.Add(Sev.Error, "dup-id", $"Id {e.Id} is used by both '{ids[e.Id]}' and '{e.Name}'."); }
            if (!names.TryAdd(e.Name, e.Id)) { dupName++; if (dupName <= 5) a.Add(Sev.Warn, "dup-name", $"Name '{e.Name}' appears with ids {names[e.Name]} and {e.Id}."); }
        }

        // Compare with the configs that the server dumped (mods removed → names that no longer exist).
        try
        {
            string? cfg = isItems ? "ConfigsDump/items.xml" : "ConfigsDump/blocks.xml";
            var known = ReadNames(ctx, cfg, isItems ? "item" : "block");
            if (isItems) foreach (var n in ReadNames(ctx, "ConfigsDump/item_modifiers.xml", "item_modifier")) known.Add(n);
            if (known.Count > 0)
            {
                var unknown = m.Entries.Where(e => !known.Contains(e.Name) && !known.Contains(e.Name.Split(':')[0])).ToList();
                if (unknown.Count > 0)
                {
                    a.Add(Sev.Info, "unknown-names", $"{unknown.Count} entries are not defined by the current game configs (removed mod?): " +
                        string.Join(", ", unknown.Take(8).Select(u => u.Name)) + (unknown.Count > 8 ? ", …" : ""));
                    a.Info["unknownNames"] = unknown.Select(u => u.Name).Take(200).ToList();
                    a.Repairs.Add(new RepairOption("prune-unknown", $"Remove the {unknown.Count} unknown entr{(unknown.Count == 1 ? "y" : "ies")} that nothing uses",
                        "Deletes entries the current game configs do not define (leftovers of removed mods) – but only if no player, container, vehicle, drone or block in the world still uses that id; used ones are kept and reported. Harmless to leave in place, this just tidies the table.", false));
                }
            }
        }
        catch { }
        return a;
    }

    static HashSet<string> ReadNames(SaveCtx ctx, string rel, string element)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var p = ctx.Abs(rel);
        if (!File.Exists(p)) return set;
        using var xr = XmlReader.Create(p, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
        while (xr.Read())
            if (xr.NodeType == XmlNodeType.Element && xr.Name == element) { var n = xr.GetAttribute("name"); if (n != null) set.Add(n); }
        return set;
    }

    public override object? LoadModel(SaveCtx ctx, string rel, byte[] data)
    {
        var m = NameIdMap.Parse(data);
        return new { m.Version, Entries = m.Entries };
    }

    public override byte[] BuildFromModel(SaveCtx ctx, string rel, byte[] original, JsonElement model)
    {
        var m = new NameIdMap
        {
            Version = model.GetProperty("version").GetInt32(),
            Entries = Json.De<List<NimEntry>>(model.GetProperty("entries"))!,
        };
        return m.Build();
    }

    public override string Repair(SaveCtx ctx, string rel, string action, JsonElement args)
    {
        var data = ctx.ReadAll(rel);
        var m = NameIdMap.Parse(data);
        if (action == "dedupe")
        {
            var seen = new HashSet<int>();
            var keep = new List<NimEntry>();
            foreach (var e in m.Entries) if (seen.Add(e.Id) && !string.IsNullOrWhiteSpace(e.Name)) keep.Add(e);
            int removed = m.Entries.Count - keep.Count;
            m.Entries = keep;
            ctx.WriteFile(rel, m.Build(), "nim-dedupe");
            return $"Removed {removed} duplicate/empty entries.";
        }
        if (action == "prune-unknown")
        {
            bool isItems = rel.Contains("item", StringComparison.OrdinalIgnoreCase);
            var known = ReadNames(ctx, isItems ? "ConfigsDump/items.xml" : "ConfigsDump/blocks.xml", isItems ? "item" : "block");
            if (isItems) foreach (var n in ReadNames(ctx, "ConfigsDump/item_modifiers.xml", "item_modifier")) known.Add(n);
            if (known.Count == 0) throw new InvalidOperationException("The server's config dump is missing, so unknown names cannot be told apart.");
            var unknown = m.Entries.Where(e => !known.Contains(e.Name) && !known.Contains(e.Name.Split(':')[0])).ToList();
            var used = UsedIds(ctx, isItems);
            var drop = unknown.Where(e => !used.Contains(e.Id)).ToList();
            var kept = unknown.Count - drop.Count;
            if (drop.Count == 0) return $"Nothing removed – all {unknown.Count} unknown entr{(unknown.Count == 1 ? "y is" : "ies are")} still used somewhere in the save.";
            var dropSet = drop.ToHashSet();
            m.Entries = m.Entries.Where(e => !dropSet.Contains(e)).ToList();
            ctx.WriteFile(rel, m.Build(), "nim-prune-unknown");
            return $"Removed {drop.Count} unused entr{(drop.Count == 1 ? "y" : "ies")} ({string.Join(", ", drop.Take(5).Select(d => d.Name))}{(drop.Count > 5 ? ", …" : "")})." + (kept > 0 ? $" {kept} still in use and kept." : "");
        }
        return base.Repair(ctx, rel, action, args);
    }

    /// <summary>Ids of items / blocks that players, containers, vehicles, drones or chunks still reference.</summary>
    static HashSet<int> UsedIds(SaveCtx ctx, bool items)
    {
        var used = new HashSet<int>(); var g = ctx.Game;
        void Item(ItemVal? v) { if (v == null || v.Type == 0) return; used.Add(v.Type); if (v.Mods != null) foreach (var m in v.Mods) Item(m); if (v.Cosmetics != null) foreach (var m in v.Cosmetics) Item(m); }
        void GridUse(Grid? gr) { if (gr != null) foreach (var st in gr.Items) if (st.Count > 0) Item(st.Item); }
        foreach (var f in ctx.ListFilesRel().Where(r => r.StartsWith("Player/", StringComparison.OrdinalIgnoreCase) && r.EndsWith(".ttp", StringComparison.OrdinalIgnoreCase)))
            try { var p = PlayerFile.Parse(ctx.ReadAll(f), g); GridUse(p.Inventory.Grid); GridUse(p.Bag.Grid); GridUse(p.Equipment.Grid); foreach (var st in p.DragAndDrop) if (st.Count > 0) Item(st.Item); }
            catch { used.UnionWith(Enumerable.Range(0, 0)); }
        foreach (var f in new[] { "vehicles.dat", "drones.dat" })
            try
            {
                if (!File.Exists(ctx.Abs(f))) continue;
                foreach (var e in EntityListFile.Parse(ctx.ReadAll(f), g).Entities)
                {
                    if (f == "vehicles.dat") { var vd = VehicleDataM.TryParse(e.EntityData, g, out _); GridUse(vd?.Bag); Item(vd?.Item?.Item); }
                    else { var dd = DroneDataM.TryParse(e.EntityData, g, out _); GridUse(dd?.Bag); Item(dd?.Item); }
                }
            }
            catch { }
        var ix = WorldIndexer.Get(ctx);
        for (int i = 0; i < 600 && !ix.Done; i++) Thread.Sleep(200);
        if (!ix.Done) throw new InvalidOperationException("The world index is still being built – try again in a minute.");
        if (items) foreach (var c in ix.Containers) foreach (var it in c.Items) used.Add(it.type);
        else { used.UnionWith(ix.BlockTotals.Keys); foreach (var k in ix.TeBlocks.Keys) if (k > 0) used.Add(k); }
        return used;
    }
}

// ───────────────────────────── small .dat files ─────────────────────────────

public sealed class SimpleDatHandler : FormatHandler
{
    public override string Kind => "dat";
    public override string Title => "Game state (.dat)";
    static readonly string[] Names = { "blockLimits.dat", "power.dat", "drones.dat", "turrets.dat", "vehicles.dat" };
    public override bool Match(string rel) => Names.Any(n => rel.Equals(n, StringComparison.OrdinalIgnoreCase));

    static string FileKey(string rel) => Path.GetFileName(rel).ToLowerInvariant();

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data); a.Editable = true;
        var key = FileKey(rel);
        a.Title = key switch
        {
            "blocklimits.dat" => "Block limit tracker",
            "power.dat" => "Power grid",
            "drones.dat" => "Drones",
            "turrets.dat" => "Turrets",
            "vehicles.dat" => "Vehicles",
            _ => Title
        };
        if (LooksWiped(data, out var why)) return a.Add(Sev.Error, "wiped", "This file is unusable: " + why + ".");
        try
        {
            using var r = BinUtil.Reader(data);
            switch (key)
            {
                case "blocklimits.dat":
                    {
                        byte v = r.ReadByte();
                        if (v != 1) a.Add(Sev.Error, "version", $"Unsupported version {v} (game expects 1).", 0);
                        int p = ReadVecList(r, a, "powered blocks");
                        int s = ReadVecList(r, a, "storage blocks");
                        a.Summary = $"{p} powered blocks, {s} storage blocks";
                        break;
                    }
                case "power.dat":
                    {
                        var pf = PowerFile.Parse(data, ctx.Game);
                        var all = pf.All().ToList();
                        a.Summary = $"version {pf.Version}, {all.Count} power nodes in {pf.Circuits.Count} circuits";
                        a.Info["circuits"] = pf.Circuits.Count; a.Info["nodes"] = all.Count;
                        a.Info["editor"] = "power";
                        if (pf.TrailingBytes > 0) a.Add(Sev.Warn, "trailing", $"{pf.TrailingBytes} unexpected bytes after the last node.", data.Length - pf.TrailingBytes);
                        var seen = new Dictionary<string, PowerNodeM>();
                        int dup = 0;
                        foreach (var n in all)
                        {
                            var k = n.Pos[0] + "," + n.Pos[1] + "," + n.Pos[2];
                            if (!seen.TryAdd(k, n)) { dup++; if (dup <= 5) a.Add(Sev.Error, "dup-pos", $"Two power nodes share the position {k} – the game throws \"duplicate key\" while loading the grid."); }
                        }
                        if (dup > 5) a.Add(Sev.Error, "dup-pos", $"… and {dup - 5} more duplicate positions.");
                        var missing = new SortedSet<int>();
                        if (ctx.Game.BlockNames.Count > 0) foreach (var n in all) if (!ctx.Game.BlockNames.ContainsKey(n.BlockId)) missing.Add(n.BlockId);
                        if (missing.Count > 0)
                        {
                            // blockmappings.nim only gets new ids when the game saves it, so a freshly placed block can be missing for a while
                            bool live = SaveCtx.IsServerRunning(out _);
                            a.Add(live ? Sev.Info : Sev.Warn, "unknown-block", $"Block id(s) {string.Join(", ", missing)} used by the power grid are not in blockmappings.nim yet" + (live ? " – normal while the game runs; it adds new ids when it saves." : " (removed mod, or the game has not saved its id table since they were placed)."));
                        }
                        foreach (var n in all) foreach (var c in n.Children)
                            if (c.ParentPos != null && (c.ParentPos[0] != n.Pos[0] || c.ParentPos[1] != n.Pos[1] || c.ParentPos[2] != n.Pos[2]))
                            { a.Add(Sev.Warn, "parent-pos", $"Node at {c.Pos[0]},{c.Pos[1]},{c.Pos[2]} records a different parent position than the node it hangs under."); break; }
                        a.Editable = true;
                        if (dup > 0) a.Repairs.Add(new RepairOption("dedupePower", "Remove duplicate power nodes", "Keeps the first node at every position and drops later duplicates (with their wired children).", true));
                        break;
                    }
                case "drones.dat":
                case "vehicles.dat":
                    {
                        Header(r, a);
                        r.Need(4);
                        int n = r.ReadInt32();
                        if (n < 0 || n > 1_000_000) a.Add(Sev.Error, "count", $"Implausible entity count {n}.", r.Pos() - 4);
                        a.Summary = $"{n} stored {(key == "drones.dat" ? "drones" : "vehicles")}";
                        a.Info["editor"] = "entities";
                        a.Info["count"] = n;
                        if (n == 0 && r.Left() != 0) a.Add(Sev.Warn, "trailing", $"{r.Left()} unexpected bytes after header.", r.Pos());
                        if (n > 0)
                        {
                            try
                            {
                                var list = new List<Ecd>();
                                for (int i = 0; i < n; i++) list.Add(Ecd.Read(r, ctx.Game, false));
                                a.Info["entities"] = list.Select(e => new { e.Id, Class = ctx.Game.EntityClassName(e.EntityClass), e.PosX, e.PosY, e.PosZ }).ToList();
                                a.Info["editor"] = "entities";
                                if (key == "vehicles.dat")
                                    foreach (var e in list)
                                        if (VehicleDataM.TryParse(e.EntityData, ctx.Game, out var verr) == null)
                                            a.Add(Sev.Info, "vehicle-data", $"Vehicle {ctx.Game.EntityClassName(e.EntityClass)} #{e.Id}: detail data could not be decoded ({verr}); it is kept unchanged.");
                                if (r.Left() != 0) a.Add(Sev.Warn, "trailing", $"{r.Left()} unexpected bytes after last entity.", r.Pos());
                            }
                            catch (ParseFail pf) { a.Add(Sev.Error, "entity", "Stored entity data is damaged: " + pf.Message, pf.Offset); }
                            catch (EndOfStreamException) { a.Add(Sev.Error, "eof", "File ends in the middle of an entity record (truncated).", r.Pos()); }
                        }
                        break;
                    }
                case "turrets.dat":
                    {
                        Header(r, a);
                        r.Need(4);
                        int n = r.ReadInt32();
                        if (n < 0 || n * 4L > r.Left()) { a.Add(Sev.Error, "count", $"Turret count {n} does not fit in the file ({r.Left()} bytes left).", r.Pos() - 4); break; }
                        var ids = new List<int>();
                        for (int i = 0; i < n; i++) ids.Add(r.ReadInt32());
                        a.Summary = $"{n} turrets";
                        a.Info["ids"] = ids;
                        if (r.Left() != 0) a.Add(Sev.Warn, "trailing", $"{r.Left()} unexpected bytes at end.", r.Pos());
                        break;
                    }
            }
        }
        catch (ParseFail pf) { a.Add(Sev.Error, "parse", pf.Message, pf.Offset); }
        catch (EndOfStreamException) { a.Add(Sev.Error, "eof", "File is truncated (ended unexpectedly)."); }
        catch (Exception ex) { a.Add(Sev.Error, "parse", ex.Message); }

        if (a.Worst >= Sev.Error)
        {
            if (key != "power.dat")
                a.Repairs.Add(new RepairOption("reset", "Reset to an empty file", key switch
                {
                    "blocklimits.dat" => "Writes an empty tracker (block limit counters restart from 0).",
                    "turrets.dat" => "Writes an empty turret list.",
                    _ => "Writes an empty list. Stored entities in this file are lost.",
                }, true));
            else
                a.Repairs.Add(new RepairOption("reset", "Reset to an empty power grid", "All wire connections / power nodes are lost – only do this if no backup is usable.", true));
        }
        return a;
    }

    static void Header(BinaryReader r, Analysis a)
    {
        r.Need(5, "header");
        var m = new string(new[] { (char)r.ReadByte(), (char)r.ReadByte(), (char)r.ReadByte() });
        byte z = r.ReadByte();
        byte v = r.ReadByte();
        if (m != "vda" || z != 0) a.Add(Sev.Error, "magic", "Wrong signature (expected 'vda').", 0);
        if (v != 1) a.Add(Sev.Error, "version", $"Unsupported version {v} (expected 1).", 4);
    }

    static int ReadVecList(BinaryReader r, Analysis a, string what)
    {
        r.Need(4, what);
        int n = r.ReadInt32();
        if (n < 0 || n * 12L > r.Left()) { a.Add(Sev.Error, "count", $"Count of {what} ({n}) does not fit in the file.", r.Pos() - 4); return 0; }
        r.BaseStream.Seek(n * 12L, SeekOrigin.Current);
        return n;
    }

    public override object? LoadModel(SaveCtx ctx, string rel, byte[] data)
    {
        var key = FileKey(rel);
        using var r = BinUtil.Reader(data);
        if (key == "blocklimits.dat")
        {
            r.ReadByte();
            List<int[]> L() { int n = r.ReadInt32(); var l = new List<int[]>(); for (int i = 0; i < n; i++) l.Add(new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() }); return l; }
            return new { Powered = L(), Storage = L() };
        }
        if (key == "power.dat")
        {
            var pf = PowerFile.Parse(data, ctx.Game);
            var names = new Dictionary<string, string>();
            foreach (var n in pf.All()) { names[n.BlockId.ToString()] = ctx.Game.BlockNames.GetValueOrDefault(n.BlockId, "block#" + n.BlockId); if (n.Slots != null) foreach (var st in n.Slots) if (st.Item.Type != 0) names[st.Item.Type.ToString()] = ctx.Game.NameOf(st.Item.Type); }
            return new { Power = pf, Names = names };
        }
        if (key is "vehicles.dat" or "drones.dat")
        {
            var el = EntityListFile.Parse(data, ctx.Game);
            var names = new Dictionary<string, string>();
            var items = new List<object>();
            foreach (var e in el.Entities)
            {
                string cls = ctx.Game.EntityClassName(e.EntityClass);
                object? detail = null; string? err = null;
                if (key == "vehicles.dat") detail = VehicleDataM.TryParse(e.EntityData, ctx.Game, out err);
                else detail = DroneDataM.TryParse(e.EntityData, ctx.Game, out err);
                void Nm(ItemVal? it) { if (it != null && it.Type != 0) names[it.Type.ToString()] = ctx.Game.NameOf(it.Type); }
                if (detail is VehicleDataM vd) { Nm(vd.Item?.Item); if (vd.Bag != null) foreach (var st in vd.Bag.Items) Nm(st.Item); }
                if (detail is DroneDataM dd) { Nm(dd.Item); Nm(dd.SyncItem); if (dd.Bag != null) foreach (var st in dd.Bag.Items) Nm(st.Item); }
                items.Add(new { Ecd = e, ClassName = cls, Detail = detail, DetailError = err });
            }
            return new { Kind = key, Entities = items, Names = names };
        }
        if (key == "turrets.dat")
        {
            r.BaseStream.Seek(5, SeekOrigin.Begin);
            int n = r.ReadInt32(); var l = new List<int>(); for (int i = 0; i < n; i++) l.Add(r.ReadInt32());
            return new { Ids = l };
        }
        return null;
    }

    public override byte[] BuildFromModel(SaveCtx ctx, string rel, byte[] original, JsonElement model)
    {
        var key = FileKey(rel);
        if (key == "power.dat")
        {
            var pf = Json.De<PowerFile>(model.GetProperty("power")) ?? throw new InvalidOperationException("Empty model");
            var bytes = pf.Build();
            PowerFile.Parse(bytes, ctx.Game);
            return bytes;
        }
        if (key is "vehicles.dat" or "drones.dat")
        {
            var el = new EntityListFile();
            foreach (var item in model.GetProperty("entities").EnumerateArray())
            {
                var e = Json.De<Ecd>(item.GetProperty("ecd"))!;
                if (item.TryGetProperty("detail", out var dj) && dj.ValueKind == JsonValueKind.Object)
                {
                    // the cargo grid lives twice (entity bag + sync data); keep both in step
                    if (key == "vehicles.dat")
                    {
                        var vd = Json.De<VehicleDataM>(dj)!; e.EntityData = vd.Write();
                        if (vd.Bag != null && e.Bag != null) e.Bag = new BagBlob { Grid = vd.Bag }.Write();
                    }
                    else
                    {
                        var dd = Json.De<DroneDataM>(dj)!; e.EntityData = dd.Write();
                        if (dd.Bag != null && e.Bag != null) e.Bag = new BagBlob { Grid = dd.Bag }.Write();
                    }
                }
                el.Entities.Add(e);
            }
            var bytes = el.Build(ctx.Game);
            EntityListFile.Parse(bytes, ctx.Game);
            return bytes;
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        if (key == "blocklimits.dat")
        {
            w.Write((byte)1);
            foreach (var prop in new[] { "powered", "storage" })
            {
                var l = Json.De<List<int[]>>(model.GetProperty(prop))!;
                w.Write(l.Count);
                foreach (var v in l) { w.Write(v[0]); w.Write(v[1]); w.Write(v[2]); }
            }
        }
        else if (key == "turrets.dat")
        {
            w.Write(new byte[] { (byte)'v', (byte)'d', (byte)'a', 0, 1 });
            var l = Json.De<List<int>>(model.GetProperty("ids"))!;
            w.Write(l.Count); foreach (var v in l) w.Write(v);
        }
        else throw new NotSupportedException();
        w.Flush();
        return ms.ToArray();
    }

    public override string Repair(SaveCtx ctx, string rel, string action, JsonElement args)
    {
        if (action == "dedupePower")
        {
            var pf = PowerFile.Parse(ctx.ReadAll(rel), ctx.Game);
            var seen = new HashSet<string>(); int removed = 0;
            void Walk(List<PowerNodeM> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var n = list[i]; var k = n.Pos[0] + "," + n.Pos[1] + "," + n.Pos[2];
                    if (!seen.Add(k)) { removed++; list.RemoveAt(i--); continue; }
                    Walk(n.Children);
                }
            }
            Walk(pf.Circuits);
            ctx.WriteFile(rel, pf.Build(), "dedupe-power");
            return $"Removed {removed} duplicate power node(s).";
        }
        if (action != "reset") return base.Repair(ctx, rel, action, args);
        var key = FileKey(rel);
        byte[] empty = key switch
        {
            "blocklimits.dat" => new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 },
            "power.dat" => new byte[] { 2, 0, 0, 0, 0 },
            _ => new byte[] { (byte)'v', (byte)'d', (byte)'a', 0, 1, 0, 0, 0, 0 },
        };
        ctx.WriteFile(rel, empty, "reset-" + key);
        return "Reset " + rel + " to an empty file.";
    }
}

// ───────────────────────────── decoration.7dt ─────────────────────────────

public sealed class DecoHandler : FormatHandler
{
    public override string Kind => "deco";
    public override string Title => "World decorations (trees, rocks…)";
    public override bool Match(string rel) => rel.Equals("decoration.7dt", StringComparison.OrdinalIgnoreCase);
    const int Rec = 17;

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data); a.Editable = true;
        if (LooksWiped(data, out var why)) { a.Add(Sev.Error, "wiped", "This file is unusable: " + why + "."); AddRepairs(a); return a; }
        try
        {
            using var r = BinUtil.Reader(data);
            r.Need(5, "header");
            byte ver = r.ReadByte();
            int n = r.ReadInt32();
            if (ver != 6) a.Add(Sev.Error, "version", $"Version {ver}, the game requires 6 and will delete + regenerate the file.", 0);
            long expect = 5 + (long)n * Rec;
            a.Summary = $"{n:N0} decorations";
            a.Info["count"] = n;
            if (n < 0 || expect > data.Length)
            {
                long fit = (data.Length - 5) / Rec;
                a.Add(Sev.Error, "truncated", $"Header says {n:N0} records but the file only holds {fit:N0} complete ones (file is truncated or the count is garbage).", 1);
                a.Info["recoverable"] = Math.Max(0, fit);
            }
            else
            {
                if (expect < data.Length) a.Add(Sev.Warn, "trailing", $"{data.Length - expect} extra bytes after the last record.", expect);
                // sample-check record sanity
                int bad = 0, nonFinite = 0;
                var perBlock = new Dictionary<int, int>();
                for (int i = 0; i < n; i++)
                {
                    ulong pos = r.ReadUInt64();
                    float y = r.ReadSingle();
                    uint bv = r.ReadUInt32();
                    byte st = r.ReadByte();
                    if (!float.IsFinite(y)) nonFinite++;
                    if (st > 2) bad++;
                    int id = (int)(bv & 0xFFFF);
                    perBlock[id] = perBlock.GetValueOrDefault(id) + 1;
                }
                if (bad > 0) a.Add(Sev.Error, "state", $"{bad} records have an invalid state byte.");
                if (nonFinite > 0) a.Add(Sev.Error, "float", $"{nonFinite} records have NaN/Infinity heights.");
                var top = perBlock.OrderByDescending(kv => kv.Value).Take(10).Select(kv => new { Id = kv.Key, Name = ctx.Game.BlockNames.GetValueOrDefault(kv.Key, "block#" + kv.Key), Count = kv.Value }).ToList();
                a.Info["topBlocks"] = top;
                var unknown = perBlock.Keys.Where(k => k != 0 && !ctx.Game.BlockNames.ContainsKey(k)).ToList();
                if (unknown.Count > 0) a.Add(Sev.Warn, "unknown-blocks", $"{unknown.Count} block ids used by decorations are missing from blockmappings.nim: " + string.Join(", ", unknown.Take(10)));
            }
        }
        catch (Exception ex) { a.Add(Sev.Error, "parse", ex.Message); }
        if (a.Worst >= Sev.Warn) AddRepairs(a);
        return a;
    }

    void AddRepairs(Analysis a)
    {
        if (a.Info.TryGetValue("recoverable", out var rv) && rv is long rl && rl > 0)
            a.Repairs.Add(new RepairOption("truncate", "Keep the complete records", $"Fixes the count and keeps {rl:N0} intact decorations; the rest are re-generated by the game.", true));
        a.Repairs.Add(new RepairOption("delete", "Delete file (game regenerates decorations)",
            "Safe: decorations are only trees/rocks/plants. Player-built blocks are stored in the region files and are not affected. Decorations you already harvested may reappear.", true));
    }

    public override string Repair(SaveCtx ctx, string rel, string action, JsonElement args)
    {
        var data = ctx.ReadAll(rel);
        if (action == "delete") { ctx.DeleteFile(rel, "delete-deco"); return "Deleted decoration.7dt – the game will regenerate it on next start."; }
        if (action == "truncate")
        {
            int n = (int)Math.Max(0, (data.Length - 5) / Rec);
            var outp = new byte[5 + n * Rec];
            Array.Copy(data, outp, outp.Length);
            outp[0] = 6;
            BitConverter.GetBytes(n).CopyTo(outp, 1);
            ctx.WriteFile(rel, outp, "truncate-deco");
            return $"Rewrote header with {n:N0} complete records.";
        }
        return base.Repair(ctx, rel, action, args);
    }
}

// ───────────────────────────── multiblocks.7dt ─────────────────────────────

public sealed class MultiBlockHandler : FormatHandler
{
    public override string Kind => "multiblock";
    public override string Title => "Multi-block tracker";
    public override bool Match(string rel) => rel.Equals("multiblocks.7dt", StringComparison.OrdinalIgnoreCase);
    const int Rec = 17;

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data); a.Editable = true;
        if (LooksWiped(data, out var why)) { a.Add(Sev.Error, "wiped", "This file is unusable: " + why + "."); AddRepairs(a); return a; }
        try
        {
            using var r = BinUtil.Reader(data);
            r.Need(5, "header");
            byte ver = r.ReadByte();
            int n = r.ReadInt32();
            if (ver != 6) a.Add(Sev.Error, "version", $"Version {ver}; the game expects 6 and ignores the file.", 0);
            long expect = 5 + (long)n * Rec;
            a.Summary = $"{n:N0} tracked blocks";
            a.Info["count"] = n;
            if (n < 0 || expect > data.Length)
            {
                long fit = (data.Length - 5) / Rec;
                a.Add(Sev.Error, "truncated", $"Header says {n:N0} records but only {fit:N0} fit – the game throws while reading this and the server may fail to start.", 1);
                a.Info["recoverable"] = Math.Max(0, fit);
            }
            else
            {
                if (expect < data.Length) a.Add(Sev.Warn, "trailing", $"{data.Length - expect} extra bytes after the last record (an interrupted shorter rewrite?).", expect);
                int badFlags = 0;
                for (int i = 0; i < n; i++)
                {
                    r.BaseStream.Seek(16, SeekOrigin.Current);
                    byte f = r.ReadByte();
                    if (f == 0 || f > 15) badFlags++;
                }
                if (badFlags > 0) a.Add(Sev.Error, "flags", $"{badFlags} records have impossible tracking flags (data is garbled).");
            }
        }
        catch (Exception ex) { a.Add(Sev.Error, "parse", ex.Message); }
        if (a.Worst >= Sev.Warn) AddRepairs(a);
        return a;
    }

    void AddRepairs(Analysis a)
    {
        if (a.Info.TryGetValue("recoverable", out var rv) && rv is long rl && rl > 0)
            a.Repairs.Add(new RepairOption("truncate", "Keep complete records", $"Fix the header and keep {rl:N0} complete records.", true));
        a.Repairs.Add(new RepairOption("delete", "Delete file (rebuilt from chunks)", "The game rebuilds multi-block tracking as chunks load.", true));
    }

    public override object? LoadModel(SaveCtx ctx, string rel, byte[] data)
    {
        var recs = MultiBlockRec.Parse(data);
        var names = recs.Select(r => (int)(r.Raw & 0xFFFF)).Distinct().ToDictionary(id => id.ToString(), id => ctx.Game.BlockNames.TryGetValue(id, out var n) ? n : "block#" + id);
        return new { Records = recs, Names = names };
    }

    public override byte[] BuildFromModel(SaveCtx ctx, string rel, byte[] original, JsonElement model)
        => MultiBlockRec.Build(Json.De<List<MultiBlockRec>>(model.GetProperty("records")) ?? new());

    public override string Repair(SaveCtx ctx, string rel, string action, JsonElement args)
    {
        var data = ctx.ReadAll(rel);
        if (action == "delete") { ctx.DeleteFile(rel, "delete-multiblocks"); return "Deleted multiblocks.7dt."; }
        if (action == "truncate")
        {
            int n = (int)Math.Max(0, (data.Length - 5) / Rec);
            var outp = new byte[5 + n * Rec];
            Array.Copy(data, outp, outp.Length);
            outp[0] = 6;
            BitConverter.GetBytes(n).CopyTo(outp, 1);
            ctx.WriteFile(rel, outp, "truncate-multiblocks");
            return $"Rewrote header with {n:N0} complete records.";
        }
        return base.Repair(ctx, rel, action, args);
    }
}

// ───────────────────────────── Player/<id>/r.X.Z.7rm (explored map) ─────────────────────────────

public sealed class MapRegionHandler : FormatHandler
{
    public override string Kind => "map";
    public override string Title => "Explored map region";
    public override bool Match(string rel) => rel.EndsWith(".7rm", StringComparison.OrdinalIgnoreCase);
    public const int Expected = 1024 * 256 * 2;

    public static byte[]? Decode(byte[] data, out string? error)
    {
        error = null;
        if (data.Length < 4 + 18) { error = "file too short"; return null; }
        int ver = BitConverter.ToInt32(data, 0);
        if (ver != 1) { error = "unsupported version " + ver; return null; }
        try
        {
            using var inp = new MemoryStream(data, 4, data.Length - 4, false);
            using var gz = new GZipStream(inp, CompressionMode.Decompress);
            using var outp = new MemoryStream();
            gz.CopyTo(outp);
            return outp.ToArray();
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data);
        if (LooksWiped(data, out var why)) { a.Add(Sev.Error, "wiped", "This file is unusable: " + why + "."); AddRepairs(a); return a; }
        var raw = Decode(data, out var err);
        if (raw == null) { a.Add(Sev.Error, "gzip", "Map data cannot be decompressed: " + err); AddRepairs(a); return a; }
        if (raw.Length != Expected) a.Add(Sev.Error, "length", $"Decompressed size is {raw.Length} bytes, expected {Expected}.");
        else
        {
            int used = 0;
            for (int c = 0; c < 1024; c++)
            {
                bool any = false;
                for (int i = 0; i < 256 && !any; i++) if (raw[c * 512 + i * 2] != 0 || raw[c * 512 + i * 2 + 1] != 0) any = true;
                if (any) used++;
            }
            a.Summary = $"{used} of 1024 chunks explored";
            a.Info["explored"] = used;
            a.Info["preview"] = true;
        }
        if (a.Worst >= Sev.Error) AddRepairs(a);
        return a;
    }

    void AddRepairs(Analysis a) =>
        a.Repairs.Add(new RepairOption("delete", "Delete this map file", "The player only loses the explored-map fog for this 512×512 area; nothing else is affected.", true));

    public override string Repair(SaveCtx ctx, string rel, string action, JsonElement args)
    {
        if (action == "delete") { ctx.DeleteFile(rel, "delete-map"); return "Deleted " + rel; }
        return base.Repair(ctx, rel, action, args);
    }
}

// ───────────────────────────── XML ─────────────────────────────

public sealed class XmlHandler : FormatHandler
{
    public override string Kind => "xml";
    public override string Title => "XML file";
    public override bool Match(string rel) =>
        rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

    public static string DecodeText(byte[] data, out bool bom)
    {
        bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
        return BinUtil.Utf8.GetString(data, bom ? 3 : 0, data.Length - (bom ? 3 : 0));
    }

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data); a.Editable = true;
        bool isDump = rel.StartsWith("ConfigsDump/", StringComparison.OrdinalIgnoreCase);
        if (isDump) a.Title = "Config dump (informational copy of game XML)";
        if (rel.Equals("players.xml", StringComparison.OrdinalIgnoreCase)) a.Title = "Persistent players (land claims, allies, names)";
        if (LooksWiped(data, out var why))
        {
            a.Add(Sev.Error, "wiped", "This file is unusable: " + why + ".");
            AddRepairs(a, rel, false);
            return a;
        }
        var text = DecodeText(data, out _);
        try
        {
            var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
            a.Summary = "<" + doc.Root?.Name.LocalName + "> well-formed";
            if (rel.Equals("players.xml", StringComparison.OrdinalIgnoreCase)) PlayersChecks(a, doc);
        }
        catch (XmlException xe)
        {
            a.Add(Sev.Error, "xml", $"Not well-formed XML: {xe.Message}", null);
            a.Info["line"] = xe.LineNumber; a.Info["column"] = xe.LinePosition;
            AddRepairs(a, rel, true);
        }
        if (rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !isDump && a.Worst >= Sev.Error) AddRepairs(a, rel, true);
        return a;
    }

    static void PlayersChecks(Analysis a, XDocument doc)
    {
        var root = doc.Root!;
        if (root.Name.LocalName != "persistentplayerdata") a.Add(Sev.Error, "root", "Root element should be <persistentplayerdata>.");
        int n = 0;
        foreach (var p in root.Elements("player"))
        {
            n++;
            if (p.Attribute("userid") == null || p.Attribute("platform") == null)
                a.Add(Sev.Error, "player", "A <player> element is missing platform/userid – the game refuses to load the list.");
        }
        a.Info["players"] = n;
        a.Summary += $", {n} players";
    }

    void AddRepairs(Analysis a, string rel, bool salvage)
    {
        if (salvage && rel.Equals("players.xml", StringComparison.OrdinalIgnoreCase))
            a.Repairs.Add(new RepairOption("salvage", "Salvage complete <player> entries",
                "Reads the file as far as it is valid and rewrites a clean document with every intact player. (If left broken the game starts with an EMPTY player list – land claims and allies are lost.)", true));
    }

    public override object? LoadModel(SaveCtx ctx, string rel, byte[] data)
    {
        var text = DecodeText(data, out var bom);
        return new { Text = text, Bom = bom };
    }

    public override byte[] BuildFromModel(SaveCtx ctx, string rel, byte[] original, JsonElement model)
    {
        var text = model.GetProperty("text").GetString() ?? "";
        bool force = model.TryGetProperty("force", out var f) && f.GetBoolean();
        if (!force)
        {
            try { XDocument.Parse(text); }
            catch (XmlException xe) { throw new InvalidOperationException($"XML is not well-formed (line {xe.LineNumber}, col {xe.LinePosition}): {xe.Message}"); }
        }
        bool bom = model.TryGetProperty("bom", out var b) && b.GetBoolean();
        var body = BinUtil.Utf8.GetBytes(text);
        if (!bom) return body;
        var outp = new byte[body.Length + 3];
        outp[0] = 0xEF; outp[1] = 0xBB; outp[2] = 0xBF;
        Array.Copy(body, 0, outp, 3, body.Length);
        return outp;
    }

    public override string Repair(SaveCtx ctx, string rel, string action, JsonElement args)
    {
        if (action == "salvage")
        {
            var data = ctx.ReadAll(rel);
            var text = DecodeText(data, out var bom);
            var kept = new List<XElement>();
            XElement? alliesEl = null;
            string version = "1";
            try
            {
                using var xr = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
                xr.MoveToContent();
                version = xr.GetAttribute("version") ?? "1";
                xr.Read();
                while (!xr.EOF)
                {
                    xr.MoveToContent();
                    if (xr.NodeType == XmlNodeType.Element && (xr.Name == "player" || xr.Name == "allies"))
                    {
                        var el = (XElement)XNode.ReadFrom(xr);
                        if (el.Name == "player" && el.Attribute("userid") != null && el.Attribute("platform") != null) kept.Add(el);
                        else if (el.Name == "allies") alliesEl = el;
                    }
                    else if (xr.NodeType == XmlNodeType.Element) xr.Skip();
                    else if (!xr.Read()) break;
                }
            }
            catch (XmlException) { /* stop at the damaged spot, keep what we have */ }
            // the last <player> may have been cut off in the middle of its start tag: keep the attributes that were written completely
            int partial = 0;
            var starts = Regex.Matches(text, @"<player\b");
            for (int mi = kept.Count; mi < starts.Count; mi++)
            {
                int from = starts[mi].Index;
                int to = mi + 1 < starts.Count ? starts[mi + 1].Index : text.Length;
                var chunk = text.Substring(from, to - from);
                int gt = chunk.IndexOf('>');
                var tag = gt >= 0 ? chunk.Substring(0, gt) : chunk;
                var el = new XElement("player");
                foreach (Match am in Regex.Matches(tag, @"\s([A-Za-z_][\w.:-]*)=""([^""]*)"""))
                    el.SetAttributeValue(am.Groups[1].Value, System.Net.WebUtility.HtmlDecode(am.Groups[2].Value));
                if (el.Attribute("userid") != null && el.Attribute("platform") != null) { kept.Add(el); partial++; }
            }
            var root = new XElement("persistentplayerdata", new XAttribute("version", version));
            foreach (var k in kept) root.Add(k);
            if (alliesEl != null) root.Add(alliesEl);
            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
            var sb = new StringWriter();
            using (var xw = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false })) doc.Save(xw);
            ctx.WriteFile(rel, BinUtil.Utf8.GetBytes(sb.ToString()), "salvage-players");
            return $"Rewrote players.xml with {kept.Count} players" + (partial > 0 ? $" ({partial} of them was cut off; its basic data was recovered but nested details such as land-claim lists may be missing)." : ".");
        }
        return base.Repair(ctx, rel, action, args);
    }
}

public sealed class JsonHandler : FormatHandler
{
    public override string Kind => "json";
    public override string Title => "JSON file";
    public override bool Match(string rel) => rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data); a.Editable = true;
        if (LooksWiped(data, out var why)) return a.Add(Sev.Error, "wiped", "This file is unusable: " + why + ".");
        try { using var d = JsonDocument.Parse(XmlHandler.DecodeText(data, out _)); a.Summary = "valid JSON"; }
        catch (JsonException je) { a.Add(Sev.Error, "json", "Invalid JSON: " + je.Message); }
        return a;
    }
    public override object? LoadModel(SaveCtx ctx, string rel, byte[] data) => new { Text = XmlHandler.DecodeText(data, out var b), Bom = b };
    public override byte[] BuildFromModel(SaveCtx ctx, string rel, byte[] original, JsonElement model)
    {
        var text = model.GetProperty("text").GetString() ?? "";
        JsonDocument.Parse(text).Dispose();
        return BinUtil.Utf8.GetBytes(text);
    }
}

public sealed class PngHandler : FormatHandler
{
    public override string Kind => "png";
    public override string Title => "Map tile image (PNG)";
    public override bool Match(string rel) => rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    static readonly uint[] CrcTable = BuildCrc();
    static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    public static uint Crc32(byte[] buf, int off, int len)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = 0; i < len; i++) c = CrcTable[(c ^ buf[off + i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data);
        if (LooksWiped(data, out var why)) { a.Add(Sev.Error, "wiped", "This file is unusable: " + why + "."); a.Repairs.Add(new RepairOption("delete", "Delete tile", "The map tile is re-rendered when the area is visited again.", true)); return a; }
        byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(sig)) { a.Add(Sev.Error, "sig", "Not a PNG (bad signature)."); }
        else
        {
            int p = 8; bool iend = false; int w = 0, h = 0;
            while (p + 12 <= data.Length)
            {
                int len = (data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
                if (len < 0 || p + 12 + len > data.Length) { a.Add(Sev.Error, "truncated", "PNG is cut off inside a chunk.", p); break; }
                string type = Encoding.ASCII.GetString(data, p + 4, 4);
                uint crc = (uint)((data[p + 8 + len] << 24) | (data[p + 9 + len] << 16) | (data[p + 10 + len] << 8) | data[p + 11 + len]);
                if (Crc32(data, p + 4, len + 4) != crc) { a.Add(Sev.Error, "crc", $"Chunk '{type}' fails its CRC check.", p); break; }
                if (type == "IHDR") { w = (data[p + 8] << 24) | (data[p + 9] << 16) | (data[p + 10] << 8) | data[p + 11]; h = (data[p + 12] << 24) | (data[p + 13] << 16) | (data[p + 14] << 8) | data[p + 15]; }
                if (type == "IEND") { iend = true; break; }
                p += 12 + len;
            }
            if (!iend && a.Worst < Sev.Error) a.Add(Sev.Error, "no-iend", "PNG has no IEND chunk (truncated).");
            if (a.Worst < Sev.Error) a.Summary = $"{w}×{h} px";
        }
        if (a.Worst >= Sev.Error) a.Repairs.Add(new RepairOption("delete", "Delete tile", "Map tiles are re-rendered automatically.", true));
        return a;
    }

    public override string Repair(SaveCtx ctx, string rel, string action, JsonElement args)
    {
        if (action == "delete") { ctx.DeleteFile(rel, "delete-png"); return "Deleted " + rel; }
        return base.Repair(ctx, rel, action, args);
    }
}

public sealed class GenericHandler : FormatHandler
{
    public override string Kind => "other";
    public override string Title => "Unknown / binary file";
    public override bool Match(string rel) => true;
    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data); a.Editable = false;
        if (LooksWiped(data, out var why)) a.Add(Sev.Warn, "wiped", "File looks wiped: " + why + ".");
        a.Summary = "No structured editor – use the hex view.";
        return a;
    }
}
