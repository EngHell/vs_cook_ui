using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace CookUi.Compat.AculinaryArtillery;

/// <summary>
/// Read-only access to A Culinary Artillery's recipes and classes without referencing its DLL:
/// we look its types up by name, so Cook UI still loads (and does nothing extra) without it.
/// </summary>
internal static class Aca
{
    public const string ModId = "aculinaryartillery";
    private const string RegistrySystem = "ACulinaryArtillery.ACARecipeRegistrySystem";
    public const string MixingBowlDialog = "ACulinaryArtillery.GuiDialogBlockEntityMixingBowl";
    public const string SaucepanBlock = "ACulinaryArtillery.BlockSaucepan";

    /// <summary>A list field of ACA's recipe registry system (synced from the server), or null.</summary>
    public static IList? RegistryList(ICoreClientAPI capi, string field)
    {
        try
        {
            var system = capi.ModLoader.GetModSystem(RegistrySystem);
            if (system == null) return null;
            return system.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance)?.GetValue(system) as IList;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="obj"/>'s class is, or derives from, the class named <paramref name="fullName"/>.</summary>
    public static bool IsA(object? obj, string fullName)
    {
        for (var t = obj?.GetType(); t != null; t = t.BaseType)
        {
            if (t.FullName == fullName) return true;
        }
        return false;
    }
}

/// <summary>ACA mixing bowl recipes: plain <see cref="CookingRecipe"/>s kept in ACA's own registry.</summary>
public sealed class AcaMixingRecipeProvider : IRecipeProvider<CookingRecipe>
{
    private readonly ICoreClientAPI capi;

    public AcaMixingRecipeProvider(ICoreClientAPI capi) => this.capi = capi;

    public IEnumerable<CookingRecipe> GetRecipes() =>
        Aca.RegistryList(capi, "MixingRecipes")?.OfType<CookingRecipe>() ?? Enumerable.Empty<CookingRecipe>();
}

/// <summary>Our view of one ACA simmer recipe (saucepan/cauldron on a firepit).</summary>
public sealed class SimmerRecipeInfo
{
    public required string Code;
    /// <summary>Every ingredient is required, one slot each, in multiples of its Quantity.</summary>
    public required CraftingRecipeIngredient[] Ingredients;
    /// <summary>What one batch makes.</summary>
    public required ItemStack Output;
    /// <summary>ACA's own matcher: batches the stacks make, 0 when they're not in proportion.</summary>
    public required System.Func<List<ItemStack>, int> Match;
}

/// <summary>ACA simmer recipes, wrapped once per recipe object.</summary>
public sealed class AcaSimmerRecipeProvider : IRecipeProvider<SimmerRecipeInfo>
{
    private readonly ICoreClientAPI capi;
    private readonly ConditionalWeakTable<object, SimmerRecipeInfo?> wrapped = new();

    public AcaSimmerRecipeProvider(ICoreClientAPI capi) => this.capi = capi;

    public IEnumerable<SimmerRecipeInfo> GetRecipes()
    {
        var list = Aca.RegistryList(capi, "SimmerRecipes");
        if (list == null) yield break;
        foreach (var recipe in list)
        {
            if (recipe == null) continue;
            var info = wrapped.GetValue(recipe, Wrap);
            if (info != null) yield return info;
        }
    }

    private static SimmerRecipeInfo? Wrap(object recipe)
    {
        try
        {
            var t = recipe.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            if (t.GetProperty("Enabled", flags)?.GetValue(recipe) is false) return null;
            if (t.GetField("Ingredients", flags)?.GetValue(recipe) is not CraftingRecipeIngredient[] ingredients || ingredients.Length == 0) return null;
            if (t.GetField("Simmering", flags)?.GetValue(recipe) is not CombustibleProperties simmering) return null;
            if (simmering.SmeltedStack?.ResolvedItemstack is not { } output) return null;
            var match = t.GetMethod("Match", flags, new[] { typeof(List<ItemStack>) });
            if (match == null) return null;

            return new SimmerRecipeInfo
            {
                Code = t.GetField("Code", flags)?.GetValue(recipe) as string ?? "?",
                Ingredients = ingredients,
                Output = output,
                Match = stacks =>
                {
                    try { return match.Invoke(recipe, new object[] { stacks }) is int n ? n : 0; }
                    catch (Exception) { return 0; }
                },
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Our view of one ACA kneading (dough) recipe, made in a mixing bowl without a pot.</summary>
public sealed class KneadingRecipeInfo
{
    /// <summary>
    /// One entry per ingredient group; each group takes exactly one slot and accepts any one of
    /// its inputs, at least that input's Quantity.
    /// </summary>
    public required CraftingRecipeIngredient[][] Groups;
    /// <summary>What one mixing cycle makes.</summary>
    public required ItemStack Output;
    /// <summary>ACA's own check (DoughRecipe.Matches) against the bowl's ingredient slots.</summary>
    public required System.Func<IWorldAccessor, ItemSlot[], bool> Matches;
}

/// <summary>ACA kneading recipes, wrapped once per recipe object.</summary>
public sealed class AcaKneadingRecipeProvider : IRecipeProvider<KneadingRecipeInfo>
{
    private readonly ICoreClientAPI capi;
    private readonly ConditionalWeakTable<object, KneadingRecipeInfo?> wrapped = new();

    public AcaKneadingRecipeProvider(ICoreClientAPI capi) => this.capi = capi;

    public IEnumerable<KneadingRecipeInfo> GetRecipes()
    {
        var list = Aca.RegistryList(capi, "DoughRecipes");
        if (list == null) yield break;
        foreach (var recipe in list)
        {
            if (recipe == null) continue;
            var info = wrapped.GetValue(recipe, Wrap);
            if (info != null) yield return info;
        }
    }

    private static KneadingRecipeInfo? Wrap(object recipe)
    {
        try
        {
            var t = recipe.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            if (t.GetProperty("Enabled", flags)?.GetValue(recipe) is false) return null;
            if (t.GetField("Output", flags)?.GetValue(recipe) is not JsonItemStack { ResolvedItemstack: { } output }) return null;
            if (t.GetField("Ingredients", flags)?.GetValue(recipe) is not Array ingredients || ingredients.Length == 0) return null;

            var groups = new CraftingRecipeIngredient[ingredients.Length][];
            for (int i = 0; i < ingredients.Length; i++)
            {
                var group = ingredients.GetValue(i);
                if (group?.GetType().GetField("Inputs", flags)?.GetValue(group) is not CraftingRecipeIngredient[] inputs || inputs.Length == 0) return null;
                groups[i] = inputs;
            }

            var matches = t.GetMethod("Matches", flags, new[] { typeof(IWorldAccessor), typeof(ItemSlot[]) });
            if (matches == null) return null;

            return new KneadingRecipeInfo
            {
                Groups = groups,
                Output = output,
                Matches = (world, slots) =>
                {
                    try { return matches.Invoke(recipe, new object[] { world, slots }) is true; }
                    catch (Exception) { return false; }
                },
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
