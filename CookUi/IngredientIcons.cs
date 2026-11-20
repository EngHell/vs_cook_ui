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
    /// <summary>Short name: the item's own name for single-item groups, else the category ("vegetables").</summary>
    public required string Label;
    /// <summary>Label plus, for categories, a few accepted item names.</summary>
    public required string Detail;
}

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
    };

    private const int MaxListedNames = 6;

    private readonly IWorldAccessor world;
    private readonly Dictionary<CookingRecipeIngredient, IngredientInfo> cache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CookingRecipeIngredient, List<ItemStack>> stacksCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CookingRecipeIngredient, (float min, float max)?> satietyCache = new(ReferenceEqualityComparer.Instance);

    public IngredientIcons(IWorldAccessor world) => this.world = world;

    public IngredientInfo Get(CookingRecipeIngredient ing)
    {
        if (cache.TryGetValue(ing, out var info)) return info;

        var stacks = Stacks(ing);
        ItemStack? icon = PickRepresentative(stacks);
        var names = stacks.Select(SafeName).Where(n => n.Length > 0).Distinct().ToList();

        string label, detail;
        if (names.Count == 1)
        {
            label = detail = names[0];
        }
        else
        {
            label = Capitalize(Lang.GetIfExists("handbook-mealingredient-" + ing.TypeName)
                               ?? Lang.GetIfExists("foodcategory-" + ing.TypeName)
                               ?? ing.TypeName);
            var shown = names.Take(MaxListedNames).ToList();
            string list = string.Join(", ", shown);
            if (names.Count > shown.Count) list += ", …";
            detail = names.Count == 0 ? label : label + ": " + list;
        }

        info = new IngredientInfo { Icon = icon, Label = label, Detail = detail };
        cache[ing] = info;
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
                foreach (var c in world.Collectibles) // items and blocks
                {
                    if (c?.Code == null || !c.WildCardMatch(vs.Code)) continue;
                    if ((vs.Type == EnumItemClass.Block) != (c is Block)) continue;
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
