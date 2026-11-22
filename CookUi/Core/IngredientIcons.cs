using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CookUi;

/// <summary>Icon and localized label for one recipe ingredient group.</summary>
public sealed class IngredientInfo
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
public sealed record IngredientCategory(string Name, ItemStack Icon);

/// <summary>
/// Resolves ingredient groups (including wildcard codes such as "fruit-*") to concrete stacks,
/// and picks a representative icon: a carrot for vegetables, an apple for fruit, a drumstick for meat...
/// All names come from the game's own language files, so they follow the selected language.
/// </summary>
public class IngredientIcons
{
    /// <summary>Preferred icons, first match wins. Wildcards allowed.</summary>
    private static readonly string[] Representatives =
    {
        "vegetable-carrot",
        "fruit-redapple",
        "poultry-raw",
        "redmeat-raw",
        "fish-raw", "fishraw-*", "fish*",
        "egg-chicken-raw",
        "grain-spelt",
        "mushroom-fieldmushroom-normal",
        "pickledvegetable-carrot",
        "dough-spelt",
    };

    private readonly IWorldAccessor world;
    // Keyed by ingredient object (CookingRecipeIngredient or CraftingRecipeIngredient).
    private readonly Dictionary<object, IngredientInfo> cache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, List<ItemStack>> stacksCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CookingRecipeIngredient, (float min, float max)?> satietyCache = new(ReferenceEqualityComparer.Instance);

    public IngredientIcons(IWorldAccessor world) => this.world = world;

    public IngredientInfo Get(CookingRecipeIngredient ing) => Get(ing, Stacks(ing), ing.TypeName);

    /// <summary>Same for a generic recipe ingredient (simmering, kneading... recipes).</summary>
    public IngredientInfo Get(CraftingRecipeIngredient ing) => Get(ing, Stacks(ing), ing.Name);

    private IngredientInfo Get(object key, List<ItemStack> stacks, string? typeName)
    {
        if (cache.TryGetValue(key, out var info)) return info;

        ItemStack? icon = PickRepresentative(stacks);
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
            label = icon == null ? "?" : SafeName(icon);
            if (stacks.Count > 1) label += " (+" + (stacks.Count - 1) + ")";
        }
        else
        {
            label = Capitalize(Lang.GetIfExists("handbook-mealingredient-" + typeName)
                               ?? Lang.GetIfExists("foodcategory-" + typeName)
                               ?? Lang.GetIfExists("cookui:family-" + typeName)
                               ?? typeName);
        }

        info = new IngredientInfo { Icon = icon, Label = label, Categories = categories };
        cache[key] = info;
        return info;
    }

    /// <summary>All concrete stacks the group accepts, wildcards expanded.</summary>
    public List<ItemStack> Stacks(CookingRecipeIngredient ing)
    {
        if (stacksCache.TryGetValue(ing, out var list)) return list;
        list = new List<ItemStack>();
        var seen = new HashSet<AssetLocation>();

        foreach (var vs in ing.ValidStacks)
        {
            if (vs.Code == null) continue;
            if (vs.Code.Path.Contains('*'))
            {
                // Vanilla matches wildcards by code only, ignoring the declared type: soup lists
                // "mushroom-*" as an item, but mushrooms are blocks.
                foreach (var c in world.Collectibles) // items and blocks
                {
                    if (c?.Code == null || !c.WildCardMatch(vs.Code)) continue;
                    if (seen.Add(c.Code)) list.Add(new ItemStack(c));
                }
            }
            else if (vs.ResolvedItemstack is { } rs && seen.Add(rs.Collectible.Code))
            {
                var s = rs.Clone();
                s.StackSize = 1;
                list.Add(s);
            }
        }

        stacksCache[ing] = list;
        return list;
    }

    /// <summary>All concrete stacks a generic recipe ingredient accepts, wildcards expanded (variant filters respected).</summary>
    public List<ItemStack> Stacks(CraftingRecipeIngredient ing)
    {
        if (stacksCache.TryGetValue(ing, out var list)) return list;
        list = new List<ItemStack>();

        if (ing.MatchingType != EnumRecipeMatchType.Exact)
        {
            // Wildcards, regexes, tags...: let the ingredient judge every collectible (cached).
            var seen = new HashSet<AssetLocation>();
            foreach (var c in world.Collectibles)
            {
                if (c?.Code == null) continue;
                var stack = new ItemStack(c);
                bool ok;
                try { ok = ing.SatisfiesAsIngredient(stack, false); }
                catch (Exception) { ok = false; }
                if (ok && seen.Add(c.Code)) list.Add(stack);
            }
        }
        else if (ing.ResolvedItemStack is { } rs)
        {
            var s = rs.Clone();
            s.StackSize = 1;
            list.Add(s);
        }

        stacksCache[ing] = list;
        return list;
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
            var icon = PickRepresentative(items)!;
            string name = items.Count == 1
                ? SafeName(icon)
                : Lang.GetIfExists("cookui:family-" + group.Key) ?? SafeName(icon) + " (+" + (items.Count - 1) + ")";
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

    /// <summary>
    /// Lowest and highest per-serving satiety any accepted item would add to the meal
    /// (fresh, before spoilage), or null if none of them is food.
    /// </summary>
    public (float min, float max)? SatietyRange(CookingRecipeIngredient ing, EntityAgent? entity)
    {
        if (satietyCache.TryGetValue(ing, out var range)) return range;
        var values = Stacks(ing)
            .Select(s => MealNutrition(world, s, ing, entity)?.Satiety)
            .Where(v => v != null).Select(v => v!.Value).ToList();
        range = values.Count == 0 ? null : (values.Min(), values.Max());
        satietyCache[ing] = range;
        return range;
    }

    /// <summary>
    /// What one serving's worth of <paramref name="stack"/> adds to a meal. Mirrors
    /// BlockMeal.GetContentNutritionProperties (without spoilage) so numbers match the game's tooltip.
    /// </summary>
    public static FoodNutritionProperties? MealNutrition(IWorldAccessor world, ItemStack stack, CookingRecipeIngredient? ing, EntityAgent? entity)
    {
        try
        {
            var nutri = stack.Clone();
            nutri.StackSize = (int)(BlockLiquidContainerBase.GetContainableProps(nutri)?.ItemsPerLitre ?? 1f);
            float mul = 1f;
            if (ing != null)
            {
                mul = ing.GetMatchingStack(nutri)?.StackSize ?? 1;
                nutri.StackSize = (int)(nutri.StackSize * ing.PortionSizeLitres);
            }
            var props = BlockMeal.GetIngredientStackNutritionProperties(world, nutri, entity)?.Clone();
            if (props == null) return null;
            props.Satiety *= mul;
            props.Health *= mul;
            return props;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Satiety multiplier from how spoiled <paramref name="stack"/> is (1 when fresh).</summary>
    public static float SpoilageMul(IWorldAccessor world, ItemStack stack, EntityAgent? entity)
    {
        try
        {
            var copy = stack.Clone();
            float level = copy.Collectible.UpdateAndGetTransitionState(world, new DummySlot(copy), EnumTransitionType.Perish)?.TransitionLevel ?? 0f;
            return GlobalConstants.FoodSpoilageSatLossMul(level, copy, entity);
        }
        catch (Exception)
        {
            return 1f;
        }
    }

    private static ItemStack? PickRepresentative(List<ItemStack> stacks)
    {
        if (stacks.Count == 0) return null;
        if (stacks.Count == 1) return stacks[0];
        foreach (var pattern in Representatives)
        {
            var hit = stacks.FirstOrDefault(s => WildcardUtil.Match(new AssetLocation(pattern), s.Collectible.Code));
            if (hit != null) return hit;
        }
        return stacks[0];
    }

    public static string SafeName(ItemStack stack)
    {
        try { return stack.GetName()?.Trim() ?? ""; }
        catch (Exception) { return ""; }
    }

    public static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}
