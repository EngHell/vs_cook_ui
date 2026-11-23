using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CookUi;

/// <summary>Icon and localized label for an ingredient option.</summary>
internal sealed class IngredientInfo
{
    public required ItemStack? Icon;
    /// <summary>Short name: the item's own name for single-item groups, else the group type ("Vegetables").</summary>
    public required string Label;
    /// <summary>
    /// Kinds of item the group accepts ("Vegetables", "Mushrooms", "Pickled vegetables"...),
    /// the one holding <see cref="Icon"/> first. Slots rotate through their icons.
    /// </summary>
    public required List<IngredientCategory> Categories;
}

/// <summary>Accepted items sharing a code prefix (vegetable-*, mushroom-*...), with one icon for all of them.</summary>
internal sealed record IngredientCategory(string Name, ItemStack Icon);

/// <summary>
/// How an ingredient option looks: a representative icon (a carrot for vegetables...), its
/// categories to rotate through, and a localized label from the game's own language files.
/// </summary>
internal static class IngredientIcons
{
    // Keyed by the option's Accepts list, which sources keep stable per ingredient.
    private static readonly ConditionalWeakTable<List<ItemStack>, IngredientInfo> cache = new();

    public static IngredientInfo Describe(IngredientOption option)
    {
        if (cache.TryGetValue(option.Accepts, out var info)) return info;
        var stacks = option.Accepts;
        string? typeName = option.TypeCode;

        ItemStack? icon = IngredientResolver.Representative(stacks);
        var categories = Categories(stacks);

        // Rotation order: the representative's category first, showing the representative itself.
        if (icon != null)
        {
            int at = categories.FindIndex(c => CategoryKey(c.Icon) == CategoryKey(icon));
            if (at >= 0)
            {
                var cat = categories[at];
                categories.RemoveAt(at);
                categories.Insert(0, cat with { Icon = icon });
            }
        }

        string label;
        if (stacks.Count == 1 || string.IsNullOrEmpty(typeName))
        {
            label = icon == null ? "?" : IngredientResolver.SafeName(icon);
            if (stacks.Count > 1) label += " (+" + (stacks.Count - 1) + ")";
        }
        else
        {
            label = IngredientResolver.Capitalize(Lang.GetIfExists("handbook-mealingredient-" + typeName)
                               ?? Lang.GetIfExists("foodcategory-" + typeName)
                               ?? Lang.GetIfExists("cookui:family-" + typeName)
                               ?? typeName);
        }

        info = new IngredientInfo { Icon = icon, Label = label, Categories = categories };
        cache.AddOrUpdate(option.Accepts, info);
        return info;
    }

    /// <summary>
    /// Groups accepted items by the first part of their code (vegetable-carrot and vegetable-onion
    /// are "vegetable", mushroom-* is "mushroom"), in recipe order. A category with one item is
    /// named after it; larger ones use cookui:family-&lt;key&gt;, else "first item (+N)".
    /// </summary>
    private static List<IngredientCategory> Categories(List<ItemStack> stacks)
    {
        var result = new List<IngredientCategory>();
        foreach (var group in stacks.GroupBy(CategoryKey))
        {
            var items = group.ToList();
            var icon = IngredientResolver.Representative(items)!;
            string name = items.Count == 1
                ? IngredientResolver.SafeName(icon)
                : Lang.GetIfExists("cookui:family-" + group.Key) ?? IngredientResolver.SafeName(icon) + " (+" + (items.Count - 1) + ")";
            if (name.Length > 0) result.Add(new IngredientCategory(name, icon));
        }
        return result;
    }

    private static string CategoryKey(ItemStack stack)
    {
        string path = stack.Collectible.Code.Path;
        int dash = path.IndexOf('-');
        return dash < 0 ? path : path[..dash];
    }

}
