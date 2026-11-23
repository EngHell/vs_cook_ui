using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace CookUi;

/// <summary>Amount and number formatting for the panel.</summary>
internal static class RecipeFormat
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
}
