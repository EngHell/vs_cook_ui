using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CookUi.Compat.AculinaryArtillery;

/// <summary>
/// ACA's mixing bowl. Inventory: 0 = input (a pot, or empty), 1 = output, 2-7 = the six
/// ingredient slots. With a pot it mixes meals (ACA mixing recipes, a <see cref="CookingRecipeSource"/>),
/// without one it kneads dough (<see cref="KneadingRecipeSource"/>).
/// </summary>
public sealed class MixingBowlStation : ICookingStation
{
    public const string StationId = "aculinaryartillery:mixingbowl";
    private const int InputSlot = 0;
    private const int FirstIngredientSlot = 2;
    private const int IngredientSlots = 6;

    public string Id => StationId;
    public string Title => Lang.Get("cookui:panel-title-mixing");

    public StationContents? GetContents(GuiDialog dialog)
    {
        if (dialog is not GuiDialogBlockEntity { Inventory: { } inv } || !Aca.IsA(dialog, Aca.MixingBowlDialog)) return null;
        if (inv.Count < FirstIngredientSlot + IngredientSlots) return null;
        var slots = Enumerable.Range(FirstIngredientSlot, IngredientSlots).Select(i => inv[i]).ToArray();
        return new StationContents(inv[InputSlot].Itemstack, slots);
    }
}
