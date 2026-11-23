# Extending Cook UI

Cook UI shows, next to a cooking station's dialog, which recipes its contents can still make
and what is missing. Other client mods can add **stations** (a dialog to attach to) and
**recipe sources** (recipes for a station, new or existing).

The split is strict:

- **Your mod** describes matches as **data**: what's in each slot, what's still needed, amounts
  and state.
- **Cook UI** decides **how it looks**: layout, icons, rotating and mosaic slots, amount labels,
  colours, ordering, tooltip wording and messages. It also handles finding the dialog,
  rebuilding only when contents change, scrolling and the title bar buttons.

Every station, vanilla or modded, therefore looks and reads the same.

## Where the API lives

| File | What's in it |
|---|---|
| [`CookUi/Core/Api.cs`](CookUi/Core/Api.cs) | The contract: interfaces you implement and the data you return |
| [`CookUi/CookUiModSystem.cs`](CookUi/CookUiModSystem.cs) | Registration: `RegisterStation`, `RegisterRecipeSource`, `FirepitStationId` |
| [`CookUi/Core/IngredientResolver.cs`](CookUi/Core/IngredientResolver.cs) | Optional data helpers: wildcard expansion and satiety |

Anything not listed here or under [Reference implementations](#reference-implementations) is
an implementation detail and may change without notice.

## The contract (`Api.cs`)

### `ICookingStation`

| Member | Meaning |
|---|---|
| `string Id` | Unique id, e.g. `"mymod:oven"`. Recipe sources register against it. |
| `string Title` | Localized station name, used as the panel title. |
| `StationContents? GetContents(GuiDialog dialog)` | What the dialog holds if it's your station, else `null`. Called every tick for each open dialog, so keep it cheap. |

### `StationContents(ItemStack? Container, IReadOnlyList<ItemSlot> Slots)`

- `Container`: the vessel recipes depend on (pot, saucepan...), or `null`.
- `Slots`: the ingredient slots in grid order. Their count is the number of slots in each recipe row.

### `IRecipeSource`

| Member | Meaning |
|---|---|
| `bool AppliesTo(StationContents contents)` | Whether these contents are your business (e.g. a pot rather than a crucible). The panel and its buttons only appear while some source applies. Called often; keep it cheap. |
| `IReadOnlyList<RecipeMatch> FindMatches(StationContents contents)` | Every recipe the contents can still become, in any order. Called only when the contents change. |

Several sources can serve one station. Cook UI merges and sorts their matches.

### `RecipeMatch`: one recipe

| Field | Meaning |
|---|---|
| `ItemStack Output` (required) | What it makes, one unit. A meal can be a filled bowl. |
| `string? Name` | Localized dish name, when it isn't simply `Output`'s name. |
| `MatchState State` (required) | `Ready`, `WrongAmounts`, `TooMuch` or `Missing`. |
| `List<IngredientSlot> Slots` (required) | One entry per station slot the recipe uses, in display order: filled slots, then required ones, then optional room. Never more than the station has; Cook UI fills the rest with empty slots. |
| `AmountRule AmountRule` | How amounts must relate, so the player gets the right message on `WrongAmounts`: `SameInEverySlot` (vanilla pot), `SameMultiple` (every ingredient the same multiple of its recipe amount), `AtLeast` (each ingredient needs at least its amount). |
| `int Makes`, `bool MakesServings` | When `Ready` or `TooMuch`: how much it makes, in `Output` items, or in servings if `MakesServings`. |
| `int Batches` | When `Ready`: how many times in a row the contents can make it, or 0 if one go uses everything. |
| `MealNutrition? Nutrition` | Satiety of one serving, for meals. |

### `IngredientSlot`: one station slot within a recipe

| Field | Used for | Meaning |
|---|---|---|
| `SlotRole Role` (required) | all | `Filled`, `Required` or `Optional`. |
| `ItemStack? Stack` | Filled | The stack in the slot. |
| `int? AmountPerBatch` | Filled | How much of it one batch uses, in items. |
| `bool AmountWrong` | Filled | The amount doesn't fit the recipe (drawn in orange). |
| `float? Satiety` | Filled | Satiety it adds to one serving. |
| `List<IngredientOption>? Options` | Required, Optional | What may go here. |

Optional slots that offer the same choice should **share one `Options` list instance**. Cook UI
then lays them out together: one kind per slot when they fit, otherwise every slot accepts any
kind and shows a small mosaic.

### `IngredientOption`: a kind of ingredient a slot accepts

| Field | Meaning |
|---|---|
| `List<ItemStack> Accepts` (required) | Every accepted item, wildcards expanded. Return the **same list instance** each time where possible, because Cook UI caches icons by it. |
| `string? TypeCode` | Group type code (`"vegetable"`, `"fruit"`...), used to name groups of several items through the game's lang files. |
| `int? Amount` | Amount needed, in items (liquids in portions: 100 portions is 1L of water). |
| `int? Limit` | Optional slots only: at most this many of this kind fit. |
| `(float min, float max)? Satiety` | Lowest and highest satiety per serving any accepted item adds. |

A slot with several options rotates through them (required) or shows them as a mosaic
(optional). An option accepting items of several families rotates through those families.
Both are Cook UI's decision, not yours.

### `MealNutrition`

Per-serving satiety: `Confirmed` (from what's in), `NeededMin`/`NeededMax` (range for what's
still needed), `OptionalMax` (most the optional room could add) and `ByCategory`. Helpers:
`AddConfirmed(category, sat)` and `AddNeeded((min, max))`.

## Example

A station for your own oven dialog with a recipe source, registered only when Cook UI is
installed:

```csharp
using System.Collections.Generic;
using System.Linq;
using CookUi;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

/// <summary>Attaches Cook UI's panel to my oven's dialog.</summary>
public class MyOvenStation : ICookingStation
{
    public string Id => "mymod:oven";
    public string Title => Lang.Get("mymod:oven-recipes");

    public StationContents? GetContents(GuiDialog dialog)
    {
        if (dialog is not GuiDialogMyOven oven) return null;
        var inv = oven.Inventory;
        // Slots 1-4 are the oven's ingredient slots; slot 0 is fuel.
        var slots = Enumerable.Range(1, 4).Select(i => inv[i]).ToArray();
        return new StationContents(null, slots);
    }
}

/// <summary>Tells Cook UI which oven recipes the contents fit.</summary>
public class MyOvenRecipes : IRecipeSource
{
    private readonly IEnumerable<MyOvenRecipe> recipes;

    public MyOvenRecipes(IEnumerable<MyOvenRecipe> recipes) => this.recipes = recipes;

    public bool AppliesTo(StationContents contents) => true;

    public IReadOnlyList<RecipeMatch> FindMatches(StationContents contents)
    {
        var matches = new List<RecipeMatch>();
        var flour = contents.Slots[0].Itemstack;
        foreach (var recipe in recipes)
        {
            if (flour != null && !recipe.FlourItems.Any(f => f.Satisfies(flour))) continue;

            var slots = new List<IngredientSlot>();
            if (flour != null)
            {
                slots.Add(new IngredientSlot
                {
                    Role = SlotRole.Filled,
                    Stack = flour,
                    AmountPerBatch = 2,
                    AmountWrong = flour.StackSize < 2,
                });
            }
            else
            {
                slots.Add(new IngredientSlot
                {
                    Role = SlotRole.Required,
                    Options = new() { new IngredientOption { Accepts = recipe.FlourItems, Amount = 2 } },
                });
            }
            slots.Add(new IngredientSlot
            {
                Role = SlotRole.Optional,
                Options = new() { new IngredientOption { Accepts = recipe.FillingItems, TypeCode = "fruit", Limit = 1 } },
            });

            matches.Add(new RecipeMatch
            {
                Output = recipe.Output,
                State = flour == null ? MatchState.Missing
                      : flour.StackSize < 2 ? MatchState.WrongAmounts
                      : MatchState.Ready,
                AmountRule = AmountRule.AtLeast,
                Makes = 1,
                Batches = flour == null ? 0 : flour.StackSize / 2,
                Slots = slots,
            });
        }
        return matches;
    }
}

public class MyModSystem : ModSystem
{
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        // Cook UI is optional: only touch its types when it's loaded.
        if (api.ModLoader.IsModEnabled("cookui")) CookUiCompat.Register(api);
    }
}

/// <summary>Kept in its own class so the game never loads Cook UI's types when it's absent.</summary>
internal static class CookUiCompat
{
    public static void Register(ICoreClientAPI api)
    {
        var cookUi = api.ModLoader.GetModSystem<CookUiModSystem>();
        cookUi.RegisterStation(new MyOvenStation());
        cookUi.RegisterRecipeSource("mymod:oven", new MyOvenRecipes(LoadMyRecipes(api)));

        // Or add recipes to a station that already exists, such as the vanilla firepit:
        // cookUi.RegisterRecipeSource(CookUiModSystem.FirepitStationId, new MyPotRecipes(api));

        // A pot-like station using vanilla-style CookingRecipes can reuse Cook UI's own source:
        // cookUi.RegisterRecipeSource("mymod:wok", new CookingRecipeSource(api, () => myWokRecipes));
    }
}
```

`GuiDialogMyOven`, `MyOvenRecipe` and `LoadMyRecipes` stand for your own mod's types.

### Registration notes

- `RegisterStation` and `RegisterRecipeSource` work at any point on the client, before or after
  Cook UI starts. A source can be registered for a station that is registered later.
- Station ids are unique: registering an id twice keeps the first station and logs a warning.
- If your source throws, Cook UI logs it with your class name and keeps the other sources working.

## Setting up your project

- Reference `CookUi.dll` (from the [release zip](https://github.com/EngHell/vs_cook_ui/releases))
  with `<Private>false</Private>`, so it isn't copied into your mod.
- **Optional integration** (recommended): don't list `cookui` under `dependencies` in your
  `modinfo.json`, because Vintage Story treats every listed dependency as required. Check
  `IsModEnabled("cookui")` and keep every Cook UI type in a separate class, as in the example,
  so nothing tries to load `CookUi.dll` when it's missing.
- **Required integration:** list `"cookui": "0.0.4"` (or later) under `dependencies` and call
  the API directly.
- The mod id is `cookui`, and the API exists from version **0.0.4**.

## Helpers: `IngredientResolver`

Data only, no presentation. Create one per source with `new IngredientResolver(capi.World)`.

| Member | Meaning |
|---|---|
| `Stacks(CookingRecipeIngredient)`, `Stacks(CraftingRecipeIngredient)` | Expand an ingredient (wildcards, variant filters, tags) into the items it accepts. Cached, and the same list instance is returned for the same ingredient, which suits `IngredientOption.Accepts`. |
| `SatietyRange(CookingRecipeIngredient, EntityAgent?)` | Lowest and highest per-serving satiety the ingredient's items add. |
| `static NutritionOf(world, stack, ingredient, entity)` | What one serving's worth of a stack adds to a meal; matches the game's own tooltip. |
| `static SpoilageMul(world, stack, entity)` | Satiety multiplier from spoilage (1 when fresh). |
| `static Representative(stacks)`, `SafeName(stack)`, `Capitalize(text)` | A typical item of a group, a name that never throws, capitalization. |

## Reference implementations

Cook UI's own support is registered through the same API (see `StartClientSide` in
`CookUiModSystem.cs`), so these files are working examples:

| File | Shows |
|---|---|
| [`CookUi/Cooking/FirepitStation.cs`](CookUi/Cooking/FirepitStation.cs) | The smallest station. |
| [`CookUi/Compat/AculinaryArtillery/MixingBowlStation.cs`](CookUi/Compat/AculinaryArtillery/MixingBowlStation.cs) | A station for another mod's dialog, recognized by type name. |
| [`CookUi/Cooking/CookingRecipeSource.cs`](CookUi/Cooking/CookingRecipeSource.cs) | A full source with servings, optional room and nutrition. Reusable for any pot-like station: `new CookingRecipeSource(capi, () => recipes)`. |
| [`CookUi/Compat/AculinaryArtillery/SimmerRecipeSource.cs`](CookUi/Compat/AculinaryArtillery/SimmerRecipeSource.cs) | Every ingredient required, `AmountRule.SameMultiple`. |
| [`CookUi/Compat/AculinaryArtillery/KneadingRecipeSource.cs`](CookUi/Compat/AculinaryArtillery/KneadingRecipeSource.cs) | Slots accepting several different items, `AmountRule.AtLeast`, `Batches`. |

## Not part of the API

- **Presentation (internal, not reachable from other mods):** `RowBuilder`, `RowView`,
  `RecipeWindow`, `GuiElementRecipeRows`, `IngredientIcons`, `RecipeFormat`, `TitleBarButtons`,
  `TooltipRenderer` in `CookUi/Core/`.
- **Public for now, but don't depend on them:** `CookingRecipePredictor` and `RecipePrediction`,
  `CookUiConfig`, and the A Culinary Artillery compat types in `CookUi/Compat/` (the stations and
  sources are fine to read as examples). These may become internal in a later version.
