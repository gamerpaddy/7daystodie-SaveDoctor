using System.Text.RegularExpressions;
using System.Text.Json;
using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed class PlayerDataHandler : FormatHandler
{
    public override string Kind => "player";
    public override string Title => "Player data";
    static readonly Regex Rx = new(@"^Player/[^/]+\.ttp$", RegexOptions.IgnoreCase);
    public override bool Match(string rel) => Rx.IsMatch(rel);

    public override Analysis Analyze(SaveCtx ctx, string rel, byte[] data)
    {
        var a = NewAnalysis(rel, data); a.Editable = true;
        if (LooksWiped(data, out var why))
        {
            a.Add(Sev.Error, "wiped", "This player file is unusable: " + why + ".");
            return a;
        }
        PlayerFile p;
        try { p = PlayerFile.Parse(data, ctx.Game); }
        catch (ParseFail pf)
        {
            a.Editable = false;
            return a.Add(Sev.Error, "parse", $"Player data is damaged or unreadable: {pf.Message}" + (pf.Offset >= 0 ? $" (offset 0x{pf.Offset:X})" : ""), pf.Offset >= 0 ? pf.Offset : null);
        }
        catch (EndOfStreamException)
        {
            a.Editable = false;
            return a.Add(Sev.Error, "truncated", "The player file ends too early – it was cut off while being written.");
        }
        catch (Exception ex)
        {
            a.Editable = false;
            return a.Add(Sev.Error, "parse", "Player data cannot be read: " + ex.Message);
        }

        var e = p.Ecd;
        a.Summary = $"{(string.IsNullOrEmpty(e.EntityName) ? "(unnamed)" : e.EntityName)} · id {p.Id} · pos {e.PosX:0},{e.PosY:0},{e.PosZ:0} · {p.ZombieKills} zombie kills · {p.Deaths} deaths";
        a.Info["name"] = e.EntityName; a.Info["id"] = p.Id;
        a.Info["position"] = new[] { e.PosX, e.PosY, e.PosZ };
        if (p.Skills != null) { a.Info["level"] = p.Skills.Level; a.Summary += $" · level {p.Skills.Level}"; }

        if (p.TrailingBytes > 0) a.Add(Sev.Warn, "trailing", $"{p.TrailingBytes} unexpected bytes after the end of the player data.", data.Length - p.TrailingBytes);

        // fidelity check: our writer must reproduce the file
        try
        {
            var rebuilt = p.Build(ctx.Game);
            long diff = BinUtil.FirstDiff(data, rebuilt);
            if (diff >= 0 && p.TrailingBytes == 0)
            {
                a.Add(Sev.Info, "roundtrip", $"The editor re-encodes this file slightly differently (first difference at 0x{diff:X}). Saving will normalise it; the game accepts that.", diff);
            }
            else a.Info["roundtrip"] = "exact";
        }
        catch (Exception ex) { a.Add(Sev.Warn, "rebuild", "Editor could not re-encode the file: " + ex.Message); a.Editable = false; }

        // semantic sanity
        foreach (var (name, v) in new[] { ("X", e.PosX), ("Y", e.PosY), ("Z", e.PosZ) })
            if (!float.IsFinite(v)) a.Add(Sev.Error, "pos-nan", $"Player position {name} is NaN/Infinity – the player can never spawn properly.");
        if (float.IsFinite(e.PosY) && (e.PosY < -100 || e.PosY > 1000)) a.Add(Sev.Warn, "pos-range", $"Player Y position {e.PosY:0.#} is outside the world.");
        if (Math.Abs(e.PosX) > 100000 || Math.Abs(e.PosZ) > 100000) a.Add(Sev.Warn, "pos-range", $"Player position {e.PosX:0},{e.PosZ:0} is far outside any world.");
        if (e.Stats != null)
        {
            Chk(a, "Health", e.Stats.Health); Chk(a, "Stamina", e.Stats.Stamina); Chk(a, "Water", e.Stats.Water); Chk(a, "Food", e.Stats.Food);
        }
        if (p.Id <= 0) a.Add(Sev.Warn, "entity-id", $"Entity id is {p.Id}.");
        if (e.Id != p.Id) a.Add(Sev.Info, "entity-id", $"Entity id inside the entity block ({e.Id}) differs from the player id ({p.Id}).");

        // items
        var unknown = new SortedSet<int>();
        int items = 0, badCounts = 0;
        void Scan(IEnumerable<Stack> stacks)
        {
            foreach (var s in stacks)
            {
                if (s.Count <= 0 || s.Item.Type == 0) continue;
                items++;
                WalkItem(s.Item);
                if (s.Count > 30000) badCounts++;
            }
        }
        void WalkItem(ItemVal it)
        {
            if (it.Type != 0 && !KnownType(ctx.Game, it.Type)) unknown.Add(it.Type);
            if (it.Mods != null) foreach (var m in it.Mods) if (m != null) WalkItem(m);
            if (it.Cosmetics != null) foreach (var m in it.Cosmetics) if (m != null) WalkItem(m);
            if (!float.IsFinite(it.UseTimes)) a.Add(Sev.Error, "item-nan", $"Item {ctx.Game.NameOf(it.Type)} has a NaN durability value.");
        }
        Scan(p.Inventory.Grid.Items); Scan(p.Bag.Grid.Items); Scan(p.Equipment.Grid.Items); Scan(p.DragAndDrop);
        a.Info["items"] = items;
        if (badCounts > 0) a.Add(Sev.Warn, "stack-size", $"{badCounts} stacks hold more than 30000 items.");
        if (unknown.Count > 0 && ctx.Game.ItemNames.Count > 0)
        {
            bool running = SaveCtx.IsServerRunning(out _);
            a.Info["unknownItems"] = unknown.ToList();
            if (running)
                a.Add(Sev.Info, "unknown-items", $"{unknown.Count} item id(s) are not yet in itemmappings.nim ({string.Join(", ", unknown.Take(10))}). That is normal while the game runs – it appends new ids to the table only when it saves it.");
            else
            {
                a.Add(Sev.Warn, "unknown-items", $"{unknown.Count} item type(s) are not known to the save's id tables (removed mod, or itemmappings.nim is older than this player file): " + string.Join(", ", unknown.Take(10)));
                a.Repairs.Add(new RepairOption("removeUnknownItems", "Remove items with unknown ids", "Deletes inventory/backpack/equipment stacks whose item id is not defined (they can crash or vanish on load).", true));
            }
        }

        // quests from removed mods
        if (ctx.Game.HasQuests)
        {
            var missing = p.Quests.Quests.Where(q => !ctx.Game.QuestIds.Contains(q.Id)).Select(q => q.Id).Distinct().ToList();
            if (missing.Count > 0)
            {
                a.Add(Sev.Info, "unknown-quests", $"{missing.Count} saved quest(s) are no longer defined (mod removed?): {string.Join(", ", missing.Take(8))}. The game logs an error and ignores them.");
                a.Repairs.Add(new RepairOption("removeUnknownQuests", "Remove quests that no longer exist", "Removes quests the server cannot load anyway.", false));
            }
        }
        a.Info["quests"] = p.Quests.Quests.Count;

        var meta = ctx.Abs(rel + ".meta");
        if (!File.Exists(meta)) a.Add(Sev.Info, "meta-missing", "The .ttp.meta file is missing (the game recreates it on next save).");

        if (a.Worst >= Sev.Error && e.Stats != null)
            a.Repairs.Add(new RepairOption("fixStats", "Repair invalid stats / position", "Replaces NaN or impossible health, stamina, food, water and position values with safe ones.", false));
        return a;
    }

    static bool KnownType(GameData g, int type)
    {
        if (type >= GameData.ItemsStartHere) return g.ItemNames.Count == 0 || g.ItemNames.ContainsKey(type);
        return g.BlockNames.Count == 0 || g.BlockNames.ContainsKey(type);
    }

    static void Chk(Analysis a, string name, StatM? s)
    {
        if (s == null) return;
        if (!float.IsFinite(s.Value) || !float.IsFinite(s.BaseMax) || !float.IsFinite(s.OriginalBaseMax) || !float.IsFinite(s.MaxModifier))
            a.Add(Sev.Error, "stat-nan", $"{name} contains NaN/Infinity.");
        else if (s.Value < 0 || s.BaseMax < 0) a.Add(Sev.Warn, "stat-neg", $"{name} is negative ({s.Value:0.#}/{s.BaseMax:0.#}).");
        else if (s.Value > s.BaseMax * 5 + 1000) a.Add(Sev.Warn, "stat-big", $"{name} value {s.Value:0.#} is far above its maximum {s.BaseMax:0.#}.");
    }

    public override object? LoadModel(SaveCtx ctx, string rel, byte[] data)
    {
        var p = PlayerFile.Parse(data, ctx.Game);
        var names = new Dictionary<int, string>();
        void Collect(ItemVal? it)
        {
            if (it == null || it.Type == 0) return;
            names[it.Type] = ctx.Game.NameOf(it.Type);
            if (it.Mods != null) foreach (var m in it.Mods) Collect(m);
            if (it.Cosmetics != null) foreach (var m in it.Cosmetics) Collect(m);
        }
        foreach (var s in p.Inventory.Grid.Items.Concat(p.Bag.Grid.Items).Concat(p.Equipment.Grid.Items).Concat(p.DragAndDrop)) Collect(s.Item);
        return new
        {
            Player = p,
            Names = names.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            EntityClass = ctx.Game.EntityClassName(p.Ecd.EntityClass),
            Mods = ctx.Game.ModifierItemNames.Count,
        };
    }

    public override byte[] BuildFromModel(SaveCtx ctx, string rel, byte[] original, JsonElement model)
    {
        var pe = model.TryGetProperty("player", out var x) ? x : model;
        var p = Json.De<PlayerFile>(pe) ?? throw new InvalidOperationException("Empty model");
        var bytes = p.Build(ctx.Game);
        // verify what we produce can be read back by our parser (which mirrors the game's reader)
        PlayerFile.Parse(bytes, ctx.Game);
        return bytes;
    }

    public override string Repair(SaveCtx ctx, string rel, string action, JsonElement args)
    {
        var data = ctx.ReadAll(rel);
        var p = PlayerFile.Parse(data, ctx.Game);
        string msg;
        switch (action)
        {
            case "removeUnknownItems":
                {
                    int removed = 0;
                    void Clean(Grid gr)
                    {
                        foreach (var s in gr.Items)
                            if (s.Count > 0 && s.Item.Type != 0 && !KnownType(ctx.Game, s.Item.Type)) { s.Count = 0; s.Item = new ItemVal(); removed++; }
                    }
                    Clean(p.Inventory.Grid); Clean(p.Bag.Grid); Clean(p.Equipment.Grid);
                    foreach (var s in p.DragAndDrop) if (s.Count > 0 && !KnownType(ctx.Game, s.Item.Type)) { s.Count = 0; s.Item = new ItemVal(); removed++; }
                    msg = $"Removed {removed} stacks with unknown item ids.";
                    break;
                }
            case "removeUnknownQuests":
                {
                    int n = p.Quests.Quests.RemoveAll(q => !ctx.Game.QuestIds.Contains(q.Id));
                    msg = $"Removed {n} unknown quests.";
                    break;
                }
            case "fixStats":
                {
                    var e = p.Ecd;
                    void Fix(StatM? s, float def)
                    {
                        if (s == null) return;
                        if (!float.IsFinite(s.BaseMax) || s.BaseMax <= 0) s.BaseMax = def;
                        if (!float.IsFinite(s.OriginalBaseMax) || s.OriginalBaseMax <= 0) s.OriginalBaseMax = s.BaseMax;
                        if (!float.IsFinite(s.MaxModifier)) s.MaxModifier = 0;
                        if (!float.IsFinite(s.Value) || s.Value < 0) s.Value = s.BaseMax;
                        if (!float.IsFinite(s.OriginalValue)) s.OriginalValue = s.Value;
                    }
                    if (e.Stats != null) { Fix(e.Stats.Health, 100); Fix(e.Stats.Stamina, 100); Fix(e.Stats.Water, 100); Fix(e.Stats.Food, 100); }
                    if (!float.IsFinite(e.PosX) || !float.IsFinite(e.PosY) || !float.IsFinite(e.PosZ) || e.PosY < -100 || e.PosY > 1000)
                    {
                        // fall back to the last spawn position, otherwise the world origin at a safe height
                        if (p.LastSpawnPos.Any(v => v != 0)) { e.PosX = p.LastSpawnPos[0]; e.PosY = p.LastSpawnPos[1] + 2; e.PosZ = p.LastSpawnPos[2]; }
                        else { e.PosX = 0; e.PosY = 100; e.PosZ = 0; }
                    }
                    msg = "Stats and position were reset to safe values.";
                    break;
                }
            default:
                return base.Repair(ctx, rel, action, args);
        }
        var bytes = p.Build(ctx.Game);
        PlayerFile.Parse(bytes, ctx.Game);
        ctx.WriteFile(rel, bytes, "player-" + action);
        return msg;
    }
}
