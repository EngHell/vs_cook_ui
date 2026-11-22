using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CookUi.Compat.AculinaryArtillery;

/// <summary>
/// ACA saucepan or cauldron in a firepit. These simmer: either a single ingredient that cooks
/// on its own (its CombustibleProps), or an ACA simmer recipe, where every ingredient is required,
/// takes one slot, and all of them come in the same multiple of the recipe amounts.
/// </summary>
public sealed class SimmerRecipeSource : IRecipeSource
{
    private readonly ICoreClientAPI capi;
    private readonly AcaSimmerRecipes recipes;
    private IngredientIcons? icons;

    public SimmerRecipeSource(ICoreClientAPI capi)
    {
        this.capi = capi;
        recipes = new AcaSimmerRecipes(capi);
    }

    private IngredientIcons Icons => icons ??= new IngredientIcons(capi.World);

    public bool AppliesTo(StationContents contents) => Aca.IsA(contents.Container?.Collectible, Aca.SaucepanBlock);

    private sealed record Prediction(
        string Code, ItemStack Output, PredictionState State, int Batches,
        CraftingRecipeIngredient[] Ingredients, int[] Assigned, int Missing);

    public IEnumerable<RecipeRow> GetRows(StationContents contents)
    {
        var stacks = RecipeFormat.Stacks(contents.Slots);
        int slotCount = contents.Slots.Count;

        var predictions = new List<Prediction>();
        if (SingleIngredient(stacks) is { } single) predictions.Add(single);
        foreach (var recipe in recipes.All())
        {
            var p = Analyze(recipe, stacks, slotCount);
            if (p != null) predictions.Add(p);
        }

        return predictions
            .OrderBy(p => p.State == PredictionState.Missing ? 1 : 0)
            .ThenBy(p => p.Missing)
            .ThenBy(p => IngredientIcons.SafeName(p.Output), StringComparer.OrdinalIgnoreCase)
            .Select(p => BuildRow(p, stacks, slotCount))
            .ToList();
    }

    /// <summary>One stack that simmers by itself (mirrors BlockSaucepan.CanSmelt).</summary>
    private static Prediction? SingleIngredient(ItemStack[] stacks)
    {
        if (stacks.Length != 1) return null;
        var props = stacks[0].Collectible?.CombustibleProps;
        if (props?.SmeltedStack?.ResolvedItemstack is not { } output) return null;
        if (props.SmeltingType is not (EnumSmeltType.Cook or EnumSmeltType.Convert) || !props.RequiresContainer) return null;
        int ratio = Math.Max(1, props.SmeltedRatio);
        int batches = stacks[0].StackSize / ratio;
        var state = batches > 0 ? PredictionState.Ready : PredictionState.RatioWrong;
        return new Prediction("~single", output, state, batches, Array.Empty<CraftingRecipeIngredient>(), new[] { -1 }, 0);
    }

    /// <summary>Where the contents stand with <paramref name="recipe"/>, or null if something doesn't belong.</summary>
    private static Prediction? Analyze(SimmerRecipeInfo recipe, ItemStack[] stacks, int slotCount)
    {
        var ings = recipe.Ingredients;
        if (stacks.Length > ings.Length) return null;
        var used = new bool[ings.Length];
        var assigned = new int[stacks.Length];

        for (int i = 0; i < stacks.Length; i++)
        {
            int j = -1;
            for (int k = 0; k < ings.Length; k++)
            {
                if (used[k] || (ings[k].ResolvedItemStack == null && ings[k].MatchingType == EnumRecipeMatchType.Exact)) continue;
                bool ok;
                try { ok = ings[k].SatisfiesAsIngredient(stacks[i], false); }
                catch (Exception) { ok = false; }
                if (ok) { j = k; break; }
            }
            if (j < 0) return null;
            used[j] = true;
            assigned[i] = j;
        }

        int missing = ings.Length - stacks.Length;
        if (missing > slotCount - stacks.Length) return null;
        if (missing > 0) return new Prediction(recipe.Code, recipe.Output, PredictionState.Missing, 0, ings, assigned, missing);

        int batches = recipe.Match(stacks.Select(s => s.Clone()).ToList());
        var state = batches > 0 ? PredictionState.Ready : PredictionState.RatioWrong;
        return new Prediction(recipe.Code, recipe.Output, state, batches, ings, assigned, 0);
    }

    private RecipeRow BuildRow(Prediction p, ItemStack[] stacks, int slotCount)
    {
        // Batch count every ingredient has to match: known when ready, else the most common
        // whole multiple among what's already in.
        int? batches = p.State == PredictionState.Ready ? p.Batches : null;
        if (batches == null)
        {
            var multiples = new List<int>();
            for (int i = 0; i < stacks.Length; i++)
            {
                int j = p.Assigned[i];
                if (j >= 0 && p.Ingredients[j].Quantity > 0) multiples.Add(stacks[i].StackSize / p.Ingredients[j].Quantity);
            }
            multiples.RemoveAll(m => m <= 0);
            if (multiples.Count > 0) batches = multiples.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        }

        var slots = new List<RecipeSlot>();
        var present = new bool[p.Ingredients.Length];

        for (int i = 0; i < stacks.Length; i++)
        {
            var stack = stacks[i];
            int j = p.Assigned[i];
            string tooltip = IngredientIcons.SafeName(stack);
            bool warn = false;
            if (j >= 0)
            {
                present[j] = true;
                var ing = p.Ingredients[j];
                tooltip += "\n" + Lang.Get("cookui:simmer-per-batch", RecipeFormat.AmountOf(stack, ing.Quantity));
                warn = batches != null && stack.StackSize != ing.Quantity * batches.Value;
            }
            else if (p.State == PredictionState.RatioWrong)
            {
                warn = true; // single ingredient below one portion
            }
            slots.Add(new RecipeSlot { Kind = SlotKind.Present, Icon = stack.Clone(), Tooltip = tooltip, Count = RecipeFormat.AmountLabel(stack), Warn = warn });
        }

        for (int j = 0; j < p.Ingredients.Length && slots.Count < slotCount; j++)
        {
            if (present[j]) continue;
            slots.Add(Ghost(p.Ingredients[j], batches));
        }

        while (slots.Count < slotCount) slots.Add(RecipeSlot.Empty());

        return new RecipeRow { Result = ResultSlot(p), State = p.State, Ingredients = slots };
    }

    private RecipeSlot Ghost(CraftingRecipeIngredient ing, int? batches)
    {
        var info = Icons.Get(ing);
        var sample = info.Icon ?? ing.ResolvedItemStack;
        string perBatch = sample == null ? ing.Quantity.ToString() : RecipeFormat.AmountOf(sample, ing.Quantity);
        bool rotates = info.Categories.Count > 1;
        string tooltip = Lang.Get("cookui:slot-needed", info.Label) + "\n" + Lang.Get("cookui:simmer-per-batch", perBatch);
        return new RecipeSlot
        {
            Kind = SlotKind.Required,
            Icon = info.Icon?.Clone(),
            Cycle = rotates ? info.Categories.Select(c => c.Icon.Clone()).ToList() : null,
            Categories = rotates ? info.Categories.Select(c => c.Name).ToList() : null,
            Tooltip = tooltip,
            Count = sample == null || batches == null ? null : RecipeFormat.AmountOf(sample, ing.Quantity * batches.Value),
        };
    }

    private RecipeSlot ResultSlot(Prediction p)
    {
        var icon = p.Output.Clone();
        icon.StackSize = 1;
        string name = IngredientIcons.SafeName(icon);

        string? count = null;
        string status;
        switch (p.State)
        {
            case PredictionState.Ready:
                var total = p.Output.Clone();
                total.StackSize = p.Output.StackSize * p.Batches;
                count = RecipeFormat.AmountLabel(total);
                status = Lang.Get("cookui:simmer-makes", count);
                break;
            case PredictionState.RatioWrong:
                status = Lang.Get("cookui:simmer-ratio");
                break;
            default:
                status = Lang.Get("cookui:needs-more", p.Missing);
                break;
        }

        return new RecipeSlot
        {
            Kind = SlotKind.Present,
            Icon = icon,
            Tooltip = name + "\n" + status,
            Count = count,
        };
    }
}
