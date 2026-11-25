using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CookUi.Compat.ElectricalProgressive;

/// <summary>
/// Electrical Progressive: QoL's electric stove. Its inventory is a vanilla <see cref="InventorySmelting"/>
/// (pot slot plus cooking slots) and it cooks through the same DoSmelt path as the firepit, so the
/// firepit's recipe sources apply unchanged. Recognized by type name: we don't reference its DLL.
/// </summary>
public sealed class EStoveStation : ICookingStation
{
    public const string ModId = "electricalprogressiveqol";
    public const string StationId = "electricalprogressiveqol:estove";
    private const string DialogType = "ElectricalProgressive.Content.Block.EStove.GuiDialogBlockEntityEStove";

    public string Id => StationId;
    public string Title => Lang.Get("cookui:panel-title");

    public StationContents? GetContents(GuiDialog dialog)
    {
        if (dialog is not GuiDialogBlockEntity { Inventory: InventorySmelting inv } || !inv.HaveCookingContainer) return null;
        if (!IsA(dialog, DialogType)) return null;
        return new StationContents(inv[1].Itemstack, inv.CookingSlots);
    }

    private static bool IsA(object obj, string fullName)
    {
        for (var t = obj.GetType(); t != null; t = t.BaseType)
        {
            if (t.FullName == fullName) return true;
        }
        return false;
    }
}
