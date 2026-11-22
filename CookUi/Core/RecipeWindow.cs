using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CookUi;

/// <summary>
/// Non-interactive panel that sits beside an open station dialog and lists the rows its recipe
/// sources return for the station's contents. One per registered station. Also adds show and
/// auto-open buttons to the station's title bar. Knows nothing about any particular recipe type.
/// </summary>
public sealed class RecipeWindow : HudElement
{
    private const double GapPx = 6;
    private const int MaxVisibleRows = 6;
    private const double ScrollbarWidth = 20;
    private const double ScrollbarGap = 6;

    private readonly ICookingStation station;
    private readonly IReadOnlyList<IRecipeSource> sources;
    private readonly CookUiConfig config;

    private GuiDialog? target;
    private string lastSignature = "";
    /// <summary>Host dialog the current show/hide choice belongs to; reset each time a station opens.</summary>
    private GuiDialog? sessionDlg;
    /// <summary>Whether the player wants the panel shown for this station (starts as the auto-open setting).</summary>
    private bool panelWanted;
    private ElementBounds? rowsBounds;

    /// <param name="sources">Live list: sources registered later are picked up on the next rebuild.</param>
    public RecipeWindow(ICoreClientAPI capi, CookUiConfig config, ICookingStation station, IReadOnlyList<IRecipeSource> sources)
        : base(capi)
    {
        this.config = config;
        this.station = station;
        this.sources = sources;
    }

    public override bool Focusable => false;
    public override double DrawOrder => 0.21; // just above block entity dialogs (0.2)
    // Mouse events stay on (default) so the title bar buttons work and clicks on the
    // panel don't fall through to the world.
    public override bool ShouldReceiveKeyboardEvents() => false;

    /// <summary>The open dialog of this station and its contents, if any.</summary>
    private (GuiDialog? dlg, StationContents? contents) FindStation()
    {
        foreach (var dlg in capi.Gui.OpenedGuis)
        {
            StationContents? contents;
            try { contents = station.GetContents(dlg); }
            catch (Exception) { contents = null; }
            if (contents != null) return (dlg, contents);
        }
        return (null, null);
    }

    /// <summary>Sources with something to say about <paramref name="contents"/>.</summary>
    private List<IRecipeSource> Applicable(StationContents? contents)
    {
        var list = new List<IRecipeSource>();
        if (contents == null) return list;
        foreach (var source in sources)
        {
            try { if (source.AppliesTo(contents)) list.Add(source); }
            catch (Exception e) { capi.Logger.Warning("[cookui] {0}.AppliesTo failed: {1}", source.GetType().Name, e.Message); }
        }
        return list;
    }

    private bool IsActive(GuiDialog dlg)
    {
        StationContents? contents;
        try { contents = station.GetContents(dlg); }
        catch (Exception) { return false; }
        return Applicable(contents).Count > 0;
    }

    /// <summary>Called periodically by the mod system; shows, refreshes or hides the panel.</summary>
    public void Update()
    {
        var (dlg, contents) = FindStation();
        if (dlg != sessionDlg)
        {
            sessionDlg = dlg;
            panelWanted = config.AutoOpen;
        }

        if (dlg != null)
        {
            TitleBarButtons.AttachTo(capi, dlg, "cookui-buttons-" + station.Id, () => new TitleBarButtons(
                capi, dlg, () => IsActive(dlg), IsOpened, () => config.AutoOpen, TogglePanel, ToggleAutoOpen));
        }

        target = dlg;
        var active = Applicable(contents);
        if (contents == null || active.Count == 0 || !panelWanted)
        {
            lastSignature = "";
            if (IsOpened()) TryClose();
            return;
        }

        string sig = Signature(contents, active);
        if (sig != lastSignature || SingleComposer == null)
        {
            lastSignature = sig;
            Compose(contents, active);
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
        config.AutoOpen = !config.AutoOpen;
        config.Save(capi);
    }

    // Only scroll when the mouse is over the panel, so the wheel still works elsewhere.
    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (SingleComposer?.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY) != true) return;
        base.OnMouseWheel(args);
    }

    /// <summary>Changes whenever the rows would; the panel is only rebuilt when it does.</summary>
    private static string Signature(StationContents contents, List<IRecipeSource> active)
    {
        var sb = new StringBuilder();
        sb.Append(Lang.CurrentLocale).Append('|');
        sb.Append(contents.Container?.Collectible.Code).Append('|');
        foreach (var slot in contents.Slots)
        {
            var s = slot.Itemstack;
            sb.Append(s == null ? "-" : s.Collectible.Code + "x" + s.StackSize).Append(';');
        }
        foreach (var source in active) sb.Append('|').Append(source.GetType().FullName);
        return sb.ToString();
    }

    private (List<RecipeRow> rows, string note) BuildRows(StationContents contents, List<IRecipeSource> active)
    {
        var rows = new List<RecipeRow>();
        string? note = null;
        foreach (var source in active)
        {
            try
            {
                rows.AddRange(source.GetRows(contents));
                note ??= source.EmptyNote(contents);
            }
            catch (Exception e)
            {
                capi.Logger.Error("[cookui] {0} failed to build rows: {1}", source.GetType().Name, e);
            }
        }
        // Several sources: keep each one's order, but list everything ready first.
        if (active.Count > 1) rows = rows.OrderBy(r => RecipeFormat.Rank(r.State)).ToList();
        note ??= Lang.Get(contents.Slots.All(s => s.Empty) ? "cookui:no-recipes" : "cookui:no-recipe-fits");
        return (rows, note);
    }

    private void Compose(StationContents contents, List<IRecipeSource> active)
    {
        var (rows, note) = BuildRows(contents, active);
        int slotCount = contents.Slots.Count;
        foreach (var row in rows)
        {
            while (row.Ingredients.Count < slotCount) row.Ingredients.Add(RecipeSlot.Empty());
        }

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
        var composer = capi.Gui.CreateCompo("cookui-" + station.Id, dialogBounds)
            .AddShadedDialogBG(bgBounds, true)
            .AddDialogTitleBarWithBg(station.Title, OnCloseClicked, CairoFont.WhiteSmallText(), null, "titlebar")
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
}
