using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CookUi;

/// <summary>
/// Turns sources' <see cref="RecipeMatch"/> data into what the panel draws. Every presentation
/// choice lives here: ordering, icons and their rotation, mosaics, amount labels, colours and
/// all tooltip text. Sources have no say in any of it.
/// </summary>
internal static class RowBuilder
{
    /// <summary>Rows for <paramref name="matches"/>, best first, each exactly <paramref name="slotCount"/> wide.</summary>
    public static List<RowView> Build(IEnumerable<RecipeMatch> matches, int slotCount) =>
        matches
            .OrderBy(m => Rank(m.State))
            .ThenBy(m => m.Slots.Count(s => s.Role == SlotRole.Required))
            .ThenBy(ResultName, StringComparer.OrdinalIgnoreCase)
            .Select(m => Row(m, slotCount))
            .ToList();

    private static int Rank(MatchState state) => state switch
    {
        MatchState.Ready => 0,
        MatchState.TooMuch or MatchState.WrongAmounts => 1,
        _ => 2,
    };

    private static string ResultName(RecipeMatch m) => m.Name ?? IngredientResolver.SafeName(m.Output);

    private static RowView Row(RecipeMatch m, int slotCount)
    {
        var slots = new List<SlotView>();
        var source = m.Slots.Take(slotCount).ToList();
        for (int i = 0; i < source.Count; i++)
        {
            var slot = source[i];
            switch (slot.Role)
            {
                case SlotRole.Filled:
                    slots.Add(Filled(slot));
                    break;
                case SlotRole.Required:
                    slots.Add(Required(slot.Options));
                    break;
                default:
                    // Optional slots offering the same choice are laid out together.
                    int end = i + 1;
                    while (end < source.Count && source[end].Role == SlotRole.Optional && source[end].Options == slot.Options) end++;
                    slots.AddRange(OptionalRun(slot.Options, end - i));
                    i = end - 1;
                    break;
            }
        }
        while (slots.Count < slotCount) slots.Add(SlotView.Empty());

        return new RowView { Result = Result(m), State = m.State, Ingredients = slots };
    }

    private static SlotView Filled(IngredientSlot slot)
    {
        if (slot.Stack == null) return SlotView.Empty();
        var tooltip = new StringBuilder(IngredientResolver.SafeName(slot.Stack));
        if (slot.Satiety is { } sat) tooltip.Append('\n').Append(Lang.Get("cookui:sat-each", RecipeFormat.Round(sat)));
        if (slot.AmountPerBatch is { } per) tooltip.Append('\n').Append(Lang.Get("cookui:per-batch", RecipeFormat.AmountOf(slot.Stack, per)));
        return new SlotView
        {
            Kind = SlotKind.Present,
            Icon = slot.Stack,
            Tooltip = tooltip.ToString(),
            Count = RecipeFormat.AmountLabel(slot.Stack),
            Warn = slot.AmountWrong,
        };
    }

    /// <summary>A still-needed slot: one kind (rotating through its categories) or any of several kinds.</summary>
    private static SlotView Required(List<IngredientOption>? options)
    {
        if (options == null || options.Count == 0) return new SlotView { Kind = SlotKind.Required };
        if (options.Count == 1) return Ghost(SlotKind.Required, options[0]);

        // Several kinds: rotate through one icon per kind, each listed with its amount.
        var icons = new List<ItemStack>();
        var labels = new List<string>();
        var amounts = new HashSet<string?>();
        foreach (var opt in options)
        {
            var info = IngredientIcons.Describe(opt);
            if (info.Icon == null) continue;
            string? amount = Amount(opt, info);
            amounts.Add(amount);
            icons.Add(info.Icon);
            labels.Add(amount == null ? info.Label : info.Label + " (" + amount + ")");
        }

        return new SlotView
        {
            Kind = SlotKind.Required,
            Icon = icons.FirstOrDefault(),
            Cycle = icons.Count > 1 ? icons : null,
            Categories = labels.Count > 1 ? labels : null,
            Tooltip = labels.Count == 1 ? Lang.Get("cookui:slot-needed", labels[0]) : Lang.Get("cookui:slot-needed-any"),
            Count = amounts.Count == 1 ? amounts.First() : null,
        };
    }

    /// <summary>
    /// <paramref name="count"/> optional slots offering the same kinds. If every kind can have its
    /// own slot, each slot shows one kind (round-robin, within each kind's limit); otherwise every
    /// slot accepts any kind and draws a small mosaic of them.
    /// </summary>
    private static IEnumerable<SlotView> OptionalRun(List<IngredientOption>? options, int count)
    {
        if (options == null || options.Count == 0)
        {
            for (int i = 0; i < count; i++) yield return SlotView.Empty();
            yield break;
        }

        if (options.Count <= count)
        {
            var left = options.Select(o => o.Limit ?? int.MaxValue).ToArray();
            int made = 0;
            bool added = true;
            while (made < count && added)
            {
                added = false;
                for (int i = 0; i < options.Count && made < count; i++)
                {
                    if (left[i] <= 0) continue;
                    left[i]--;
                    made++;
                    added = true;
                    yield return Ghost(SlotKind.Optional, options[i]);
                }
            }
            for (; made < count; made++) yield return SlotView.Empty();
            yield break;
        }

        // No corner amount on mosaic slots (it would cover the "+N" cell): each line carries its own.
        var choices = options.Select(o => Ghost(SlotKind.Optional, o)).ToList();
        var tooltip = new StringBuilder(Lang.Get("cookui:slot-optional-any"));
        for (int i = 0; i < options.Count; i++)
        {
            var opt = options[i];
            string label = IngredientIcons.Describe(opt).Label;
            if (choices[i].Count != null) label += " (" + choices[i].Count + ")";
            string limit = opt.Limit?.ToString() ?? "∞";
            tooltip.Append('\n').Append(opt.Satiety is { } s
                ? Lang.Get("cookui:slot-choice-sat", label, limit, RecipeFormat.Range(s.min, s.max))
                : Lang.Get("cookui:slot-choice", label, limit));
        }
        string text = tooltip.ToString();
        for (int i = 0; i < count; i++)
        {
            yield return new SlotView { Kind = SlotKind.Optional, Choices = choices, Tooltip = text };
        }
    }

    /// <summary>A shadow icon for one kind of ingredient.</summary>
    private static SlotView Ghost(SlotKind kind, IngredientOption opt)
    {
        var info = IngredientIcons.Describe(opt);
        bool rotates = info.Categories.Count > 1;
        var tooltip = new StringBuilder(Lang.Get(kind == SlotKind.Required ? "cookui:slot-needed" : "cookui:slot-optional", info.Label));
        if (opt.Satiety is { } s) tooltip.Append('\n').Append(Lang.Get("cookui:sat-each", RecipeFormat.Range(s.min, s.max)));
        return new SlotView
        {
            Kind = kind,
            Icon = info.Icon,
            Cycle = rotates ? info.Categories.Select(c => c.Icon).ToList() : null,
            Categories = rotates ? info.Categories.Select(c => c.Name).ToList() : null,
            Tooltip = tooltip.ToString(),
            Count = Amount(opt, info),
        };
    }

    private static string? Amount(IngredientOption opt, IngredientInfo info)
    {
        if (opt.Amount is not { } amount) return null;
        var like = info.Icon ?? opt.Accepts.FirstOrDefault();
        return like == null ? amount.ToString() : RecipeFormat.AmountOf(like, amount);
    }

    private static SlotView Result(RecipeMatch m)
    {
        var icon = m.Output.Clone();
        icon.StackSize = 1;
        bool made = m.State is MatchState.Ready or MatchState.TooMuch;
        string? amount = !made ? null : m.MakesServings ? m.Makes.ToString() : RecipeFormat.AmountOf(icon, m.Makes);
        int missing = m.Slots.Count(s => s.Role == SlotRole.Required);

        string status = m.State switch
        {
            MatchState.Ready when m.MakesServings => Lang.Get("cookui:servings", m.Makes),
            MatchState.Ready when m.Batches > 1 => Lang.Get("cookui:makes-batches", amount, m.Batches),
            MatchState.Ready => Lang.Get("cookui:makes", amount),
            MatchState.TooMuch => Lang.Get("cookui:too-much", m.Makes),
            MatchState.WrongAmounts => Lang.Get(m.AmountRule switch
            {
                AmountRule.SameMultiple => "cookui:amount-same-multiple",
                AmountRule.AtLeast => "cookui:amount-at-least",
                _ => "cookui:ratio-wrong",
            }),
            _ => Lang.Get("cookui:needs-more", missing),
        };

        var tooltip = new StringBuilder(ResultName(m)).Append('\n').Append(status);
        if (m.Nutrition is { IsEmpty: false } n) AppendNutrition(tooltip, m, n);

        return new SlotView
        {
            Kind = SlotKind.Present,
            Icon = m.Output,
            Tooltip = tooltip.ToString(),
            Count = amount,
            Warn = m.State == MatchState.TooMuch,
        };
    }

    private static void AppendNutrition(StringBuilder sb, RecipeMatch m, MealNutrition n)
    {
        if (n.NeededMax <= 0)
        {
            // Everything required is in: these are the real numbers.
            if (m.State == MatchState.Ready && m.MakesServings)
                sb.Append('\n').Append(Lang.Get("cookui:sat-ready", RecipeFormat.Round(n.Confirmed), RecipeFormat.Round(n.Confirmed * m.Makes)));
            else
                sb.Append('\n').Append(Lang.Get("cookui:sat-per-serving", RecipeFormat.Round(n.Confirmed)));
        }
        else
        {
            if (n.Confirmed > 0) sb.Append('\n').Append(Lang.Get("cookui:sat-so-far", RecipeFormat.Round(n.Confirmed)));
            sb.Append('\n').Append(Lang.Get("cookui:sat-finished",
                RecipeFormat.Range(n.Confirmed + n.NeededMin, n.Confirmed + n.NeededMax)));
        }

        foreach (var kv in n.ByCategory.OrderByDescending(kv => kv.Value))
        {
            string cat = Lang.Get("foodcategory-" + kv.Key.ToString().ToLowerInvariant());
            sb.Append('\n').Append(Lang.Get("nutrition-facts-line-satiety", cat, RecipeFormat.Round(kv.Value)));
        }

        if (n.OptionalMax > 0) sb.Append('\n').Append(Lang.Get("cookui:sat-extras", RecipeFormat.Round(n.OptionalMax)));
    }
}
