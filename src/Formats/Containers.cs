using SaveDoctor.Core;

namespace SaveDoctor.Formats;

/// <summary>Reading and editing the contents of storage tile entities (chests, cupboards, loot containers …) inside chunks.</summary>
public static class ContainerOps
{
    static (ChunkDoc doc, TeRec te, TeFeat feat, StorageData data) Find(WorldEdit we, GameData g, int x, int y, int z, bool forWrite)
    {
        var d = we.ChunkAt(x, z, forWrite) ?? throw new InvalidOperationException("That chunk does not exist in the save.");
        if (!d.TailParsed) throw new InvalidOperationException("This chunk contains tile entities SaveDoctor cannot split safely (" + d.TailNote + ") – its containers are read-only.");
        var te = d.TileEntityAt(x & 15, y, z & 15) ?? throw new InvalidOperationException("There is no tile entity at that position.");
        if (!te.Composite) throw new InvalidOperationException("That tile entity is not a storage container.");
        var f = te.Feats!.FirstOrDefault(f => f.Hash == StorageData.StorageHash) ?? throw new InvalidOperationException("That block has no storage.");
        var sd = StorageData.TryRead(f.Payload, g) ?? throw new InvalidOperationException("The storage data of this block has an unknown layout.");
        return (d, te, f, sd);
    }

    public static object Read(SaveCtx ctx, int x, int y, int z)
    {
        var g = ctx.Game;
        var we = new WorldEdit(ctx);
        var (d, te, _, sd) = Find(we, g, x, y, z, false);
        var names = new Dictionary<int, string>();
        foreach (var st in sd.Grid.Items)
        {
            if (st.Count > 0 && st.Item.Type != 0) names[st.Item.Type] = g.NameOf(st.Item.Type);
            foreach (var m in st.Item.Mods ?? new()) if (m != null && m.Type != 0) names[m.Type] = g.NameOf(m.Type);
        }
        return new
        {
            x, y, z,
            blockId = te.BlockId,
            block = g.NameOf(te.BlockId),
            loot = sd.LootList,
            touched = sd.Grid.TimeTouched != 0,
            playerOwned = sd.Grid.PlayerOwned,
            grid = sd.Grid,
            names,
        };
    }

    public static string Write(SaveCtx ctx, int x, int y, int z, Grid grid, string? loot, bool resetLoot)
    {
        var g = ctx.Game;
        var we = new WorldEdit(ctx);
        var (d, te, feat, sd) = Find(we, g, x, y, z, true);
        if (grid.Items.Count != sd.Grid.Items.Count || grid.Width != sd.Grid.Width || grid.Height != sd.Grid.Height)
            throw new InvalidOperationException($"The container has {sd.Grid.Width}×{sd.Grid.Height} slots – the edited grid has a different size.");
        foreach (var st in grid.Items)
        {
            if (st.Count < 0 || st.Count > 65535) throw new InvalidOperationException("Item count out of range.");
            if (st.Count == 0) st.Item = new ItemVal();
            else if (st.Item.Type == 0) { st.Count = 0; st.Item = new ItemVal(); }
        }
        grid.Locks = sd.Grid.Locks;       // lock flags / preferences are not edited
        grid.Prefs = sd.Grid.Prefs;
        grid.PlayerOwned = sd.Grid.PlayerOwned;
        grid.TimeTouched = sd.Grid.TimeTouched;
        if (resetLoot)
        {
            if (sd.Grid.PlayerOwned) throw new InvalidOperationException("Player-owned containers have no loot to reset.");
            foreach (var st in grid.Items) { st.Count = 0; st.Item = new ItemVal(); }
            grid.TimeTouched = 0;
        }
        else if (grid.TimeTouched == 0)
        {
            // an untouched loot container fills itself when it is first opened – mark it as opened so the edit sticks
            grid.TimeTouched = Math.Max(1UL, WorldTime(ctx));
        }
        sd.Grid = grid;
        if (loot != null) sd.LootList = string.IsNullOrWhiteSpace(loot) ? null : loot;
        var payload = sd.Write();
        if (StorageData.TryRead(payload, g) == null) throw new InvalidOperationException("Internal check failed – the edited storage could not be read back.");
        feat.Payload = payload;
        te.Dirty = true;
        we.MarkDirty(d.X, d.Z);
        we.BlocksChanged += 0;
        return we.Commit("edit-container").Replace("0 block(s) changed in", "Container saved –");
    }

    public static ulong WorldTime(SaveCtx ctx)
    {
        try { return WorldFile.Parse(ctx.ReadAll("main.ttw")).WorldTime; } catch { return 0; }
    }

    /// <summary>Empty every non-player container (optionally only inside a box) and mark it untouched so the game rolls fresh loot when it is next opened.</summary>
    public static string ResetLoot(SaveCtx ctx, int x1, int z1, int x2, int z2, bool onlyUntouchedBlocks)
    {
        var g = ctx.Game;
        var we = new WorldEdit(ctx);
        int cx1 = x1 >> 4, cx2 = x2 >> 4, cz1 = z1 >> 4, cz2 = z2 >> 4;
        int reset = 0, skipped = 0, chunks = 0;
        for (int cz = cz1; cz <= cz2; cz++)
            for (int cx = cx1; cx <= cx2; cx++)
            {
                var d = we.Chunk(cx, cz, true);
                if (d == null) continue;
                if (!d.TailParsed) { skipped++; continue; }
                bool touchedChunk = false;
                foreach (var te in d.Tes)
                {
                    if (!te.Composite) continue;
                    int wx = d.X * 16 + te.X, wz = d.Z * 16 + te.Z;
                    if (wx < x1 || wx > x2 || wz < z1 || wz > z2) continue;
                    var f = te.Feats!.FirstOrDefault(f => f.Hash == StorageData.StorageHash);
                    if (f == null) continue;
                    var sd = StorageData.TryRead(f.Payload, g);
                    if (sd == null || sd.Grid.PlayerOwned || string.IsNullOrEmpty(sd.LootList)) continue;
                    foreach (var st in sd.Grid.Items) { st.Count = 0; st.Item = new ItemVal(); }
                    sd.Grid.TimeTouched = 0;
                    f.Payload = sd.Write();
                    te.Dirty = true; touchedChunk = true; reset++;
                }
                if (touchedChunk) { we.MarkDirty(cx, cz); chunks++; }
            }
        if (reset == 0) return "No loot containers found in that area." + (skipped > 0 ? $" ({skipped} chunk(s) skipped because their tile entities cannot be split.)" : "");
        we.Commit("reset-loot");
        return $"Reset {reset} loot container(s) in {chunks} chunk(s). The game rolls new loot when each one is opened." + (skipped > 0 ? $" {skipped} chunk(s) were skipped (unsupported tile entities)." : "");
    }
}
