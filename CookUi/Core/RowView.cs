using System.Collections.Generic;
using Vintagestory.API.Common;

namespace CookUi;

// What the panel draws. Built only by RowBuilder from sources' RecipeMatch data.

internal enum SlotKind { Empty, Present, Required, Optional }

/// <summary>One square in a recipe row: an item icon, a small amount label and a hover text.</summary>
internal sealed class SlotView
{
    public SlotKind Kind;
    public ItemStack? Icon;
    public string? Tooltip;
    /// <summary>Lines listed after <see cref="Tooltip"/>, one per <see cref="Cycle"/> entry; the shown one is highlighted.</summary>
    public List<string>? Categories;
    public string? Count;
    /// <summary>Amount is wrong; count is drawn in orange.</summary>
    public bool Warn;
    /// <summary>Drawn as a 2×2 mosaic of these instead of <see cref="Icon"/>, "+N" in the last cell past 4.</summary>
    public List<SlotView>? Choices;
    /// <summary>The icon rotates through these instead of always showing <see cref="Icon"/>.</summary>
    public List<ItemStack>? Cycle;

    public static SlotView Empty() => new() { Kind = SlotKind.Empty };
}

internal sealed class RowView
{
    public required SlotView Result;
    public required MatchState State;
    public required List<SlotView> Ingredients;
}
