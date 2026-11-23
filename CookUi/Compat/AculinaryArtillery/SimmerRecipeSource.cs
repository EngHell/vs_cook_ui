using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

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
    private IngredientResolver? resolver;

    public SimmerRecipeSource(ICoreClientAPI capi)
    {
        this.capi = capi;
        recipes = new AcaSimmerRecipes(capi);
    }

    private IngredientResolver Resolver => resolver ??= new IngredientResolver(capi.World);

    public bool AppliesTo(StationContents contents) => Aca.IsA(contents.Container?.Collectible, Aca.SaucepanBlock);

    public IReadOnlyList<RecipeMatch> FindMatches(StationContents contents)
    {
        var stacks = RecipeFormat.Stacks(contents.Slots);
        int slotCount = contents.Slots.Count;

        var matches = new List<RecipeMatch>();
        if (SingleIngredient(stacks) is { } single) matches.Add(single);
        foreach (var recipe in recipes.All())
        {
            if (Analyze(recipe, stacks, slotCount) is { } m) matches.Add(m);
        }
        return matches;
    }

    /// <summary>One stack that simmers by itself (mirrors BlockSaucepan.CanSmelt).</summary>
    private static RecipeMatch? SingleIngredient(ItemStack[] stacks)
    {
        if (stacks.Length != 1) return null;
        var stack = stacks[0];
        var props = stack.Collectible?.CombustibleProps;
        if (props?.SmeltedStack?.ResolvedItemstack is not { } output) return null;
        if (props.SmeltingType is not (EnumSmeltType.Cook or EnumSmeltType.Convert) || !props.RequiresContainer) return null;
        int ratio = Math.Max(1, props.SmeltedRatio);
        int batches = stack.StackSize / ratio;
        return new RecipeMatch
        {
            Output = output,
            State = batches > 0 ? MatchState.Ready : MatchState.WrongAmounts,
            AmountRule = AmountRule.AtLeast,
            Makes = output.StackSize * batches,
            Slots = new List<IngredientSlot>
            {
                new() { Role = SlotRole.Filled, Stack = stack.Clone(), AmountPerBatch = ratio, AmountWrong = batches == 0 },
            },
        };
    }

    /// <summary>Where the contents stand with <paramref name="recipe"/>, or null if something doesn't belong.</summary>
    private RecipeMatch? Analyze(SimmerRecipeInfo recipe, ItemStack[] stacks, int slotCount)
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

        int batches = missing == 0 ? recipe.Match(stacks.Select(s => s.Clone()).ToList()) : 0;
        var state = missing > 0 ? MatchState.Missing : batches > 0 ? MatchState.Ready : MatchState.WrongAmounts;

        // Batch count every ingredient has to match: known when ready, else the most common
        // whole multiple among what's already in.
        int? target = state == MatchState.Ready ? batches : null;
        if (target == null)
        {
            var multiples = Enumerable.Range(0, stacks.Length)
                .Where(i => ings[assigned[i]].Quantity > 0)
                .Select(i => stacks[i].StackSize / ings[assigned[i]].Quantity)
                .Where(m => m > 0).ToList();
            if (multiples.Count > 0) target = multiples.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        }

        var slots = new List<IngredientSlot>();
        for (int i = 0; i < stacks.Length; i++)
        {
            var ing = ings[assigned[i]];
            slots.Add(new IngredientSlot
            {
                Role = SlotRole.Filled,
                Stack = stacks[i].Clone(),
                AmountPerBatch = ing.Quantity,
                AmountWrong = target != null && stacks[i].StackSize != ing.Quantity * target.Value,
            });
        }
        for (int j = 0; j < ings.Length; j++)
        {
            if (used[j]) continue;
            slots.Add(new IngredientSlot
            {
                Role = SlotRole.Required,
                Options = new List<IngredientOption>
                {
                    new()
                    {
                        Accepts = Resolver.Stacks(ings[j]),
                        TypeCode = ings[j].Name,
                        // Unknown batch count: show what one batch needs.
                        Amount = ings[j].Quantity * (target ?? 1),
                    },
                },
            });
        }

        return new RecipeMatch
        {
            Output = recipe.Output,
            State = state,
            Slots = slots,
            AmountRule = AmountRule.SameMultiple,
            Makes = recipe.Output.StackSize * batches,
        };
    }
}
