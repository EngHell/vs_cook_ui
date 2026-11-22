using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace CookUi;

/// <summary>Vanilla's cooking recipes, as synced from the server.</summary>
public sealed class VanillaCookingRecipeProvider : IRecipeProvider<CookingRecipe>
{
    private readonly ICoreClientAPI capi;

    public VanillaCookingRecipeProvider(ICoreClientAPI capi) => this.capi = capi;

    public IEnumerable<CookingRecipe> GetRecipes() => capi.GetCookingRecipes() ?? new List<CookingRecipe>();
}

/// <summary>Cooking pot (or any <see cref="BlockCookingContainer"/>) sitting in a firepit.</summary>
public sealed class FirepitPotWindow : CookingRecipeWindow<GuiDialogBlockEntityFirepit>
{
    public FirepitPotWindow(ICoreClientAPI capi, CookUiConfig config)
        : base(capi, config, new VanillaCookingRecipeProvider(capi)) { }

    public override string Id => "firepit-pot";
    protected override string TitleLangKey => "cookui:panel-title";

    protected override CookingStationState? GetState(GuiDialogBlockEntityFirepit host)
    {
        if (host.Inventory is not InventorySmelting inv || !inv.HaveCookingContainer) return null;
        var container = inv[1].Itemstack;
        if (container?.Collectible is not BlockCookingContainer pot) return null;
        bool dirty = pot.Attributes?["isDirtyPot"].AsBool() == true;
        return new CookingStationState(container, inv.CookingSlots, pot.MaxServingSize, dirty);
    }
}
