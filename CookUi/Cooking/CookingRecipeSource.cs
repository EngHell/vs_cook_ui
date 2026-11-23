using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CookUi;

/// <summary>
/// Matches vanilla-style <see cref="CookingRecipe"/>s made in a cooking pot: the firepit,
/// A Culinary Artillery's mixing bowl... Reports what's in the pot, the ingredient groups still
/// needed, the optional room left, servings and satiety.
/// </summary>
public sealed class CookingRecipeSource : IRecipeSource
{
    private readonly ICoreClientAPI capi;
    private readonly Func<IEnumerable<CookingRecipe>> recipes;
    private readonly bool dirtyPotsMakeFood;
    private IngredientResolver? resolver;

    /// <param name="recipes">Recipe list, read each time the contents change.</param>
    /// <param name="dirtyPotsMakeFood">False for the firepit, where a dirty pot only makes non-food.</param>
    public CookingRecipeSource(ICoreClientAPI capi, Func<IEnumerable<CookingRecipe>> recipes, bool dirtyPotsMakeFood = false)
    {
        this.capi = capi;
        this.recipes = recipes;
        this.dirtyPotsMakeFood = dirtyPotsMakeFood;
    }

    /// <summary>Vanilla's cooking recipes, as synced from the server.</summary>
    public static CookingRecipeSource Vanilla(ICoreClientAPI capi) =>
        new(capi, () => capi.GetCookingRecipes() ?? new List<CookingRecipe>());

    private IngredientResolver Resolver => resolver ??= new IngredientResolver(capi.World);

    public bool AppliesTo(StationContents contents) => contents.Container?.Collectible is BlockCookingContainer;

    public IReadOnlyList<RecipeMatch> FindMatches(StationContents contents)
    {
        if (contents.Container?.Collectible is not BlockCookingContainer pot) return Array.Empty<RecipeMatch>();
        bool dirty = !dirtyPotsMakeFood && pot.Attributes?["isDirtyPot"].AsBool() == true;
        var stacks = RecipeFormat.Stacks(contents.Slots);
        int slotCount = contents.Slots.Count;
        return CookingRecipePredictor.Predict(recipes(), stacks, slotCount, pot.MaxServingSize, dirty)
            .Select(p => Match(p, contents.Container, stacks, slotCount, pot.MaxServingSize))
            .ToList();
    }

    private RecipeMatch Match(RecipePrediction p, ItemStack potStack, ItemStack[] stacks, int slotCount, int maxServings)
    {
        var recipe = p.Recipe;
        var ingredients = recipe.Ingredients!;

        // Serving count every slot has to match: known when the recipe matches; otherwise
        // follow the most common solid stack size already in the pot.
        int? servings = p.State is MatchState.Ready or MatchState.TooMuch ? p.Servings : null;
        if (servings == null)
        {
            var solid = stacks.Where(s => !RecipeFormat.IsLiquid(s)).Select(s => s.StackSize).ToList();
            if (solid.Count > 0)
            {
                servings = Math.Min(maxServings, solid.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key);
            }
        }

        var entity = capi.World.Player?.Entity;
        var slots = new List<IngredientSlot>();
        var mealContents = new List<ItemStack>();
        var nutrition = new MealNutrition();

        // 1. What is already in the pot.
        var counts = new int[ingredients.Length];
        foreach (var stack in stacks)
        {
            int j = CookingRecipePredictor.AssignedIngredient(ingredients, counts, stack);
            if (j >= 0) counts[j]++;
            float? sat = null;
            var props = IngredientResolver.NutritionOf(capi.World, stack, j >= 0 ? ingredients[j] : null, entity);
            if (props != null)
            {
                sat = props.Satiety * IngredientResolver.SpoilageMul(capi.World, stack, entity);
                nutrition.AddConfirmed(props.FoodCategory, sat.Value);
            }
            mealContents.Add(stack);
            slots.Add(new IngredientSlot
            {
                Role = SlotRole.Filled,
                Stack = stack.Clone(),
                Satiety = sat,
                AmountWrong = servings != null && !RecipeFormat.IsLiquid(stack) && stack.StackSize != servings,
            });
        }

        // 2. Required ingredients still missing.
        for (int j = 0; j < ingredients.Length && slots.Count < slotCount; j++)
        {
            int need = ingredients[j].MinQuantity - counts[j];
            for (int k = 0; k < need && slots.Count < slotCount; k++)
            {
                var option = Option(ingredients[j], servings, entity, null);
                if (option.Satiety != null) nutrition.AddNeeded(option.Satiety.Value);
                if (IngredientResolver.Representative(option.Accepts) is { } typical) mealContents.Add(typical);
                slots.Add(new IngredientSlot { Role = SlotRole.Required, Options = new List<IngredientOption> { option } });
            }
        }

        // 3. Optional room.
        AddOptionalRoom(ingredients, counts, slots, slotCount, servings, entity, nutrition);

        bool food = recipe.CooksInto?.ResolvedItemstack == null;
        return new RecipeMatch
        {
            // Meals show as a bowl filled with them; without bowls (odd modpacks), the pot.
            Output = food ? MealBowl(recipe, mealContents) ?? OneOf(potStack) : OneOf(recipe.CooksInto!.ResolvedItemstack!),
            Name = food ? MealName(p, stacks) : null,
            State = p.State,
            Slots = slots,
            AmountRule = AmountRule.SameInEverySlot,
            Makes = p.Servings,
            MakesServings = true,
            Nutrition = food ? nutrition : null,
        };
    }

    private IngredientOption Option(CookingRecipeIngredient ing, int? servings, EntityAgent? entity, int? limit)
    {
        var accepts = Resolver.Stacks(ing);
        int? amount = null;
        if (servings != null)
        {
            float perLitre = accepts.Count > 0 ? BlockLiquidContainerBase.GetContainableProps(accepts[0])?.ItemsPerLitre ?? 0 : 0;
            amount = ing.PortionSizeLitres > 0 && perLitre > 0
                ? (int)Math.Round(servings.Value * ing.PortionSizeLitres * perLitre)
                : servings.Value;
        }
        return new IngredientOption
        {
            Accepts = accepts,
            TypeCode = ing.TypeName,
            Amount = amount,
            Limit = limit,
            Satiety = Resolver.SatietyRange(ing, entity),
        };
    }

    /// <summary>
    /// The pot's free slots that optional ingredients may take. Groups accepting the same items
    /// (egg-base + egg-extra...) are one kind; every optional slot offers the same kinds.
    /// </summary>
    private void AddOptionalRoom(CookingRecipeIngredient[] ingredients, int[] counts, List<IngredientSlot> slots,
        int slotCount, int? servings, EntityAgent? entity, MealNutrition nutrition)
    {
        var kinds = new List<(CookingRecipeIngredient ing, int room)>();
        var byKey = new Dictionary<string, int>();
        for (int j = 0; j < ingredients.Length; j++)
        {
            var ing = ingredients[j];
            int room = ing.MaxQuantity - Math.Max(ing.MinQuantity, counts[j]);
            if (room <= 0) continue;
            string key = KindKey(ing);
            if (byKey.TryGetValue(key, out int at)) kinds[at] = (kinds[at].ing, kinds[at].room + room);
            else
            {
                byKey[key] = kinds.Count;
                kinds.Add((ing, room));
            }
        }

        int free = slotCount - slots.Count;
        if (free <= 0 || kinds.Count == 0) return;

        var options = kinds.Select(k => Option(k.ing, servings, entity, k.room)).ToList();

        // Best extra satiety: the most nutritious items that fit in the free slots.
        nutrition.OptionalMax += options
            .SelectMany(o => Enumerable.Repeat(o.Satiety?.max ?? 0f, o.Limit ?? 0))
            .OrderByDescending(v => v).Take(free).Sum();

        int optSlots = Math.Min(free, kinds.Sum(k => k.room));
        for (int i = 0; i < optSlots; i++)
        {
            slots.Add(new IngredientSlot { Role = SlotRole.Optional, Options = options });
        }
    }

    private string KindKey(CookingRecipeIngredient ing) =>
        string.Join(",", Resolver.Stacks(ing).Select(s => s.Collectible.Code.ToString()).OrderBy(c => c, StringComparer.Ordinal))
        + "|" + ing.PortionSizeLitres.ToString(CultureInfo.InvariantCulture);

    private string MealName(RecipePrediction p, ItemStack[] stacks)
    {
        var recipe = p.Recipe;
        string name = Lang.GetIfExists("mealrecipe-name-" + recipe.Code)
                      ?? Lang.GetIfExists("cookui:recipe-" + recipe.Code)
                      ?? IngredientResolver.Capitalize(recipe.Code ?? "?");
        if (p.State != MatchState.Missing && stacks.Length > 0)
        {
            try
            {
                var vanilla = recipe.GetOutputName(capi.World, stacks);
                if (!string.IsNullOrWhiteSpace(vanilla)) name = IngredientResolver.Capitalize(vanilla.Trim());
            }
            catch (Exception) { /* naming helpers can throw on odd ingredient sets */ }
        }
        return name;
    }

    private static ItemStack OneOf(ItemStack stack)
    {
        var s = stack.Clone();
        s.StackSize = 1;
        return s;
    }

    /// <summary>A bowl showing the meal, filled with the actual/expected ingredients like the handbook does.</summary>
    private ItemStack? MealBowl(CookingRecipe recipe, List<ItemStack> contents)
    {
        var bowls = BlockMeal.AllMealBowls(capi);
        if (bowls == null || bowls.Length == 0) return null;
        var bowl = new ItemStack(bowls[0]);
        try
        {
            var filled = contents.Select(s => OneOf(s)).ToArray<ItemStack?>();
            ((IBlockMealContainer)bowls[0]).SetContents(recipe.Code, bowl, filled);
        }
        catch (Exception)
        {
            // An unusual combination can fail to mesh; an empty bowl still identifies the row.
        }
        return bowl;
    }
}
