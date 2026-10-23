using System.Text;
using System.Xml.Linq;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>players.xml (persistent player data): land claims, bedrolls, dropped backpacks, vending machines – plus ownership transfers.</summary>
public static class Claims
{
    const string File_ = "players.xml";

    public static XDocument Load(SaveCtx c)
    {
        if (!File.Exists(c.Abs(File_))) throw new InvalidOperationException("This save has no players.xml.");
        return XDocument.Parse(XmlHandler.DecodeText(c.ReadAll(File_), out _));
    }

    static void Save(SaveCtx c, XDocument doc, string reason)
    {
        var settings = new System.Xml.XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(true), OmitXmlDeclaration = false };
        using var ms = new MemoryStream();
        using (var xw = System.Xml.XmlWriter.Create(ms, settings)) doc.Save(xw);
        c.WriteFile(File_, ms.ToArray(), reason);
    }

    static int[]? Pos(XElement? e)
    {
        var v = ((string?)e?.Attribute("pos"))?.Split(',');
        return v?.Length == 3 && int.TryParse(v[0].Trim(), out int x) && int.TryParse(v[1].Trim(), out int y) && int.TryParse(v[2].Trim(), out int z) ? new[] { x, y, z } : null;
    }

    static int[]? Position(XElement p)
    {
        var v = ((string?)p.Attribute("position"))?.Split(',');
        return v?.Length == 3 && int.TryParse(v[0].Trim(), out int x) && int.TryParse(v[1].Trim(), out int y) && int.TryParse(v[2].Trim(), out int z) ? new[] { x, y, z } : null;
    }

    public static object List(SaveCtx c)
    {
        var doc = Load(c);
        var ttp = c.ListFilesRel().Where(r => r.StartsWith("Player/", StringComparison.OrdinalIgnoreCase) && r.EndsWith(".ttp", StringComparison.OrdinalIgnoreCase)).ToList();
        var players = doc.Descendants("player").Select(p =>
        {
            var uid = (string?)p.Attribute("userid") ?? "";
            var plat = (string?)p.Attribute("platform") ?? "";
            var file = ttp.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(plat + "_" + uid, StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(f).Equals(uid, StringComparison.OrdinalIgnoreCase));
            return new
            {
                platform = (string?)p.Attribute("platform") ?? "", userId = uid,
                nativePlatform = (string?)p.Attribute("nativeplatform"), nativeUserId = (string?)p.Attribute("nativeuserid"),
                name = (string?)p.Attribute("playername") ?? "", playGroup = (string?)p.Attribute("playgroup"), lastLogin = (string?)p.Attribute("lastlogin"),
                position = Position(p), file,
                claims = p.Elements("lpblock").Select(Pos).Where(x => x != null).ToList(),
                bedroll = Pos(p.Element("bedroll")),
                backpacks = p.Elements("backpack").Select(b => new { id = (string?)b.Attribute("id"), pos = Pos(b) }).ToList(),
                vending = p.Elements("vendingmachinepositions").Elements("position").Select(Pos).Where(x => x != null).ToList(),
            };
        }).ToList();
        return new { players };
    }

    static XElement Find(XDocument doc, string userId) =>
        doc.Descendants("player").FirstOrDefault(p => ((string?)p.Attribute("userid"))?.Equals(userId, StringComparison.OrdinalIgnoreCase) == true) ?? throw new InvalidOperationException("Player " + userId + " is not in players.xml.");

    static bool SamePos(XElement e, int[] p) { var q = Pos(e); return q != null && q[0] == p[0] && q[1] == p[1] && q[2] == p[2]; }

    public static string AddClaim(SaveCtx c, string userId, int[] pos)
    {
        var doc = Load(c); var p = Find(doc, userId);
        if (p.Elements("lpblock").Any(e => SamePos(e, pos))) throw new InvalidOperationException("That claim is already listed for this player.");
        var el = new XElement("lpblock", new XAttribute("pos", $"{pos[0]},{pos[1]},{pos[2]}"));
        var last = p.Elements("lpblock").LastOrDefault();
        if (last != null) last.AddAfterSelf(el); else p.AddFirst(el);
        Save(c, doc, "claim-add");
        string note = "";
        try
        {
            var we = new WorldEdit(c);
            if (we.TryGet(pos[0], pos[1], pos[2], out uint v)) { var nm = c.Game.NameOf(ChunkDoc.TypeOf(v)); if (!nm.Contains("claim", StringComparison.OrdinalIgnoreCase) && !nm.Contains("lpc", StringComparison.OrdinalIgnoreCase)) note = nm.Length == 0 ? " Warning: there is no block at that position (no land claim block)." : $" Warning: the block there is {nm}, not a land claim block."; }
        }
        catch { }
        return $"Added a land claim at {pos[0]}, {pos[1]}, {pos[2]} for {(string?)p.Attribute("playername")}." + note;
    }

    public static string RemoveClaim(SaveCtx c, string userId, int[] pos, bool removeBlock)
    {
        var doc = Load(c); var p = Find(doc, userId);
        var hit = p.Elements("lpblock").Where(e => SamePos(e, pos)).ToList();
        if (hit.Count == 0) throw new InvalidOperationException("That claim is not listed for this player.");
        foreach (var e in hit) e.Remove();
        Save(c, doc, "claim-remove");
        string extra = "";
        if (removeBlock)
        {
            var we = new WorldEdit(c);
            int n = we.Clear(pos[0], pos[1], pos[2]);
            if (n > 0) { we.Commit("claim-block-remove"); extra = " The claim block was removed from the world."; }
        }
        return $"Removed the claim at {pos[0]}, {pos[1]}, {pos[2]}." + extra;
    }

    /// <summary>Move a claim from one player to another in players.xml and set the owner of the claim block's tile entity.</summary>
    public static string TransferClaim(SaveCtx c, string fromUser, string toUser, int[] pos)
    {
        using var batch = c.BeginBatch("claim-transfer");
        var doc = Load(c); var from = Find(doc, fromUser); var to = Find(doc, toUser);
        var hit = from.Elements("lpblock").Where(e => SamePos(e, pos)).ToList();
        if (hit.Count == 0) throw new InvalidOperationException("That claim is not listed for the old owner.");
        foreach (var e in hit) e.Remove();
        var el = new XElement("lpblock", new XAttribute("pos", $"{pos[0]},{pos[1]},{pos[2]}"));
        var last = to.Elements("lpblock").LastOrDefault();
        if (last != null) last.AddAfterSelf(el); else to.AddFirst(el);
        string note = "";
        var we = new WorldEdit(c);
        try
        {
            var d = we.ChunkAt(pos[0], pos[2], true);
            var te = d?.TailParsed == true ? d.TileEntityAt(pos[0] & 15, pos[1], pos[2] & 15) : null;
            if (te is { Composite: true })
            {
                te.SetOwner(new PlatformId { Platform = (string?)to.Attribute("platform") ?? "EOS", UserId = (string?)to.Attribute("userid") ?? "" });
                we.MarkDirty(d!.X, d.Z);
            }
            else note = " (The claim block's tile entity was not found, so only players.xml changed.)";
        }
        catch (Exception ex) { note = " (World not updated: " + ex.Message + ")"; we = new WorldEdit(c); }
        Save(c, doc, "claim-transfer");
        try { we.Commit("claim-transfer"); } catch (Exception ex) { note += " Chunk update failed: " + ex.Message; }
        return $"Claim at {pos[0]}, {pos[1]}, {pos[2]} now belongs to {(string?)to.Attribute("playername")}." + note;
    }

    /// <summary>Hand over every vehicle / drone (and optionally every claim) of one player to another.</summary>
    public static string TransferAssets(SaveCtx c, string fromUser, string toUser, bool vehicles, bool drones, bool claims)
    {
        using var batch = c.BeginBatch("transfer-assets");
        var doc = Load(c); var from = Find(doc, fromUser); var to = Find(doc, toUser);
        var toId = new PlatformId { Platform = (string?)to.Attribute("platform") ?? "EOS", UserId = (string?)to.Attribute("userid") ?? "" };
        var g = c.Game; int nv = 0, nd = 0, nc = 0;
        bool Mine(PlatformId? o) => o != null && o.UserId.Equals(fromUser, StringComparison.OrdinalIgnoreCase);
        if (vehicles && File.Exists(c.Abs("vehicles.dat")))
        {
            var el = EntityListFile.Parse(c.ReadAll("vehicles.dat"), g);
            foreach (var e in el.Entities) { var vd = VehicleDataM.TryParse(e.EntityData, g, out _); if (vd != null && Mine(vd.Owner)) { vd.Owner = toId; e.EntityData = vd.Write(); nv++; } }
            if (nv > 0) c.WriteFile("vehicles.dat", el.Build(g), "transfer-vehicles");
        }
        if (drones && File.Exists(c.Abs("drones.dat")))
        {
            var el = EntityListFile.Parse(c.ReadAll("drones.dat"), g);
            foreach (var e in el.Entities) { var dd = DroneDataM.TryParse(e.EntityData, g, out _); if (dd != null && dd.Parsed && Mine(dd.Owner)) { dd.Owner = toId; if (dd.SyncOwner != null) dd.SyncOwner = toId; e.EntityData = dd.Write(); nd++; } }
            if (nd > 0) c.WriteFile("drones.dat", el.Build(g), "transfer-drones");
        }
        if (claims)
        {
            var poss = from.Elements("lpblock").Select(Pos).Where(x => x != null).Select(x => x!).ToList();
            doc = Load(c); from = Find(doc, fromUser); to = Find(doc, toUser);
            foreach (var p in poss) { nc++; }
            foreach (var e in from.Elements("lpblock").ToList()) { e.Remove(); to.Add(new XElement("lpblock", e.Attributes())); }
            if (nc > 0) { Save(c, doc, "transfer-claims"); var we = new WorldEdit(c); foreach (var p in poss) { try { var d = we.ChunkAt(p![0], p[2], true); var te = d?.TailParsed == true ? d.TileEntityAt(p[0] & 15, p[1], p[2] & 15) : null; if (te is { Composite: true }) { te.SetOwner(toId); we.MarkDirty(d!.X, d.Z); } } catch { } } try { we.Commit("transfer-claims"); } catch { } }
        }
        return $"Transferred {nv} vehicle(s), {nd} drone(s) and {nc} land claim(s) to {(string?)to.Attribute("playername")}.";
    }

    // ───────── characters from another save
    public static object SourcePlayers(string savePath)
    {
        var c = new SaveCtx(savePath); var g = c.Game; var list = new List<object>();
        foreach (var f in c.ListFilesRel().Where(r => r.StartsWith("Player/", StringComparison.OrdinalIgnoreCase) && r.EndsWith(".ttp", StringComparison.OrdinalIgnoreCase)))
            try { var p = PlayerFile.Parse(c.ReadAll(f), g); list.Add(new { rel = f, name = p.Ecd.EntityName, level = p.Skills?.Level, zombieKills = p.ZombieKills, items = p.Inventory.Grid.Items.Count(i => i.Count > 0) + p.Bag.Grid.Items.Count(i => i.Count > 0) + p.Equipment.Grid.Items.Count(i => i.Count > 0) }); }
            catch { }
        return new { name = c.Name, players = list };
    }

    /// <summary>Copy parts of a character from a player file of another save into a player of this save. Items are matched by block / item NAME, unknown ones are dropped.</summary>
    public static string ImportCharacter(SaveCtx target, string sourcePath, string sourceRel, string targetRel, bool items, bool skills, bool stats, bool position)
    {
        var src = new SaveCtx(sourcePath);
        if (Path.GetFullPath(sourcePath).TrimEnd('\\').Equals(target.Root, StringComparison.OrdinalIgnoreCase) && sourceRel.Equals(targetRel, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Source and target are the same character.");
        var ps = PlayerFile.Parse(src.ReadAll(sourceRel), src.Game);
        var pt = PlayerFile.Parse(target.ReadAll(targetRel), target.Game);
        int dropped = 0;
        int Map(int type)
        {
            if (type == 0) return 0;
            var name = src.Game.NameOf(type);
            if (type < GameData.ItemsStartHere) return target.Game.BlockIds.TryGetValue(name, out int b) ? b : -1;
            return target.Game.ItemIds.TryGetValue(name, out int i) ? i : -1;
        }
        ItemVal? Fix(ItemVal? v)
        {
            if (v == null || v.Type == 0) return v;
            int t = Map(v.Type); if (t < 0) { dropped++; return new ItemVal(); }
            v.Type = t;
            if (v.Mods != null) v.Mods = v.Mods.Select(Fix).ToList();
            if (v.Cosmetics != null) v.Cosmetics = v.Cosmetics.Select(Fix).ToList();
            return v;
        }
        void FixGrid(Grid gr) { foreach (var st in gr.Items) { if (st.Count > 0) { st.Item = Fix(st.Item)!; if (st.Item.Type == 0) st.Count = 0; } } if (gr.Prefs != null) { gr.Prefs = null; } }
        if (items)
        {
            FixGrid(ps.Inventory.Grid); FixGrid(ps.Bag.Grid); FixGrid(ps.Equipment.Grid);
            pt.Inventory = ps.Inventory; pt.Bag = ps.Bag; pt.Equipment = ps.Equipment;
        }
        if (skills && ps.Skills != null) { pt.Progression = ps.Progression; pt.Skills = ps.Skills; }
        if (stats) { pt.Ecd.Stats = ps.Ecd.Stats; pt.ZombieKills = ps.ZombieKills; pt.PlayerKills = ps.PlayerKills; pt.Deaths = ps.Deaths; pt.Score = ps.Score; }
        if (position) { pt.Ecd.PosX = ps.Ecd.PosX; pt.Ecd.PosY = ps.Ecd.PosY; pt.Ecd.PosZ = ps.Ecd.PosZ; }
        var bytes = pt.Build(target.Game);
        PlayerFile.Parse(bytes, target.Game);       // verify
        target.WriteFile(targetRel, bytes, "import-character");
        return $"Copied {(items ? "items " : "")}{(skills ? "skills " : "")}{(stats ? "stats " : "")}{(position ? "position " : "")}of {ps.Ecd.EntityName} into {pt.Ecd.EntityName}." + (dropped > 0 ? $" {dropped} item(s) do not exist in this save and were dropped." : "");
    }
}
