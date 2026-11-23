using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace CookUi.Compat.AculinaryArtillery;

/// <summary>
/// ACA mixing bowl without a pot: it kneads dough (Expanded Foods' doughs, breads, fillings...).
/// A kneading recipe is a list of ingredient groups; each group takes exactly one of the six
/// slots and accepts any one of its inputs, at least that input's amount. Nothing else may be in
/// the bowl. Each mixing cycle makes one batch and uses one amount of each, leaving the rest.
/// </summary>
public sealed class KneadingRecipeSource : IRecipeSource
{
    private readonly ICoreClientAPI capi;
    private readonly AcaKneadingRecipes recipes;
    private IngredientResolver? resolver;

    public KneadingRecipeSource(ICoreClientAPI capi)
    {
        this.capi = capi;
        recipes = new AcaKneadingRecipes(capi);
    }

    private IngredientResolver Resolver => resolver ??= new IngredientResolver(capi.World);

    // With a pot in, the bowl mixes meals instead.
    public bool AppliesTo(StationContents contents) => contents.Container?.Collectible is not BlockCookingContainer;

    public IReadOnlyList<RecipeMatch> FindMatches(StationContents contents)
    {
        var slotArray = contents.Slots.ToArray();
        var stacks = RecipeFormat.Stacks(slotArray);
        // Over a thousand recipes: an empty bowl matches them all, which tells the player nothing.
        if (stacks.Length == 0) return Array.Empty<RecipeMatch>();

        return recipes.All()
            .Select(r => Analyze(r, stacks, slotArray))
            .Where(m => m != null).Cast<RecipeMatch>()
            .ToList();
    }

    private RecipeMatch? Analyze(KneadingRecipeInfo recipe, ItemStack[] stacks, ItemSlot[] slots)
    {
        var groups = recipe.Groups;
        int n = groups.Length, m = stacks.Length;
        if (n > slots.Length || m > n) return null;

        // Which group input (ignoring amounts) each stack could count as, per group.
        var accepts = new CraftingRecipeIngredient?[m, n];
        for (int s = 0; s < m; s++)
        {
            for (int g = 0; g < n; g++) accepts[s, g] = InputFor(groups[g], stacks[s]);
        }

        // Give every stack its own group (small bipartite matching; groups can overlap).
        var stackOf = Enumerable.Repeat(-1, n).ToArray();
        for (int s = 0; s < m; s++)
        {
            if (!Assign(s, accepts, stackOf, new bool[n])) return null; // something doesn't belong
        }

        var result = new List<IngredientSlot>();
        var groupFilled = new bool[n];
        int mixes = int.MaxValue;
        for (int g = 0; g < n; g++)
        {
            int s = stackOf[g];
            if (s < 0) continue;
            groupFilled[g] = true;
            var input = accepts[s, g]!;
            if (input.Quantity > 0) mixes = Math.Min(mixes, stacks[s].StackSize / input.Quantity);
        }
        // Filled slots in the bowl's order.
        for (int s = 0; s < m; s++)
        {
            int g = Array.IndexOf(stackOf, s);
            var input = accepts[s, g]!;
            result.Add(new IngredientSlot
            {
                Role = SlotRole.Filled,
                Stack = stacks[s].Clone(),
                AmountPerBatch = input.Quantity,
                AmountWrong = stacks[s].StackSize < input.Quantity,
            });
        }
        for (int g = 0; g < n; g++)
        {
            if (groupFilled[g]) continue;
            result.Add(new IngredientSlot
            {
                Role = SlotRole.Required,
                Options = groups[g].Select(input => new IngredientOption
                {
                    Accepts = Resolver.Stacks(input),
                    TypeCode = input.Name,
                    Amount = input.Quantity,
                }).Where(o => o.Accepts.Count > 0).ToList(),
            });
        }

        MatchState state;
        if (n > m) state = MatchState.Missing;
        // Every group is filled; ACA's own check decides, and refuses only over amounts.
        else state = recipe.Matches(capi.World, slots) ? MatchState.Ready : MatchState.WrongAmounts;

        return new RecipeMatch
        {
            Output = recipe.Output,
            State = state,
            Slots = result,
            AmountRule = AmountRule.AtLeast,
            Makes = recipe.Output.StackSize,
            Batches = state == MatchState.Ready && mixes != int.MaxValue ? Math.Max(1, mixes) : 0,
        };
    }

    /// <summary>The group input <paramref name="stack"/> counts as, preferring one it has enough of.</summary>
    private static CraftingRecipeIngredient? InputFor(CraftingRecipeIngredient[] group, ItemStack stack)
    {
        CraftingRecipeIngredient? loose = null;
        foreach (var input in group)
        {
            try
            {
                if (input.SatisfiesAsIngredient(stack, true)) return input;
                if (loose == null && input.SatisfiesAsIngredient(stack, false)) loose = input;
            }
            catch (Exception) { /* odd ingredient definitions: treat as no match */ }
        }
        return loose;
    }

    private static bool Assign(int s, CraftingRecipeIngredient?[,] accepts, int[] stackOf, bool[] seen)
    {
        for (int g = 0; g < stackOf.Length; g++)
        {
            if (accepts[s, g] == null || seen[g]) continue;
            seen[g] = true;
            if (stackOf[g] < 0 || Assign(stackOf[g], accepts, stackOf, seen))
            {
                stackOf[g] = s;
                return true;
            }
        }
        return false;
    }
}
