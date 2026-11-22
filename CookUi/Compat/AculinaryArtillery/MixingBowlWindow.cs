using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace CookUi.Compat.AculinaryArtillery;

/// <summary>
/// Layout of ACA's mixing bowl inventory: 0 = input (a pot, or empty), 1 = output,
/// 2-7 = the six ingredient slots. With a pot it mixes meals (<see cref="MixingBowlWindow"/>),
/// without one it kneads dough (<see cref="KneadingWindow"/>).
/// </summary>
internal static class MixingBowl
{
    public const int InputSlot = 0;
    public const int FirstIngredientSlot = 2;
    public const int IngredientSlots = 6;

    /// <summary>The bowl's six ingredient slots, or null if this isn't a mixing bowl inventory.</summary>
    public static ItemSlot[]? Ingredients(InventoryBase? inv) =>
        inv == null || inv.Count < FirstIngredientSlot + IngredientSlots
            ? null
            : Enumerable.Range(FirstIngredientSlot, IngredientSlots).Select(i => inv[i]).ToArray();
}

/// <summary>
/// ACA mixing bowl with a cooking pot in its input slot: the ingredients mix into a meal using
/// ACA's mixing recipes, which are ordinary cooking recipes.
/// </summary>
public sealed class MixingBowlWindow : CookingRecipeWindow<GuiDialogBlockEntity>
{
    public MixingBowlWindow(ICoreClientAPI capi, CookUiConfig config)
        : base(capi, config, new AcaMixingRecipeProvider(capi)) { }

    public override string Id => "aca-mixingbowl";
    protected override string TitleLangKey => "cookui:panel-title-mixing";

    protected override bool IsHost(GuiDialogBlockEntity dlg) => Aca.IsA(dlg, Aca.MixingBowlDialog);

    protected override CookingStationState? GetState(GuiDialogBlockEntity host)
    {
        var slots = MixingBowl.Ingredients(host.Inventory);
        if (slots == null) return null;
        var container = host.Inventory[MixingBowl.InputSlot].Itemstack;
        if (container?.Collectible is not BlockCookingContainer pot) return null;
        // The bowl doesn't care whether the pot is dirty (BlockEntityMixingBowl.GetMatchingMixingRecipe).
        return new CookingStationState(container, slots, pot.MaxServingSize, false);
    }
}
