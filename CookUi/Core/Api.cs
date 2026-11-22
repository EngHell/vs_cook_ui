using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace CookUi;

// Public extension API. Another client mod can add a station or recipes like this:
//
//   var cookUi = capi.ModLoader.GetModSystem<CookUi.CookUiModSystem>();
//   cookUi?.RegisterStation(new MyOvenStation());                       // a new dialog to attach to
//   cookUi?.RegisterRecipeSource("myoven", new MyOvenRecipes(capi));    // recipes for it
//   cookUi?.RegisterRecipeSource(CookUiModSystem.FirepitStationId, new MyPotRecipes(capi)); // or for an existing one
//
// Cook UI then shows a panel beside the station's dialog whenever one of its sources applies,
// rebuilds the rows only when the contents change, and handles layout, scrolling, tooltips and
// the title bar buttons. Sources only turn contents into rows.

/// <summary>What a station holds right now.</summary>
/// <param name="Container">The vessel recipes depend on (pot, saucepan...), if any.</param>
/// <param name="Slots">The ingredient slots in grid order; their count sets each row's width.</param>
public record StationContents(ItemStack? Container, IReadOnlyList<ItemSlot> Slots);

/// <summary>A block dialog Cook UI attaches its recipe panel to (firepit, mixing bowl...).</summary>
public interface ICookingStation
{
    /// <summary>Unique id, e.g. "firepit" or "mymod:oven"; recipe sources register against it.</summary>
    string Id { get; }

    /// <summary>Panel title, already localized.</summary>
    string Title { get; }

    /// <summary>
    /// What <paramref name="dialog"/> holds if it is this station's dialog, else null. Called every
    /// tick for each open dialog, so keep it cheap.
    /// </summary>
    StationContents? GetContents(GuiDialog dialog);
}

/// <summary>Turns a station's contents into recipe rows: one kind of recipe (cooking, simmering...).</summary>
public interface IRecipeSource
{
    /// <summary>
    /// Whether this source has anything to say about these contents (a pot rather than a crucible...).
    /// The panel and its buttons only appear while some source applies. Called often; keep it cheap.
    /// </summary>
    bool AppliesTo(StationContents contents);

    /// <summary>One row per recipe the contents can still become. Called only when the contents change.</summary>
    IEnumerable<RecipeRow> GetRows(StationContents contents);

    /// <summary>Text shown when no source returned rows; null for the default message.</summary>
    string? EmptyNote(StationContents contents) => null;
}

/// <summary>How far a station's contents are from making a recipe. Ready rows are listed first.</summary>
public enum PredictionState
{
    /// <summary>Ingredients match; the station will make this. Green border.</summary>
    Ready,
    /// <summary>Everything required is present but amounts are off. Orange border.</summary>
    RatioWrong,
    /// <summary>Matches, but makes more than the container holds. Orange border.</summary>
    TooMuch,
    /// <summary>Current contents fit this recipe; more ingredients are required.</summary>
    Missing,
}

public enum SlotKind
{
    Empty,
    /// <summary>Something actually in the station (or the result): drawn normally.</summary>
    Present,
    /// <summary>Still needed: dark shadow icon, reddish amount.</summary>
    Required,
    /// <summary>Allowed but not needed: fainter shadow icon.</summary>
    Optional,
}

/// <summary>One recipe: its result, then one square per station slot.</summary>
public sealed class RecipeRow
{
    public required RecipeSlot Result;
    public required PredictionState State;
    /// <summary>Up to one entry per station slot; missing ones are drawn empty.</summary>
    public required List<RecipeSlot> Ingredients;
}

/// <summary>One square in a recipe row: an item icon, a small amount label and a hover text.</summary>
public sealed class RecipeSlot
{
    public SlotKind Kind;
    public ItemStack? Icon;
    public string? Tooltip;
    /// <summary>Corner label ("4", "1.5L").</summary>
    public string? Count;
    /// <summary>Amount is wrong; the count is drawn in orange.</summary>
    public bool Warn;
    /// <summary>
    /// The slot accepts several kinds of item: the icon rotates through these instead of showing
    /// <see cref="Icon"/>. Hovering freezes the rotation.
    /// </summary>
    public List<ItemStack>? Cycle;
    /// <summary>Lines listed after <see cref="Tooltip"/>, one per <see cref="Cycle"/> entry; the one shown is highlighted.</summary>
    public List<string>? Categories;
    /// <summary>
    /// The slot accepts any of these: drawn as a 2×2 mosaic of small icons instead of
    /// <see cref="Icon"/>, with "+N" in the last cell when there are more than 4.
    /// </summary>
    public List<RecipeSlot>? Choices;

    public static RecipeSlot Empty() => new() { Kind = SlotKind.Empty };
}
