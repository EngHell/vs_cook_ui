using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace CookUi;

// Public extension API. A client mod adds a station, or recipes for one, like this:
//
//   var cookUi = capi.ModLoader.GetModSystem<CookUi.CookUiModSystem>();
//   cookUi?.RegisterStation(new MyOvenStation());                     // a dialog to attach to
//   cookUi?.RegisterRecipeSource("myoven", new MyOvenRecipes(capi));  // its recipes
//   cookUi?.RegisterRecipeSource(CookUiModSystem.FirepitStationId, new MyPotRecipes(capi));
//
// A source only describes matches as data (what's in each slot, what's missing, amounts,
// state). How that looks (layout, icons, tooltips, colours, ordering, messages) is decided by
// Cook UI alone, so every station reads the same.

/// <summary>What a station holds right now.</summary>
/// <param name="Container">The vessel recipes depend on (pot, saucepan...), if any.</param>
/// <param name="Slots">The ingredient slots in grid order; their count is the number of slots per recipe.</param>
public record StationContents(ItemStack? Container, IReadOnlyList<ItemSlot> Slots);

/// <summary>A block dialog Cook UI attaches its recipe panel to (firepit, mixing bowl...).</summary>
public interface ICookingStation
{
    /// <summary>Unique id, e.g. "firepit" or "mymod:oven"; recipe sources register against it.</summary>
    string Id { get; }

    /// <summary>Localized station name, used as the panel title.</summary>
    string Title { get; }

    /// <summary>
    /// What <paramref name="dialog"/> holds if it is this station's dialog, else null.
    /// Called every tick for each open dialog, so keep it cheap.
    /// </summary>
    StationContents? GetContents(GuiDialog dialog);
}

/// <summary>Finds which recipes of one kind (cooking, simmering...) a station's contents fit.</summary>
public interface IRecipeSource
{
    /// <summary>
    /// Whether these contents are this source's business (a pot rather than a crucible...).
    /// The panel only appears while some source applies. Called often; keep it cheap.
    /// </summary>
    bool AppliesTo(StationContents contents);

    /// <summary>
    /// Every recipe the contents can still become, in any order. Called only when the contents change.
    /// </summary>
    IReadOnlyList<RecipeMatch> FindMatches(StationContents contents);
}

/// <summary>How far the contents are from making a recipe.</summary>
public enum MatchState
{
    /// <summary>The station will make this now.</summary>
    Ready,
    /// <summary>Every slot is filled correctly but the amounts break the recipe's <see cref="AmountRule"/>.</summary>
    WrongAmounts,
    /// <summary>Matches, but makes more than the container holds.</summary>
    TooMuch,
    /// <summary>The contents fit the recipe so far; the <see cref="SlotRole.Required"/> slots are still needed.</summary>
    Missing,
}

/// <summary>How a recipe's amounts must relate, so the player can be told what's wrong.</summary>
public enum AmountRule
{
    /// <summary>Every solid ingredient has the same stack size (vanilla pot meals).</summary>
    SameInEverySlot,
    /// <summary>Every ingredient is the same multiple of its own recipe amount (ACA simmering).</summary>
    SameMultiple,
    /// <summary>Each ingredient needs at least its amount; leftovers stay (ACA kneading).</summary>
    AtLeast,
}

public enum SlotRole
{
    /// <summary>Something already in the station.</summary>
    Filled,
    /// <summary>Must still be added: one of <see cref="IngredientSlot.Options"/>.</summary>
    Required,
    /// <summary>May be added: one of <see cref="IngredientSlot.Options"/>.</summary>
    Optional,
}

/// <summary>One recipe the station's contents can become.</summary>
public sealed class RecipeMatch
{
    /// <summary>What it makes (one unit; a meal can be a filled bowl).</summary>
    public required ItemStack Output;
    /// <summary>Localized dish name when it isn't simply <see cref="Output"/>'s name (meals).</summary>
    public string? Name;
    public required MatchState State;
    /// <summary>
    /// One entry per station slot the recipe uses, in display order: filled slots, then the
    /// required ones, then optional room. Never more than the station has.
    /// </summary>
    public required List<IngredientSlot> Slots;
    public AmountRule AmountRule = AmountRule.SameInEverySlot;
    /// <summary>When Ready or TooMuch: how much it makes, in <see cref="Output"/> items, or servings if <see cref="MakesServings"/>.</summary>
    public int Makes;
    public bool MakesServings;
    /// <summary>When Ready: how many times in a row the contents can make it (ACA kneading), 0 if it uses them all.</summary>
    public int Batches;
    /// <summary>Satiety of one serving, for meals.</summary>
    public MealNutrition? Nutrition;
}

/// <summary>One station slot within a recipe.</summary>
public sealed class IngredientSlot
{
    public required SlotRole Role;
    /// <summary>Filled: the stack in the slot.</summary>
    public ItemStack? Stack;
    /// <summary>Filled: how much of <see cref="Stack"/> one batch uses (in items), if the recipe says.</summary>
    public int? AmountPerBatch;
    /// <summary>Filled: the amount doesn't fit the recipe.</summary>
    public bool AmountWrong;
    /// <summary>Filled: satiety it adds to one serving.</summary>
    public float? Satiety;
    /// <summary>
    /// Required/Optional: what may go here. Optional slots offering the same choice should share
    /// one list instance; Cook UI then decides how to lay them out together.
    /// </summary>
    public List<IngredientOption>? Options;
}

/// <summary>A kind of ingredient a slot accepts.</summary>
public sealed class IngredientOption
{
    /// <summary>Every item accepted (wildcards expanded). Return the same list instance each time where possible.</summary>
    public required List<ItemStack> Accepts;
    /// <summary>Group type code ("vegetable", "meat") used to name groups of several items.</summary>
    public string? TypeCode;
    /// <summary>Amount needed, in items (liquids in portions), if known.</summary>
    public int? Amount;
    /// <summary>Optional slots: at most this many of this kind fit.</summary>
    public int? Limit;
    /// <summary>Lowest and highest satiety per serving any accepted item adds.</summary>
    public (float min, float max)? Satiety;
}

/// <summary>Per-serving satiety of a meal: confirmed from contents, ranges for what's still needed.</summary>
public sealed class MealNutrition
{
    public readonly Dictionary<EnumFoodCategory, float> ByCategory = new();
    public float Confirmed;
    public float NeededMin, NeededMax;
    /// <summary>Most the optional room could add.</summary>
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

    public bool IsEmpty => ByCategory.Count == 0 && NeededMax <= 0 && OptionalMax <= 0;
}
