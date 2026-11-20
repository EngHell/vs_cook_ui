using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace CookUi;

public enum PredictionState
{
    /// <summary>Ingredients match; the pot will cook this.</summary>
    Ready,
    /// <summary>Everything required is present but quantities are uneven.</summary>
    RatioWrong,
    /// <summary>Matches, but makes more servings than the pot holds.</summary>
    TooMuch,
    /// <summary>Current contents fit this recipe; more ingredients are required.</summary>
    Missing,
}

/// <param name="Counts">How many pot stacks were assigned to each recipe ingredient group.</param>
/// <param name="MissingTotal">Pot slots that still need a required ingredient.</param>
public record RecipePrediction(
    CookingRecipe Recipe,
    PredictionState State,
    int Servings,
    int[] Counts,
    int MissingTotal);

/// <summary>
/// Works out which cooking recipes the current pot contents are heading toward.
/// Ingredient assignment mirrors <see cref="CookingRecipe.Matches(ItemStack[], ref int)"/>
/// (greedy, first matching ingredient group with room) so predictions agree with vanilla.
/// </summary>
public static class RecipePredictor
{
    public static List<RecipePrediction> Predict(
        IEnumerable<CookingRecipe> recipes,
        ItemStack[] stacks,
        int slotCount,
        int maxServings,
        bool dirtyPot)
    {
        var results = new List<RecipePrediction>();
        int freeSlots = slotCount - stacks.Length;

        foreach (var recipe in recipes)
        {
            if (!recipe.Enabled || recipe.Ingredients == null || recipe.Ingredients.Length == 0) continue;
            // Same rule as BlockCookingContainer.GetMatchingCookingRecipe: dirty pots only make non-food.
            if (dirtyPot && (recipe.CooksInto == null || recipe.IsFood)) continue;

            var prediction = Analyze(recipe, stacks, freeSlots, maxServings);
            if (prediction != null) results.Add(prediction);
        }

        return results
            .OrderBy(p => p.State == PredictionState.Missing ? 1 : 0)
            .ThenBy(p => p.MissingTotal)
            .ThenBy(p => p.Recipe.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static RecipePrediction? Analyze(CookingRecipe recipe, ItemStack[] stacks, int freeSlots, int maxServings)
    {
        var ingredients = recipe.Ingredients!;
        var counts = new int[ingredients.Length];

        foreach (var stack in stacks)
        {
            int idx = AssignedIngredient(ingredients, counts, stack);
            if (idx < 0) return null; // something in the pot doesn't belong in this recipe
            counts[idx]++;
        }

        int missingTotal = 0;
        for (int j = 0; j < ingredients.Length; j++)
        {
            missingTotal += Math.Max(0, ingredients[j].MinQuantity - counts[j]);
        }
        if (missingTotal > freeSlots) return null; // can't fit the rest in the pot

        if (missingTotal > 0)
        {
            return new RecipePrediction(recipe, PredictionState.Missing, 0, counts, missingTotal);
        }

        // Empty pot and a recipe with no required groups: not a useful suggestion.
        if (stacks.Length == 0) return null;

        int servings = 0;
        if (!recipe.Matches(stacks, ref servings))
        {
            // All required groups are satisfied, so a failed match means uneven quantities
            // (vanilla reports quantityServings == -1 for this).
            return new RecipePrediction(recipe, PredictionState.RatioWrong, 0, counts, 0);
        }

        var state = servings > maxServings ? PredictionState.TooMuch : PredictionState.Ready;
        return new RecipePrediction(recipe, state, servings, counts, 0);
    }

    /// <summary>Index of the ingredient group vanilla would put <paramref name="stack"/> in, or -1.</summary>
    public static int AssignedIngredient(CookingRecipeIngredient[] ingredients, int[] counts, ItemStack stack)
    {
        for (int j = 0; j < ingredients.Length; j++)
        {
            if (counts[j] < ingredients[j].MaxQuantity && ingredients[j].GetMatchingStack(stack) != null) return j;
        }
        return -1;
    }
}
