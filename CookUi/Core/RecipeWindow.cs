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

/// <summary>Non-generic handle so the mod system can keep every kind of window in one list.</summary>
public abstract class RecipeWindow : HudElement
{
    protected RecipeWindow(ICoreClientAPI capi) : base(capi) { }

    /// <summary>Called periodically by the mod system; shows, refreshes or hides the panel.</summary>
    public abstract void Update();
}

/// <summary>
/// Non-interactive panel that sits beside an open station dialog (firepit, mixing bowl...)
/// and lists the recipes its contents can still become, one row per recipe: the result's icon,
/// then the station's slots with the ingredients already in it and "shadows" of the ones
/// still needed or allowed. Also adds show/auto-open buttons to the station's title bar.
///
/// To support a new station, derive from a recipe-family window (e.g.
/// <see cref="CookingRecipeWindow{THost}"/>) and say which dialog it attaches to
/// (<see cref="IsHost"/>) and what the station holds (<see cref="GetState"/>).
/// To support a new kind of recipe, derive from this class and implement <see cref="BuildGrid"/>.
/// </summary>
/// <typeparam name="THost">Dialog class the window attaches to.</typeparam>
/// <typeparam name="TState">Station contents the window needs to build its rows.</typeparam>
public abstract class RecipeWindow<THost, TState> : RecipeWindow
    where THost : GuiDialog
    where TState : StationState
{
    private const double GapPx = 6;
    private const int MaxVisibleRows = 6;
    private const double ScrollbarWidth = 20;
    private const double ScrollbarGap = 6;

    protected readonly CookUiConfig Config;

    private THost? target;
    private string lastSignature = "";
    /// <summary>Host dialog the current show/hide choice belongs to; reset each time a station opens.</summary>
    private THost? sessionDlg;
    /// <summary>Whether the player wants the panel shown for this station (starts as the auto-open setting).</summary>
    private bool panelWanted;
    private ElementBounds? rowsBounds;

    protected RecipeWindow(ICoreClientAPI capi, CookUiConfig config) : base(capi)
    {
        Config = config;
    }

    /// <summary>Unique short name; keys this window's composers (panel and title bar buttons).</summary>
    public abstract string Id { get; }

    /// <summary>Lang key of the panel title ("Pot recipes"...).</summary>
    protected abstract string TitleLangKey { get; }

    /// <summary>Whether <paramref name="dlg"/>, an open <typeparamref name="THost"/>, is this window's station.</summary>
    protected virtual bool IsHost(THost dlg) => true;

    /// <summary>The station's contents, or null when it can't use these recipes right now (no pot...).</summary>
    protected abstract TState? GetState(THost host);

    /// <summary>The rows to show for <paramref name="state"/>.</summary>
    protected abstract RecipeGrid BuildGrid(TState state);

    public override bool Focusable => false;
    public override double DrawOrder => 0.21; // just above block entity dialogs (0.2)
    // Mouse events stay on (default) so the title bar buttons work and clicks on the
    // panel don't fall through to the world.
    public override bool ShouldReceiveKeyboardEvents() => false;

    public override void Update()
    {
        var dlg = capi.Gui.OpenedGuis.OfType<THost>().FirstOrDefault(IsHost);
        if (dlg != sessionDlg)
        {
            sessionDlg = dlg;
            panelWanted = Config.AutoOpen;
        }

        if (dlg != null)
        {
            TitleBarButtons.AttachTo(capi, dlg, "cookui-buttons-" + Id, () => new TitleBarButtons(
                capi, dlg, () => GetState(dlg) != null, IsOpened, () => Config.AutoOpen, TogglePanel, ToggleAutoOpen));
        }

        var state = dlg == null ? null : GetState(dlg);
        target = dlg;
        if (state == null || !panelWanted)
        {
            lastSignature = "";
            if (IsOpened()) TryClose();
            return;
        }

        string sig = Signature(state);
        if (sig != lastSignature || SingleComposer == null)
        {
            lastSignature = sig;
            Compose(state);
        }

        if (!IsOpened()) TryOpen();
    }

    private void TogglePanel()
    {
        panelWanted = !panelWanted;
        Update();
    }

    private void ToggleAutoOpen()
    {
        Config.AutoOpen = !Config.AutoOpen;
        Config.Save(capi);
    }

    // Only scroll when the mouse is over the panel, so the wheel still works elsewhere.
    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (SingleComposer?.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY) != true) return;
        base.OnMouseWheel(args);
    }

    /// <summary>Changes whenever the rows would; the panel is only rebuilt when it does.</summary>
    protected virtual string Signature(TState state)
    {
        var sb = new StringBuilder();
        sb.Append(Lang.CurrentLocale).Append('|');
        sb.Append(state.Container?.Collectible.Code).Append('|');
        foreach (var slot in state.Slots)
        {
            var s = slot.Itemstack;
            sb.Append(s == null ? "-" : s.Collectible.Code + "x" + s.StackSize).Append(';');
        }
        return sb.ToString();
    }

    private void Compose(TState state)
    {
        var grid = BuildGrid(state);
        var rows = grid.Rows;
        int slotCount = grid.SlotCount;

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
        var composer = capi.Gui.CreateCompo("cookui-" + Id, dialogBounds)
            .AddShadedDialogBG(bgBounds, true)
            .AddDialogTitleBarWithBg(Lang.Get(TitleLangKey), OnCloseClicked, CairoFont.WhiteSmallText(), null, "titlebar")
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
            composer.AddStaticText(grid.EmptyNote, CairoFont.WhiteDetailText().WithColor(new[] { 0.7, 0.7, 0.7, 1.0 }), noteBounds);
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

    /// <summary>
    /// Keep the panel glued to the host dialog, which can float with the block, unless the
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
        // After base: the EndClip element has popped the scissor, so the tooltip isn't cut off.
        (SingleComposer?.GetElement("rows") as GuiElementRecipeRows)?.RenderTooltip();
    }

    // ---- Helpers shared by row builders ----

    protected static ItemSlot Dummy(ItemStack stack) => new DummySlot(stack);

    protected static bool IsLiquid(ItemStack stack) => BlockLiquidContainerBase.GetContainableProps(stack) != null;

    /// <summary>"3" for items, "1.5L" for liquids.</summary>
    protected static string? AmountLabel(ItemStack stack)
    {
        var props = BlockLiquidContainerBase.GetContainableProps(stack);
        if (props != null && props.ItemsPerLitre > 0) return Litres(stack.StackSize / props.ItemsPerLitre);
        return stack.StackSize.ToString();
    }

    protected static string Litres(float l) => l.ToString("0.##", CultureInfo.CurrentCulture) + "L";

    protected static string Round(float v) => Math.Round(v).ToString(CultureInfo.CurrentCulture);

    protected static string Range(float min, float max) =>
        Math.Round(min) == Math.Round(max) ? Round(min) : Round(min) + "–" + Round(max);

    /// <summary>Non-empty stacks of <paramref name="slots"/>, in order.</summary>
    protected static ItemStack[] Stacks(IEnumerable<ItemSlot> slots) =>
        slots.Select(s => s.Itemstack).Where(s => s != null).Cast<ItemStack>().ToArray();

    /// <summary>Status line of a recipe row's result tooltip.</summary>
    protected static string StatusText(PredictionState state, int servings, int missing) => state switch
    {
        PredictionState.Ready => Lang.Get("cookui:servings", servings),
        PredictionState.TooMuch => Lang.Get("cookui:too-much", servings),
        PredictionState.RatioWrong => Lang.Get("cookui:ratio-wrong"),
        _ => Lang.Get("cookui:needs-more", missing),
    };
}
