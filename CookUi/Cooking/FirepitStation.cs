using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CookUi;

/// <summary>
/// A firepit holding a container with cooking slots (pot, ACA saucepan, crucible...). Which
/// containers get a panel is up to the recipe sources: the vanilla source answers for pots.
/// </summary>
public sealed class FirepitStation : ICookingStation
{
    public string Id => CookUiModSystem.FirepitStationId;
    public string Title => Lang.Get("cookui:panel-title");

    public StationContents? GetContents(GuiDialog dialog)
    {
        if (dialog is not GuiDialogBlockEntityFirepit { Inventory: InventorySmelting inv } || !inv.HaveCookingContainer) return null;
        return new StationContents(inv[1].Itemstack, inv.CookingSlots);
    }
}
