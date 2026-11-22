using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CookUi;

/// <summary>
/// What a station that makes vanilla-style <see cref="CookingRecipe"/>s holds.
/// </summary>
/// <param name="MaxServings">Most servings the container takes (pot's MaxServingSize).</param>
/// <param name="DirtyPot">Dirty pots only make non-food recipes.</param>
public sealed record CookingStationState(ItemStack? Container, IReadOnlyList<ItemSlot> Slots, int MaxServings, bool DirtyPot)
    : StationState(Container, Slots);

/// <summary>
/// Recipe window for anything that makes <see cref="CookingRecipe"/>s: the firepit's cooking pot,
/// A Culinary Artillery's mixing bowl... Rows show the meal in a bowl with its satiety, and the
/// ingredient groups still needed or allowed. Derived classes only say which dialog they attach to
/// and what it holds; the recipes come from an <see cref="IRecipeProvider{TRecipe}"/>.
/// </summary>
public abstract class CookingRecipeWindow<THost> : RecipeWindow<THost, CookingStationState>
    where THost : GuiDialog
{
    private readonly IRecipeProvider<CookingRecipe> provider;
    private IngredientIcons? icons;

    protected CookingRecipeWindow(ICoreClientAPI capi, CookUiConfig config, IRecipeProvider<CookingRecipe> provider)
        : base(capi, config)
    {
        this.provider = provider;
    }

    protected IngredientIcons Icons => icons ??= new IngredientIcons(capi.World);

    protected override string Signature(CookingStationState state) =>
        base.Signature(state) + (state.DirtyPot ? "|dirty" : "") + "|" + state.MaxServings;

    protected override RecipeGrid BuildGrid(CookingStationState state)
    {
        var stacks = Stacks(state.Slots);
        int slotCount = state.Slots.Count;
        var predictions = CookingRecipePredictor.Predict(provider.GetRecipes(), stacks, slotCount, state.MaxServings, state.DirtyPot);
        var rows = predictions.Select(p => BuildRow(p, stacks, slotCount, state.MaxServings)).ToList();
        string note = Lang.Get(stacks.Length == 0 ? "cookui:no-recipes" : "cookui:no-recipe-fits");
        return new RecipeGrid(slotCount, rows, note);
    }

    private RowView BuildRow(RecipePrediction p, ItemStack[] stacks, int slotCount, int maxServings)
    {
        var recipe = p.Recipe;
        var ingredients = recipe.Ingredients!;

        // Serving count every slot has to match: known when the recipe matches; otherwise
        // follow the most common solid stack size already in the pot.
        int? servings = p.State is PredictionState.Ready or PredictionState.TooMuch ? p.Servings : null;
        if (servings == null)
        {
            var solid = stacks.Where(s => !IsLiquid(s)).Select(s => s.StackSize).ToList();
            if (solid.Count > 0)
            {
                servings = Math.Min(maxServings, solid.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key);
            }
        }

        var entity = capi.World.Player?.Entity;
        var slots = new List<SlotView>();
        var mealContents = new List<ItemStack>();
        var nutrition = new MealNutritionSummary();

        // 1. What is already in the pot.
        var counts = new int[ingredients.Length];
        foreach (var stack in stacks)
        {
            int j = CookingRecipePredictor.AssignedIngredient(ingredients, counts, stack);
            if (j >= 0) counts[j]++;
            string tooltip = IngredientIcons.SafeName(stack);
            var props = IngredientIcons.MealNutrition(capi.World, stack, j >= 0 ? ingredients[j] : null, entity);
            if (props != null)
            {
                float sat = props.Satiety * IngredientIcons.SpoilageMul(capi.World, stack, entity);
                nutrition.AddConfirmed(props.FoodCategory, sat);
                tooltip += "\n" + Lang.Get("cookui:sat-each", Round(sat));
            }
            bool warn = servings != null && !IsLiquid(stack) && stack.StackSize != servings;
            var icon = stack.Clone();
            mealContents.Add(icon);
            slots.Add(new SlotView { Kind = SlotKind.Present, Slot = Dummy(icon), Tooltip = tooltip, Count = AmountLabel(stack), Warn = warn });
        }

        // 2. Required ingredients still missing.
        for (int j = 0; j < ingredients.Length && slots.Count < slotCount; j++)
        {
            int need = ingredients[j].MinQuantity - counts[j];
            for (int k = 0; k < need && slots.Count < slotCount; k++)
            {
                var range = Icons.SatietyRange(ingredients[j], entity);
                if (range != null) nutrition.AddNeeded(range.Value);
                var view = Ghost(SlotKind.Required, ingredients[j], servings, range);
                if (view.Slot?.Itemstack != null) mealContents.Add(view.Slot.Itemstack);
                slots.Add(view);
            }
        }

        // 3. Optional room.
        AddOptionalSlots(ingredients, counts, slots, slotCount, servings, entity, nutrition);

        while (slots.Count < slotCount) slots.Add(new SlotView { Kind = SlotKind.Empty });

        return new RowView
        {
            Meal = MealSlot(p, stacks, mealContents, nutrition),
            State = p.State,
            Ingredients = slots,
        };
    }

    /// <summary>A set of ingredient groups that accept the same items (egg-base + egg-extra...).</summary>
    private sealed class OptionalKind
    {
        public required CookingRecipeIngredient Ingredient;
        public int Room;
        public (float min, float max)? Satiety;
    }

    /// <summary>
    /// Fills the free pot slots with optional ingredients. Groups accepting the same items are
    /// merged into one kind. If every kind gets its own slot, each slot shows one kind; otherwise
    /// every free slot is a "any of these" slot drawing a small mosaic of all the kinds.
    /// </summary>
    private void AddOptionalSlots(CookingRecipeIngredient[] ingredients, int[] counts, List<SlotView> slots,
        int slotCount, int? servings, EntityAgent? entity, MealNutritionSummary nutrition)
    {
        var kinds = new List<OptionalKind>();
        var byKey = new Dictionary<string, OptionalKind>();
        for (int j = 0; j < ingredients.Length; j++)
        {
            var ing = ingredients[j];
            int room = ing.MaxQuantity - Math.Max(ing.MinQuantity, counts[j]);
            if (room <= 0) continue;
            string key = KindKey(ing);
            if (!byKey.TryGetValue(key, out var kind))
            {
                kind = new OptionalKind { Ingredient = ing, Satiety = Icons.SatietyRange(ing, entity) };
                byKey[key] = kind;
                kinds.Add(kind);
            }
            kind.Room += room;
        }

        int free = slotCount - slots.Count;
        if (free <= 0 || kinds.Count == 0) return;

        // Best extra satiety: the most nutritious items that fit in the free slots.
        float extra = kinds
            .SelectMany(k => Enumerable.Repeat(k.Satiety?.max ?? 0f, k.Room))
            .OrderByDescending(v => v).Take(free).Sum();
        if (extra > 0) nutrition.AddOptional(extra);

        int optSlots = Math.Min(free, kinds.Sum(k => k.Room));
        if (kinds.Count <= optSlots)
        {
            // Room for one of each: round-robin so different kinds show up first.
            var left = kinds.Select(k => k.Room).ToArray();
            bool added = true;
            while (slots.Count < slotCount && added)
            {
                added = false;
                for (int i = 0; i < kinds.Count && slots.Count < slotCount; i++)
                {
                    if (left[i] <= 0) continue;
                    left[i]--;
                    slots.Add(Ghost(SlotKind.Optional, kinds[i].Ingredient, servings, kinds[i].Satiety));
                    added = true;
                }
            }
            return;
        }

        // More kinds than free slots: each free slot accepts any of them.
        // No corner amount on these slots (it would cover the "+N" cell): each line carries its own.
        var choices = kinds.Select(k => Ghost(SlotKind.Optional, k.Ingredient, servings, k.Satiety)).ToList();
        var tooltip = new StringBuilder(Lang.Get("cookui:slot-optional-any"));
        for (int i = 0; i < kinds.Count; i++)
        {
            var k = kinds[i];
            string label = Icons.Get(k.Ingredient).Label;
            if (choices[i].Count != null) label += " (" + choices[i].Count + ")";
            tooltip.Append('\n').Append(k.Satiety is { } s
                ? Lang.Get("cookui:slot-choice-sat", label, k.Room, Range(s.min, s.max))
                : Lang.Get("cookui:slot-choice", label, k.Room));
        }
        string text = tooltip.ToString();
        for (int i = 0; i < optSlots; i++)
        {
            slots.Add(new SlotView { Kind = SlotKind.Optional, Choices = choices, Tooltip = text });
        }
    }

    private string KindKey(CookingRecipeIngredient ing) =>
        string.Join(",", Icons.Stacks(ing).Select(s => s.Collectible.Code.ToString()).OrderBy(c => c, StringComparer.Ordinal))
        + "|" + ing.PortionSizeLitres.ToString(CultureInfo.InvariantCulture);

    private SlotView Ghost(SlotKind kind, CookingRecipeIngredient ing, int? servings, (float min, float max)? satiety)
    {
        var info = Icons.Get(ing);
        string key = kind == SlotKind.Required ? "cookui:slot-needed" : "cookui:slot-optional";
        string? count = null;
        if (servings != null)
        {
            count = ing.PortionSizeLitres > 0 ? Litres(servings.Value * ing.PortionSizeLitres) : servings.Value.ToString();
        }
        bool rotates = info.Categories.Count > 1;
        // Rotating slots list only their categories; the rest name the item and its satiety.
        string? tooltip = rotates ? null : Lang.Get(key, info.Label);
        if (!rotates && satiety != null) tooltip += "\n" + Lang.Get("cookui:sat-each", Range(satiety.Value.min, satiety.Value.max));
        return new SlotView
        {
            Kind = kind,
            Slot = info.Icon == null ? null : Dummy(info.Icon.Clone()),
            Cycle = rotates ? info.Categories.Select(c => Dummy(c.Icon.Clone())).ToList() : null,
            Categories = rotates ? info.Categories.Select(c => c.Name).ToList() : null,
            Tooltip = tooltip,
            Count = count,
        };
    }

    private SlotView MealSlot(RecipePrediction p, ItemStack[] stacks, List<ItemStack> contents, MealNutritionSummary nutrition)
    {
        var recipe = p.Recipe;
        ItemStack? icon = null;
        string name;
        bool food = false;

        if (recipe.CooksInto?.ResolvedItemstack is { } outStack)
        {
            // Non-food recipes (glue, potash...) cook straight into an item.
            icon = outStack.Clone();
            icon.StackSize = 1;
            name = IngredientIcons.SafeName(icon);
        }
        else
        {
            food = true;
            name = Lang.GetIfExists("mealrecipe-name-" + recipe.Code)
                   ?? Lang.GetIfExists("cookui:recipe-" + recipe.Code)
                   ?? IngredientIcons.Capitalize(recipe.Code ?? "?");
            if (p.State != PredictionState.Missing && stacks.Length > 0)
            {
                try
                {
                    var vanilla = recipe.GetOutputName(capi.World, stacks);
                    if (!string.IsNullOrWhiteSpace(vanilla)) name = IngredientIcons.Capitalize(vanilla.Trim());
                }
                catch (Exception) { /* naming helpers can throw on odd ingredient sets */ }
            }
            icon = MealBowl(recipe, contents);
        }

        string status = StatusText(p.State, p.Servings, p.MissingTotal);

        var tooltip = new StringBuilder(name).Append('\n').Append(status);
        if (food) AppendNutrition(tooltip, p, nutrition);

        return new SlotView
        {
            Kind = SlotKind.Present,
            Slot = icon == null ? null : Dummy(icon),
            Tooltip = tooltip.ToString(),
            Count = p.State is PredictionState.Ready or PredictionState.TooMuch ? p.Servings.ToString() : null,
            Warn = p.State == PredictionState.TooMuch,
        };
    }

    private static void AppendNutrition(StringBuilder sb, RecipePrediction p, MealNutritionSummary n)
    {
        if (n.ByCategory.Count == 0 && n.NeededMax <= 0 && n.OptionalMax <= 0) return;

        if (n.NeededMax <= 0)
        {
            // Everything required is in the pot: these are the real numbers.
            if (p.State == PredictionState.Ready)
                sb.Append('\n').Append(Lang.Get("cookui:sat-ready", Round(n.Confirmed), Round(n.Confirmed * p.Servings)));
            else
                sb.Append('\n').Append(Lang.Get("cookui:sat-per-serving", Round(n.Confirmed)));
        }
        else
        {
            if (n.Confirmed > 0) sb.Append('\n').Append(Lang.Get("cookui:sat-so-far", Round(n.Confirmed)));
            sb.Append('\n').Append(Lang.Get("cookui:sat-finished",
                Range(n.Confirmed + n.NeededMin, n.Confirmed + n.NeededMax)));
        }

        foreach (var kv in n.ByCategory.OrderByDescending(kv => kv.Value))
        {
            string cat = Lang.Get("foodcategory-" + kv.Key.ToString().ToLowerInvariant());
            sb.Append('\n').Append(Lang.Get("nutrition-facts-line-satiety", cat, Round(kv.Value)));
        }

        if (n.OptionalMax > 0) sb.Append('\n').Append(Lang.Get("cookui:sat-extras", Round(n.OptionalMax)));
    }

    /// <summary>A bowl showing the meal, filled with the actual/expected ingredients like the handbook does.</summary>
    private ItemStack? MealBowl(CookingRecipe recipe, List<ItemStack> contents)
    {
        var bowls = BlockMeal.AllMealBowls(capi);
        if (bowls == null || bowls.Length == 0) return null;
        var bowl = new ItemStack(bowls[0]);
        try
        {
            var filled = contents.Select(s => { var c = s.Clone(); c.StackSize = 1; return c; }).ToArray<ItemStack?>();
            ((IBlockMealContainer)bowls[0]).SetContents(recipe.Code, bowl, filled);
        }
        catch (Exception)
        {
            // An unusual combination can fail to mesh; an empty bowl still identifies the row.
        }
        return bowl;
    }
}

/// <summary>Per-serving satiety of a meal: confirmed from pot contents, ranges for the rest.</summary>
public sealed class MealNutritionSummary
{
    public readonly Dictionary<EnumFoodCategory, float> ByCategory = new();
    public float Confirmed;
    public float NeededMin, NeededMax;
    public float OptionalMax;

    public void AddConfirmed(EnumFoodCategory cat, float sat)
    {
        Confirmed += sat;
        ByCategory.TryGetValue(cat, out var v);
        ByCategory[cat] = v + sat;
    }

    public void AddNeeded((float min, float max) r)
    {
        NeededMin += r.min;
        NeededMax += r.max;
    }

    public void AddOptional(float max) => OptionalMax += max;
}
