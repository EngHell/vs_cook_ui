using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace CookUi.Compat.AculinaryArtillery;

/// <summary>
/// ACA mixing bowl with a cooking pot in its input slot: 6 ingredient slots (inventory 2-7)
/// that mix into a meal using ACA's mixing recipes, which are ordinary cooking recipes.
/// Without a pot the bowl kneads dough instead; that isn't covered.
/// </summary>
public sealed class MixingBowlWindow : CookingRecipeWindow<GuiDialogBlockEntity>
{
    private const int PotSlot = 0;
    private const int FirstIngredientSlot = 2;
    private const int IngredientSlots = 6;

    public MixingBowlWindow(ICoreClientAPI capi, CookUiConfig config)
        : base(capi, config, new AcaMixingRecipeProvider(capi)) { }

    public override string Id => "aca-mixingbowl";
    protected override string TitleLangKey => "cookui:panel-title-mixing";

    protected override bool IsHost(GuiDialogBlockEntity dlg) => Aca.IsA(dlg, Aca.MixingBowlDialog);

    protected override CookingStationState? GetState(GuiDialogBlockEntity host)
    {
        var inv = host.Inventory;
        if (inv == null || inv.Count < FirstIngredientSlot + IngredientSlots) return null;
        var container = inv[PotSlot].Itemstack;
        if (container?.Collectible is not BlockCookingContainer pot) return null;
        var slots = Enumerable.Range(FirstIngredientSlot, IngredientSlots).Select(i => inv[i]).ToArray();
        // The bowl doesn't care whether the pot is dirty (BlockEntityMixingBowl.GetMatchingMixingRecipe).
        return new CookingStationState(container, slots, pot.MaxServingSize, false);
    }
}
