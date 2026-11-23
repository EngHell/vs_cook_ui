using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CookUi;

/// <summary>
/// Data helpers for recipe sources: expands recipe ingredients (wildcards, variant filters, tags)
/// into the concrete stacks they accept, and works out the satiety they add to a meal.
/// Results are cached and the same list instance is returned for the same ingredient, which is
/// what <see cref="IngredientOption.Accepts"/> wants.
/// </summary>
public class IngredientResolver
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
    private readonly Dictionary<object, List<ItemStack>> stacksCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<ItemStack>> wildcardCache = new();
    private readonly Dictionary<CookingRecipeIngredient, (float min, float max)?> satietyCache = new(ReferenceEqualityComparer.Instance);

    public IngredientResolver(IWorldAccessor world) => this.world = world;

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

        // Mod recipes repeat the same wildcard across hundreds of recipe objects (ACA's 1000+
        // kneading recipes); share the expensive scan between equal ingredients.
        // (Tag conditions are left out: those ingredients just get their own scan.)
        string? valueKey = ing.MatchingType == EnumRecipeMatchType.Exact || !ing.Tags.IsEmpty ? null : string.Join("|",
            ing.Type, ing.MatchingType, ing.Code,
            ing.AllowedVariants == null ? "" : string.Join(",", ing.AllowedVariants),
            ing.SkipVariants == null ? "" : string.Join(",", ing.SkipVariants));
        if (valueKey != null && wildcardCache.TryGetValue(valueKey, out list))
        {
            stacksCache[ing] = list;
            return list;
        }
        list = new List<ItemStack>();

        if (ing.MatchingType != EnumRecipeMatchType.Exact)
        {
            // Wildcards, regexes, tags...: let the ingredient judge every collectible.
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
        if (valueKey != null) wildcardCache[valueKey] = list;
        return list;
    }

    /// <summary>
    /// Lowest and highest per-serving satiety any accepted item would add to the meal
    /// (fresh, before spoilage), or null if none of them is food.
    /// </summary>
    public (float min, float max)? SatietyRange(CookingRecipeIngredient ing, EntityAgent? entity)
    {
        if (satietyCache.TryGetValue(ing, out var range)) return range;
        var values = Stacks(ing)
            .Select(s => NutritionOf(world, s, ing, entity)?.Satiety)
            .Where(v => v != null).Select(v => v!.Value).ToList();
        range = values.Count == 0 ? null : (values.Min(), values.Max());
        satietyCache[ing] = range;
        return range;
    }

    /// <summary>
    /// What one serving's worth of <paramref name="stack"/> adds to a meal. Mirrors
    /// BlockMeal.GetContentNutritionProperties (without spoilage) so numbers match the game's tooltip.
    /// </summary>
    public static FoodNutritionProperties? NutritionOf(IWorldAccessor world, ItemStack stack, CookingRecipeIngredient? ing, EntityAgent? entity)
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

    /// <summary>A typical item of the group for icons: a carrot for vegetables, an apple for fruit...</summary>
    public static ItemStack? Representative(List<ItemStack> stacks)
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
