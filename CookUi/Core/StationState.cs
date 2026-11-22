using System.Collections.Generic;
using Vintagestory.API.Common;

namespace CookUi;

/// <summary>
/// What a station (pot in a firepit, mixing bowl, saucepan...) holds right now.
/// </summary>
/// <param name="Container">The vessel the recipes depend on (the pot, the saucepan...), if any.</param>
/// <param name="Slots">The ingredient slots, in grid order; their count sets the width of each row.</param>
public record StationState(ItemStack? Container, IReadOnlyList<ItemSlot> Slots);

/// <summary>What a window shows: one row per recipe, each <see cref="SlotCount"/> ingredient slots wide.</summary>
/// <param name="EmptyNote">Shown instead of the rows when there are none.</param>
public sealed record RecipeGrid(int SlotCount, List<RowView> Rows, string EmptyNote);

/// <summary>How far a station's contents are from making a recipe.</summary>
public enum PredictionState
{
    /// <summary>Ingredients match; the station will make this.</summary>
    Ready,
    /// <summary>Everything required is present but quantities are uneven.</summary>
    RatioWrong,
    /// <summary>Matches, but makes more servings than the container holds.</summary>
    TooMuch,
    /// <summary>Current contents fit this recipe; more ingredients are required.</summary>
    Missing,
}
