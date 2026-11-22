using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CookUi;

/// <summary>
/// Two small buttons drawn on a station dialog's title bar, left of its menu icon:
/// a bowl that opens/closes the recipe panel, and an "A" that turns auto-open on or off.
/// They live in an extra composer added to the station dialog, so they move, render and
/// take clicks together with it (the dialog's own title bar doesn't consume clicks).
/// Several windows can attach to the same dialog (pot vs saucepan in a firepit) as long as
/// only one of them is visible at a time.
/// </summary>
public class TitleBarButtons : GuiElement
{
    private const double ButtonSize = 22;
    private const double ButtonGap = 4;
    // Dialog title bar: close (15) + menu (17) icons and ~20px padding from the right edge.
    private const double TitleBarIconsWidth = 15 + 17 + 20 + 8;

    private readonly GuiDialog host;
    private readonly Func<bool> isVisible;
    private readonly Func<bool> isPanelOpen;
    private readonly Func<bool> isAutoOpen;
    private readonly Action togglePanel;
    private readonly Action toggleAuto;
    private readonly ItemSlot? bowlSlot;
    private readonly TooltipRenderer tooltip;

    private LoadedTexture? bgOff, bgOn, autoOnTex, autoOffTex;
    private double panelX, autoX, btnY, btnSize;

    public TitleBarButtons(ICoreClientAPI capi, GuiDialog host, Func<bool> isVisible, Func<bool> isPanelOpen,
        Func<bool> isAutoOpen, Action togglePanel, Action toggleAuto)
        : base(capi, ElementBounds.Fixed(0, 0, 1, 1))
    {
        this.host = host;
        this.isVisible = isVisible;
        this.isPanelOpen = isPanelOpen;
        this.isAutoOpen = isAutoOpen;
        this.togglePanel = togglePanel;
        this.toggleAuto = toggleAuto;
        tooltip = new TooltipRenderer(capi);

        var bowls = BlockMeal.AllMealBowls(capi);
        if (bowls is { Length: > 0 }) bowlSlot = new DummySlot(new ItemStack(bowls[0]));
    }

    /// <summary>
    /// Adds the buttons to <paramref name="dialog"/> under <paramref name="composerKey"/> unless
    /// they are already there (dialogs that rebuild themselves drop them; they come back next call).
    /// </summary>
    public static void AttachTo(ICoreClientAPI capi, GuiDialog dialog, string composerKey, Func<TitleBarButtons> factory)
    {
        if (dialog.Composers.ContainsKey(composerKey)) return;
        var composer = capi.Gui.CreateCompo(composerKey, ElementBounds.Fixed(0, 0, 1, 1))
            .AddInteractiveElement(factory(), "buttons")
            .Compose();
        dialog.Composers[composerKey] = composer;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        int size = (int)scaled(ButtonSize);
        bgOff = ButtonTexture(size, false);
        bgOn = ButtonTexture(size, true);
        var font = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold);
        autoOnTex = api.Gui.TextTexture.GenTextTexture("A", font.Clone().WithColor(new[] { 0.55, 0.9, 0.55, 1.0 }));
        autoOffTex = api.Gui.TextTexture.GenTextTexture("A", font.Clone().WithColor(new[] { 0.55, 0.55, 0.55, 1.0 }));
    }

    private LoadedTexture ButtonTexture(int size, bool on)
    {
        using var surface = new ImageSurface(Format.Argb32, size, size);
        using (var ctx = genContext(surface))
        {
            ctx.SetSourceRGBA(GuiStyle.DialogSlotBackColor);
            RoundRectangle(ctx, 0, 0, size, size, 2);
            ctx.Fill();
            EmbossRoundRectangleElement(ctx, 0, 0, size, size, !on, 2, 2);
            if (on)
            {
                ctx.SetSourceRGBA(0.5, 0.82, 0.5, 0.9);
                ctx.LineWidth = scaled(1.5);
                RoundRectangle(ctx, scaled(1), scaled(1), size - scaled(2), size - scaled(2), 2);
                ctx.Stroke();
            }
        }
        var tex = new LoadedTexture(api);
        generateTexture(surface, ref tex);
        return tex;
    }

    private bool Layout()
    {
        var hb = host.SingleComposer?.Bounds;
        if (hb == null || !isVisible()) return false;
        btnSize = scaled(ButtonSize);
        double right = hb.absX + hb.OuterWidth - scaled(TitleBarIconsWidth);
        autoX = right - btnSize;
        panelX = autoX - scaled(ButtonGap) - btnSize;
        btnY = hb.absY + scaled(4);
        return true;
    }

    private bool Inside(double x, int mx, int my) =>
        mx >= x && mx < x + btnSize && my >= btnY && my < btnY + btnSize;

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (!Layout()) return;
        int mx = api.Input.MouseX, my = api.Input.MouseY;

        bool panelOpen = isPanelOpen();
        Blit(panelOpen ? bgOn : bgOff, panelX, btnY);
        if (bowlSlot != null)
        {
            api.Render.RenderItemstackToGui(bowlSlot, panelX + btnSize / 2, btnY + btnSize / 2, 120,
                (float)(btnSize * 0.62), -1, true, false, false);
        }

        bool auto = isAutoOpen();
        Blit(auto ? bgOn : bgOff, autoX, btnY);
        var a = auto ? autoOnTex : autoOffTex;
        if (a != null) Blit(a, autoX + (btnSize - a.Width) / 2, btnY + (btnSize - a.Height) / 2, 120);

        string? tip = null;
        if (Inside(panelX, mx, my)) tip = Lang.Get(panelOpen ? "cookui:btn-hide" : "cookui:btn-show");
        else if (Inside(autoX, mx, my)) tip = Lang.Get(auto ? "cookui:btn-auto-on" : "cookui:btn-auto-off");
        tooltip.Render(tip, mx, my);
    }

    private void Blit(LoadedTexture? tex, double x, double y, float z = 110)
    {
        if (tex == null) return;
        api.Render.Render2DTexturePremultipliedAlpha(tex.TextureId, x, y, tex.Width, tex.Height, z);
    }

    public override bool IsPositionInside(int posX, int posY) =>
        Layout() && (Inside(panelX, posX, posY) || Inside(autoX, posX, posY));

    public override void OnMouseDown(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Handled || !Layout()) return;
        if (Inside(panelX, args.X, args.Y))
        {
            args.Handled = true;
            togglePanel();
            api.Gui.PlaySound("toggleswitch");
        }
        else if (Inside(autoX, args.X, args.Y))
        {
            args.Handled = true;
            toggleAuto();
            api.Gui.PlaySound("toggleswitch");
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        tooltip.Dispose();
        bgOff?.Dispose();
        bgOn?.Dispose();
        autoOnTex?.Dispose();
        autoOffTex?.Dispose();
    }
}
