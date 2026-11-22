using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CookUi.Compat.AculinaryArtillery;

/// <summary>
/// ACA mixing bowl without a pot: it kneads dough (Expanded Foods' doughs, breads, fillings...).
/// A kneading recipe is a list of ingredient groups; each group takes exactly one of the six
/// slots and accepts any one of its inputs, at least that input's amount. Nothing else may be in
/// the bowl. Each mixing cycle makes one batch and uses one amount of each, leaving the rest.
/// </summary>
public sealed class KneadingWindow : RecipeWindow<GuiDialogBlockEntity, StationState>
{
    private readonly IRecipeProvider<KneadingRecipeInfo> provider;
    private IngredientIcons? icons;

    public KneadingWindow(ICoreClientAPI capi, CookUiConfig config) : base(capi, config)
    {
        provider = new AcaKneadingRecipeProvider(capi);
    }

    public override string Id => "aca-kneading";
    protected override string TitleLangKey => "cookui:panel-title-kneading";

    private IngredientIcons Icons => icons ??= new IngredientIcons(capi.World);

    protected override bool IsHost(GuiDialogBlockEntity dlg) => Aca.IsA(dlg, Aca.MixingBowlDialog);

    protected override StationState? GetState(GuiDialogBlockEntity host)
    {
        var slots = MixingBowl.Ingredients(host.Inventory);
        if (slots == null) return null;
        var input = host.Inventory[MixingBowl.InputSlot].Itemstack;
        // With a pot in, the bowl mixes meals instead (MixingBowlWindow).
        if (input?.Collectible is BlockCookingContainer) return null;
        return new StationState(input, slots);
    }

    /// <param name="InputOf">For each stack in the bowl, the group input it counts as.</param>
    /// <param name="GroupOf">For each stack in the bowl, the recipe group it fills.</param>
    /// <param name="Mixes">Cycles the current contents last for (when ready).</param>
    private sealed record Prediction(
        KneadingRecipeInfo Recipe, string Name, PredictionState State, int Missing,
        int[] GroupOf, CraftingRecipeIngredient[] InputOf, int Mixes);

    protected override RecipeGrid BuildGrid(StationState state)
    {
        var slotArray = state.Slots.ToArray();
        var stacks = Stacks(slotArray);
        int slotCount = slotArray.Length;

        // Over a thousand recipes: listing them all for an empty bowl isn't useful.
        if (stacks.Length == 0)
        {
            return new RecipeGrid(slotCount, new List<RowView>(), Lang.Get("cookui:knead-add-ingredient"));
        }

        var rows = provider.GetRecipes()
            .Select(r => Analyze(r, stacks, slotArray))
            .Where(p => p != null).Cast<Prediction>()
            .OrderBy(p => p.State switch { PredictionState.Ready => 0, PredictionState.RatioWrong => 1, _ => 2 })
            .ThenBy(p => p.Missing)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => BuildRow(p, stacks, slotCount))
            .ToList();
        return new RecipeGrid(slotCount, rows, Lang.Get("cookui:no-recipe-fits"));
    }

    private Prediction? Analyze(KneadingRecipeInfo recipe, ItemStack[] stacks, ItemSlot[] slots)
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
        var groupOf = new int[m];
        for (int g = 0; g < n; g++)
        {
            if (stackOf[g] >= 0) groupOf[stackOf[g]] = g;
        }
        var inputOf = Enumerable.Range(0, m).Select(s => accepts[s, groupOf[s]]!).ToArray();

        string name = IngredientIcons.SafeName(recipe.Output);
        int missing = n - m;
        if (missing > 0) return new Prediction(recipe, name, PredictionState.Missing, missing, groupOf, inputOf, 0);

        if (!recipe.Matches(capi.World, slots))
        {
            // Every group is filled, so ACA refused over amounts: some slot is below its input's amount.
            return new Prediction(recipe, name, PredictionState.RatioWrong, 0, groupOf, inputOf, 0);
        }

        int mixes = Enumerable.Range(0, m)
            .Select(s => inputOf[s].Quantity > 0 ? stacks[s].StackSize / inputOf[s].Quantity : int.MaxValue)
            .Min();
        return new Prediction(recipe, name, PredictionState.Ready, 0, groupOf, inputOf, Math.Max(1, mixes));
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

    private RowView BuildRow(Prediction p, ItemStack[] stacks, int slotCount)
    {
        var slots = new List<SlotView>();
        var filled = new bool[p.Recipe.Groups.Length];

        for (int s = 0; s < stacks.Length; s++)
        {
            var stack = stacks[s];
            var input = p.InputOf[s];
            filled[p.GroupOf[s]] = true;
            string tooltip = IngredientIcons.SafeName(stack) + "\n" + Lang.Get("cookui:knead-per-mix", AmountOf(stack, input.Quantity));
            slots.Add(new SlotView
            {
                Kind = SlotKind.Present,
                Slot = Dummy(stack.Clone()),
                Tooltip = tooltip,
                Count = AmountLabel(stack),
                Warn = stack.StackSize < input.Quantity,
            });
        }

        for (int g = 0; g < filled.Length && slots.Count < slotCount; g++)
        {
            if (!filled[g]) slots.Add(Ghost(p.Recipe.Groups[g]));
        }

        while (slots.Count < slotCount) slots.Add(new SlotView { Kind = SlotKind.Empty });

        return new RowView { Meal = ResultSlot(p), State = p.State, Ingredients = slots };
    }

    /// <summary>A missing group: its one input, or a slot rotating through all the inputs it accepts.</summary>
    private SlotView Ghost(CraftingRecipeIngredient[] group)
    {
        if (group.Length == 1)
        {
            var ing = group[0];
            var info = Icons.Get(ing);
            string amount = InputAmount(ing, info);
            bool rotates = info.Categories.Count > 1;
            return new SlotView
            {
                Kind = SlotKind.Required,
                Slot = info.Icon == null ? null : Dummy(info.Icon.Clone()),
                Cycle = rotates ? info.Categories.Select(c => Dummy(c.Icon.Clone())).ToList() : null,
                Categories = rotates ? info.Categories.Select(c => c.Name).ToList() : null,
                Tooltip = Lang.Get("cookui:slot-needed", info.Label) + "\n" + Lang.Get("cookui:knead-per-mix", amount),
                Count = amount,
            };
        }

        // Several inputs: rotate through one icon per input, each listed with its own amount.
        var icons = new List<ItemSlot>();
        var labels = new List<string>();
        var amounts = new HashSet<string>();
        foreach (var ing in group)
        {
            var info = Icons.Get(ing);
            if (info.Icon == null) continue;
            string amount = InputAmount(ing, info);
            amounts.Add(amount);
            icons.Add(Dummy(info.Icon.Clone()));
            labels.Add(info.Label + " (" + amount + ")");
        }

        return new SlotView
        {
            Kind = SlotKind.Required,
            Slot = icons.FirstOrDefault(),
            Cycle = icons.Count > 1 ? icons : null,
            Categories = labels.Count > 1 ? labels : null,
            Tooltip = labels.Count > 1 ? Lang.Get("cookui:slot-needed-any") : labels.Count == 1 ? Lang.Get("cookui:slot-needed", labels[0]) : null,
            Count = amounts.Count == 1 ? amounts.First() : null,
        };
    }

    private static string InputAmount(CraftingRecipeIngredient ing, IngredientInfo info)
    {
        var sample = info.Icon ?? ing.ResolvedItemStack;
        return sample == null ? ing.Quantity.ToString() : AmountOf(sample, ing.Quantity);
    }

    private SlotView ResultSlot(Prediction p)
    {
        var icon = p.Recipe.Output.Clone();
        string perMix = AmountLabel(icon) ?? icon.StackSize.ToString();
        icon.StackSize = 1;

        string status = p.State switch
        {
            PredictionState.Ready => Lang.Get("cookui:knead-ready", perMix, p.Mixes),
            PredictionState.RatioWrong => Lang.Get("cookui:knead-short"),
            _ => Lang.Get("cookui:needs-more", p.Missing),
        };

        return new SlotView
        {
            Kind = SlotKind.Present,
            Slot = Dummy(icon),
            Tooltip = p.Name + "\n" + status,
            Count = p.State == PredictionState.Ready ? perMix : null,
        };
    }
}
