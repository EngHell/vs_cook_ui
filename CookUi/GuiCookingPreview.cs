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
/// Non-interactive panel that sits beside an open firepit dialog while it holds a cooking pot.
/// One row per recipe the pot can still become: the meal's icon, then the pot's slots with the
/// ingredients already in it and "shadows" of the ones still needed or allowed.
/// </summary>
public class GuiCookingPreview : HudElement
{
    private const double GapPx = 6;
    private const int MaxVisibleRows = 6;
    private const double ScrollbarWidth = 20;
    private const double ScrollbarGap = 6;

    private GuiDialogBlockEntityFirepit? target;
    private string lastSignature = "";
    private IngredientIcons? icons;
    private readonly CookUiConfig config;
    /// <summary>Firepit dialog the current show/hide choice belongs to; reset each time a firepit opens.</summary>
    private GuiDialogBlockEntityFirepit? sessionDlg;
    /// <summary>Whether the player wants the panel shown for this firepit (starts as the auto-open setting).</summary>
    private bool panelWanted;
    private ElementBounds? rowsBounds;

    public GuiCookingPreview(ICoreClientAPI capi, CookUiConfig config) : base(capi)
    {
        this.config = config;
    }

    public override bool Focusable => false;
    public override double DrawOrder => 0.21; // just above the firepit dialog (0.2)
    // Mouse events stay on (default) so the title bar buttons work and clicks on the
    // panel don't fall through to the world.
    public override bool ShouldReceiveKeyboardEvents() => false;

    /// <summary>Called periodically by the mod system; shows, refreshes or hides the panel.</summary>
    public void Update()
    {
        var dlg = capi.Gui.OpenedGuis.OfType<GuiDialogBlockEntityFirepit>().FirstOrDefault();
        if (dlg != sessionDlg)
        {
            sessionDlg = dlg;
            panelWanted = config.AutoOpen;
        }

        if (dlg != null)
        {
            GuiElementFirepitButtons.AttachTo(capi, dlg, () => new GuiElementFirepitButtons(
                capi, dlg, () => HasPot(dlg), IsOpened, () => config.AutoOpen, TogglePanel, ToggleAutoOpen));
        }

        var inv = dlg?.Inventory as InventorySmelting;
        if (dlg == null || inv == null || !HasPot(dlg) || !panelWanted)
        {
            target = dlg;
            lastSignature = "";
            if (IsOpened()) TryClose();
            return;
        }

        target = dlg;
        string sig = Signature(inv);
        if (sig != lastSignature || SingleComposer == null)
        {
            lastSignature = sig;
            Compose(inv);
        }

        if (!IsOpened()) TryOpen();
    }

    private static bool HasPot(GuiDialogBlockEntityFirepit dlg) =>
        dlg.Inventory is InventorySmelting inv && inv.HaveCookingContainer && inv[1].Itemstack?.Collectible is BlockCookingContainer;

    private void TogglePanel()
    {
        panelWanted = !panelWanted;
        Update();
    }

    private void ToggleAutoOpen()
    {
        config.AutoOpen = !config.AutoOpen;
        config.Save(capi);
    }

    // Only scroll when the mouse is over the panel, so the wheel still works elsewhere.
    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (SingleComposer?.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY) != true) return;
        base.OnMouseWheel(args);
    }

    private static string Signature(InventorySmelting inv)
    {
        var sb = new StringBuilder();
        sb.Append(Lang.CurrentLocale).Append('|');
        sb.Append(inv[1].Itemstack?.Collectible.Code).Append('|');
        foreach (var slot in inv.CookingSlots)
        {
            var s = slot.Itemstack;
            sb.Append(s == null ? "-" : s.Collectible.Code + "x" + s.StackSize).Append(';');
        }
        return sb.ToString();
    }

    private void Compose(InventorySmelting inv)
    {
        icons ??= new IngredientIcons(capi.World);

        var pot = (BlockCookingContainer)inv[1].Itemstack!.Collectible;
        var stacks = inv.CookingSlots.Select(s => s.Itemstack).Where(s => s != null).Cast<ItemStack>().ToArray();
        bool dirty = pot.Attributes?["isDirtyPot"].AsBool() == true;
        int slotCount = inv.CookingSlots.Length;

        var recipes = capi.GetCookingRecipes() ?? new List<CookingRecipe>();
        var predictions = RecipePredictor.Predict(recipes, stacks, slotCount, pot.MaxServingSize, dirty);

        var rows = predictions
            .Select(p => BuildRow(p, stacks, slotCount, pot.MaxServingSize))
            .ToList();

        double rowsWidth = GuiElementRecipeRows.WidthFor(slotCount);
        double totalHeight = GuiElementRecipeRows.HeightFor(rows.Count);
        double visibleHeight = GuiElementRecipeRows.HeightFor(Math.Min(rows.Count, MaxVisibleRows));
        bool scroll = rows.Count > MaxVisibleRows;
        double width = rowsWidth + (scroll ? ScrollbarGap + ScrollbarWidth : 0);

        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        var clipBounds = ElementBounds.Fixed(0, 30, rowsWidth, visibleHeight);
        rowsBounds = ElementBounds.Fixed(0, 0, rowsWidth, totalHeight);
        var scrollBounds = ElementBounds.Fixed(rowsWidth + ScrollbarGap, 30, ScrollbarWidth, visibleHeight);
        bgBounds.WithChildren(clipBounds, scrollBounds);

        var dialogBounds = ElementBounds.Fixed(EnumDialogArea.None, 0, 0, width, 100);
        dialogBounds.BothSizing = ElementSizing.FitToChildren;

        SingleComposer?.Dispose();
        var composer = capi.Gui.CreateCompo("cookui-preview", dialogBounds)
            .AddShadedDialogBG(bgBounds, true)
            .AddDialogTitleBarWithBg(Lang.Get("cookui:panel-title"), OnCloseClicked, CairoFont.WhiteSmallText(), null, "titlebar")
            .BeginChildElements(bgBounds);

        if (rows.Count > 0)
        {
            composer
                .BeginClip(clipBounds)
                    .AddInteractiveElement(new GuiElementRecipeRows(capi, rowsBounds, clipBounds, rows, slotCount), "rows")
                .EndClip();
            if (scroll) composer.AddVerticalScrollbar(OnScroll, scrollBounds, "scrollbar");
        }
        else
        {
            var noteBounds = ElementBounds.Fixed(0, 30, rowsWidth, 20);
            bgBounds.WithChildren(noteBounds);
            string note = Lang.Get(stacks.Length == 0 ? "cookui:no-recipes" : "cookui:no-recipe-fits");
            composer.AddStaticText(note, CairoFont.WhiteDetailText().WithColor(new[] { 0.7, 0.7, 0.7, 1.0 }), noteBounds);
        }

        SingleComposer = composer.EndChildElements().Compose();
        if (scroll)
        {
            SingleComposer.GetScrollbar("scrollbar").SetHeights((float)visibleHeight, (float)totalHeight);
        }
        Reposition();
    }

    private void OnScroll(float value)
    {
        if (rowsBounds == null) return;
        rowsBounds.fixedY = -value;
        rowsBounds.CalcWorldBounds();
    }

    private void OnCloseClicked()
    {
        panelWanted = false;
        lastSignature = "";
        TryClose();
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
            int j = RecipePredictor.AssignedIngredient(ingredients, counts, stack);
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
                var range = icons!.SatietyRange(ingredients[j], entity);
                if (range != null) nutrition.AddNeeded(range.Value);
                var view = Ghost(SlotKind.Required, ingredients[j], servings, range);
                if (view.Slot?.Itemstack != null) mealContents.Add(view.Slot.Itemstack);
                slots.Add(view);
            }
        }

        // 3. Optional room, round-robin over groups so different kinds show up first.
        var room = ingredients.Select((ing, j) => ing.MaxQuantity - Math.Max(ing.MinQuantity, counts[j])).ToArray();
        bool added = true;
        while (slots.Count < slotCount && added)
        {
            added = false;
            for (int j = 0; j < ingredients.Length && slots.Count < slotCount; j++)
            {
                if (room[j] <= 0) continue;
                room[j]--;
                var range = icons!.SatietyRange(ingredients[j], entity);
                if (range != null) nutrition.AddOptional(range.Value.max);
                slots.Add(Ghost(SlotKind.Optional, ingredients[j], servings, range));
                added = true;
            }
        }

        while (slots.Count < slotCount) slots.Add(new SlotView { Kind = SlotKind.Empty });

        return new RowView
        {
            Meal = MealSlot(p, stacks, mealContents, nutrition),
            State = p.State,
            Ingredients = slots,
        };
    }

    private SlotView Ghost(SlotKind kind, CookingRecipeIngredient ing, int? servings, (float min, float max)? satiety)
    {
        var info = icons!.Get(ing);
        string key = kind == SlotKind.Required ? "cookui:slot-needed" : "cookui:slot-optional";
        string? count = null;
        if (servings != null)
        {
            count = ing.PortionSizeLitres > 0 ? Litres(servings.Value * ing.PortionSizeLitres) : servings.Value.ToString();
        }
        string tooltip = Lang.Get(key, info.Detail);
        if (satiety != null) tooltip += "\n" + Lang.Get("cookui:sat-each", Range(satiety.Value.min, satiety.Value.max));
        return new SlotView
        {
            Kind = kind,
            Slot = info.Icon == null ? null : Dummy(info.Icon.Clone()),
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

        string status = p.State switch
        {
            PredictionState.Ready => Lang.Get("cookui:servings", p.Servings),
            PredictionState.TooMuch => Lang.Get("cookui:too-much", p.Servings),
            PredictionState.RatioWrong => Lang.Get("cookui:ratio-wrong"),
            _ => Lang.Get("cookui:needs-more", p.MissingTotal),
        };

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

    private static string Round(float v) => Math.Round(v).ToString(CultureInfo.CurrentCulture);

    private static string Range(float min, float max) =>
        Math.Round(min) == Math.Round(max) ? Round(min) : Round(min) + "–" + Round(max);

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

    private static ItemSlot Dummy(ItemStack stack) => new DummySlot(stack);

    private static bool IsLiquid(ItemStack stack) => BlockLiquidContainerBase.GetContainableProps(stack) != null;

    private static string? AmountLabel(ItemStack stack)
    {
        var props = BlockLiquidContainerBase.GetContainableProps(stack);
        if (props != null && props.ItemsPerLitre > 0) return Litres(stack.StackSize / props.ItemsPerLitre);
        return stack.StackSize.ToString();
    }

    private static string Litres(float l) => l.ToString("0.##", CultureInfo.CurrentCulture) + "L";

    /// <summary>
    /// Keep the panel glued to the firepit dialog, which can float with the block, unless the
    /// player switched it to "Movable" in the title bar menu (the game then saves its position).
    /// </summary>
    private void Reposition()
    {
        var host = target?.SingleComposer?.Bounds;
        var own = SingleComposer?.Bounds;
        if (host == null || own == null) return;
        if (SingleComposer!.GetTitleBar("titlebar")?.Movable == true) return;

        double x = host.absX + host.OuterWidth + GapPx;
        if (x + own.OuterWidth > capi.Render.FrameWidth)
        {
            x = host.absX - own.OuterWidth - GapPx; // no room on the right: go left
        }
        double y = host.absY;

        if (Math.Abs(own.absX - x) < 0.5 && Math.Abs(own.absY - y) < 0.5) return;

        own.Alignment = EnumDialogArea.None;
        own.fixedOffsetX = 0;
        own.fixedOffsetY = 0;
        own.absMarginX = 0;
        own.absMarginY = 0;
        own.absFixedX = 0;
        own.absFixedY = 0;
        // Use fixedX/Y (unscaled) like the title bar's drag code does, so switching to Movable starts here.
        own.fixedX = x / RuntimeEnv.GUIScale;
        own.fixedY = y / RuntimeEnv.GUIScale;
        own.CalcWorldBounds();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (target == null || !target.IsOpened()) return;
        Reposition();
        base.OnRenderGUI(deltaTime);
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
