using SaveDoctor.Core;

namespace SaveDoctor.Formats;

public sealed partial class RecipeQueueM
{
    /// <summary>One RecipeQueueItem (player crafting queue and workstation queues share the layout).</summary>
    public static RecipeQueueM Read(BinaryReader r, GameData g)
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
        return q;
    }

    public void Write(BinaryWriter w)
    {
        w.Write((ushort)2); w.Write((short)Multiplier); w.Write(IsCrafting); w.Write(TimeLeft);
        w.Write(HasRepair);
        if (HasRepair) { if (RepairItem == null) w.Write((byte)0); else RepairItem.Write(w); w.Write((ushort)AmountToRepair); }
        w.Write((byte)Quality); w.Write(StartingEntityId); w.Write(OneItemCraftTime);
        w.Write(HasRecipe);
        if (HasRecipe)
        {
            w.Write((ushort)1);
            w.Write(RecipeItemType); w.Write(RecipeCount); w.Write(RecipeIsScrap); w.Write(RecipeCraftTime); w.Write(RecipeExp); w.Str(RecipeArea);
            w.Write(Ingredients.Count);
            foreach (var s in Ingredients) s.Write(w);
        }
    }
}
