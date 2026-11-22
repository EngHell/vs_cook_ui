using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CookUi;

/// <summary>Formatting helpers for recipe sources, so every station's rows read the same.</summary>
public static class RecipeFormat
{
    public static bool IsLiquid(ItemStack stack) => BlockLiquidContainerBase.GetContainableProps(stack) != null;

    /// <summary>"3" for items, "1.5L" for liquids.</summary>
    public static string AmountLabel(ItemStack stack)
    {
        var props = BlockLiquidContainerBase.GetContainableProps(stack);
        if (props != null && props.ItemsPerLitre > 0) return Litres(stack.StackSize / props.ItemsPerLitre);
        return stack.StackSize.ToString();
    }

    /// <summary><paramref name="quantity"/> items of <paramref name="like"/>'s kind, as "3" or "1.5L".</summary>
    public static string AmountOf(ItemStack like, int quantity)
    {
        var s = like.Clone();
        s.StackSize = quantity;
        return AmountLabel(s);
    }

    public static string Litres(float l) => l.ToString("0.##", CultureInfo.CurrentCulture) + "L";

    public static string Round(float v) => Math.Round(v).ToString(CultureInfo.CurrentCulture);

    public static string Range(float min, float max) =>
        Math.Round(min) == Math.Round(max) ? Round(min) : Round(min) + "–" + Round(max);

    /// <summary>Non-empty stacks of <paramref name="slots"/>, in order.</summary>
    public static ItemStack[] Stacks(IEnumerable<ItemSlot> slots) =>
        slots.Select(s => s.Itemstack).Where(s => s != null).Cast<ItemStack>().ToArray();

    /// <summary>Status line for a result tooltip, in servings.</summary>
    public static string Status(PredictionState state, int servings, int missing) => state switch
    {
        PredictionState.Ready => Lang.Get("cookui:servings", servings),
        PredictionState.TooMuch => Lang.Get("cookui:too-much", servings),
        PredictionState.RatioWrong => Lang.Get("cookui:ratio-wrong"),
        _ => Lang.Get("cookui:needs-more", missing),
    };

    /// <summary>Ready first, then wrong amounts, then by how much is missing (stable for equal keys).</summary>
    public static int Rank(PredictionState state) => state switch
    {
        PredictionState.Ready => 0,
        PredictionState.TooMuch or PredictionState.RatioWrong => 1,
        _ => 2,
    };
}
