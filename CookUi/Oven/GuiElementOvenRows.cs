using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CookUi.Oven;

/// <summary>
/// Draws the oven panel: title, oven temperature (with a flame showing the fuel left while it
/// burns) and one row per oven slot, "[item] → [result]" with the arrow filling as the stage
/// bakes, like the firepit's. The firewood shares the first slot, so its row shows a flame instead.
/// Slot backgrounds and the title are static; everything else is redrawn each frame from
/// <see cref="OvenView"/>, which the HUD refreshes every tick.
/// </summary>
internal sealed class GuiElementOvenRows : GuiElement
{
    private const double SlotSize = 40;
    private const double ArrowWidth = 44;
    private const double Gap = 8;
    private const double TextGap = 10;
    private const double TextWidth = 190;
    private const double RowGap = 6;
    private const double HeaderHeight = 74;
    private const double HeaderIcon = 22;
    /// <summary>Arrow and flame fills are cached in this many steps.</summary>
    private const int FillSteps = 40;

    private static readonly double[] White = { 1, 1, 1, 1 };
    private static readonly double[] Grey = { 0.7, 0.7, 0.7, 1 };
    private static readonly double[] Green = { 0.55, 0.85, 0.5, 1 };
    private static readonly double[] Orange = { 0.95, 0.65, 0.3, 1 };
    private static readonly double[] Red = { 0.95, 0.45, 0.4, 1 };

    private enum Icon { Arrow, BurnArrow, Flame, HeaderFlame }

    private readonly OvenView view;
    private readonly Dictionary<(Icon, int), LoadedTexture> icons = new();
    private readonly Dictionary<string, LoadedTexture> texts = new();
    private readonly DummySlot inputSlot = new(null), resultSlot = new(null);

    public GuiElementOvenRows(ICoreClientAPI capi, ElementBounds bounds, OvenView view) : base(capi, bounds)
    {
        this.view = view;
    }

    public static double Width => SlotSize + Gap + ArrowWidth + Gap + SlotSize + TextGap + TextWidth;
    public static double HeightFor(int rows) => HeaderHeight + rows * (SlotSize + RowGap) - RowGap;

    private static double RowY(int row) => HeaderHeight + row * (SlotSize + RowGap);
    private const double ArrowX = SlotSize + Gap;
    private const double ResultX = ArrowX + ArrowWidth + Gap;
    private const double TextX = ResultX + SlotSize + TextGap;

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        var font = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold);
        font.SetupContext(ctx);
        api.Gui.Text.DrawTextLine(ctx, font, view.Title, Bounds.drawX, Bounds.drawY);

        double size = scaled(SlotSize);
        for (int r = 0; r < view.Rows.Length; r++)
        {
            double y = Bounds.drawY + scaled(RowY(r));
            SlotBackground(ctx, Bounds.drawX, y, size);
            SlotBackground(ctx, Bounds.drawX + scaled(ResultX), y, size);
        }
    }

    private void SlotBackground(Context ctx, double x, double y, double size)
    {
        ctx.SetSourceRGBA(GuiStyle.DialogSlotBackColor);
        RoundRectangle(ctx, x, y, size, size, GuiStyle.ElementBGRadius);
        ctx.Fill();
        EmbossRoundRectangleElement(ctx, x, y, size, size, true, 2, 2);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        double x0 = Bounds.renderX, y0 = Bounds.renderY;

        // Header: oven temperature, with a flame for the fuel left while it burns.
        double textX = x0;
        if (view.Burning)
        {
            var flame = IconTexture(Icon.HeaderFlame, FuelLeft());
            Blit(flame, x0, y0 + scaled(26));
            textX += flame.Width + scaled(4);
        }
        string temp = view.OvenTemp <= 25
            ? Lang.Get("Temperature: {0}", Lang.Get("Cold"))
            : Lang.Get("Temperature: {0}°C", (int)view.OvenTemp);
        Blit(Text(temp, view.Burning ? Orange : White, 14, Width - HeaderIcon - 4), textX, y0 + scaled(28));
        if (view.Hint != null) Blit(Text(view.Hint, Grey, 13, Width), x0, y0 + scaled(50));

        double size = scaled(SlotSize);
        for (int r = 0; r < view.Rows.Length; r++)
        {
            var row = view.Rows[r];
            double y = y0 + scaled(RowY(r));
            if (row.Input == null) continue;

            RenderStack(inputSlot, row.Input, x0, y, size);
            double ax = x0 + scaled(ArrowX), tx = x0 + scaled(TextX);

            if (row.IsFuel)
            {
                Blit(IconTexture(Icon.Flame, row.FuelLeft), ax, y);
                Lines(tx, y, row.Input.GetName(), view.Burning
                    ? (Lang.Get("cookui:oven-fuel-burning", (int)Math.Ceiling(row.FuelSeconds)), Orange)
                    : (Lang.Get("cookui:oven-fuel-unlit"), Grey));
                continue;
            }

            int t = (int)row.Temp;
            (string, double[]) status;
            if (row.Result == null) status = (Lang.Get("cookui:oven-item-temp", t), Grey);
            else if (row.Temp < row.BakeTemp) status = (Lang.Get("cookui:oven-heating", t, (int)row.BakeTemp), Orange);
            else if (row.Burns) status = (Lang.Get("cookui:oven-burning", Percent(row.Progress), t), Red);
            else status = (Lang.Get("cookui:oven-baking", Percent(row.Progress), t), Green);

            if (row.Result != null)
            {
                Blit(IconTexture(row.Burns ? Icon.BurnArrow : Icon.Arrow, row.Progress), ax, y);
                RenderStack(resultSlot, row.Result, x0 + scaled(ResultX), y, size);
            }
            Lines(tx, y, row.Input.GetName(), status);
        }
    }

    private float FuelLeft()
    {
        foreach (var row in view.Rows) if (row.IsFuel) return row.FuelLeft;
        return 0;
    }

    private static int Percent(float progress) => (int)(progress * 100);

    private void Lines(double x, double y, string name, (string text, double[] color) status)
    {
        Blit(Text(name, White, 14), x, y + scaled(2));
        Blit(Text(status.text, status.color, 13), x, y + scaled(21));
    }

    private void RenderStack(DummySlot slot, ItemStack stack, double x, double y, double size)
    {
        slot.Itemstack = stack;
        api.Render.RenderItemstackToGui(slot, x + size / 2, y + size / 2, 100, (float)(size * 0.58), -1, true, false, true);
    }

    private void Blit(LoadedTexture tex, double x, double y) =>
        api.Render.Render2DTexturePremultipliedAlpha(tex.TextureId, x, y, tex.Width, tex.Height, 50);

    private LoadedTexture Text(string text, double[] color, double fontSize, double maxWidth = TextWidth)
    {
        string key = fontSize + "|" + maxWidth + "|" + string.Join(",", color) + "|" + text;
        if (texts.TryGetValue(key, out var tex)) return tex;
        // Temperatures change constantly: don't let old strings pile up.
        if (texts.Count > 200)
        {
            foreach (var t in texts.Values) t.Dispose();
            texts.Clear();
        }
        var font = CairoFont.WhiteDetailText().WithFontSize((float)fontSize).WithColor(color);
        tex = api.Gui.TextTexture.GenTextTexture(Ellipsize(font, text, scaled(maxWidth)), font);
        texts[key] = tex;
        return tex;
    }

    /// <summary>Cuts text that wouldn't fit, so a long item name can't run off the panel.</summary>
    private static string Ellipsize(CairoFont font, string text, double maxWidth)
    {
        if (font.GetTextExtents(text).XAdvance <= maxWidth) return text;
        for (int len = text.Length - 1; len > 0; len--)
        {
            string candidate = text[..len].TrimEnd() + "…";
            if (font.GetTextExtents(candidate).XAdvance <= maxWidth) return candidate;
        }
        return text;
    }

    /// <summary>An arrow or flame outline, filled up to <paramref name="fill"/> (0 to 1).</summary>
    private LoadedTexture IconTexture(Icon icon, float fill)
    {
        int step = (int)Math.Round(Math.Clamp(fill, 0, 1) * FillSteps);
        if (icons.TryGetValue((icon, step), out var cached)) return cached;
        double level = step / (double)FillSteps;

        bool header = icon == Icon.HeaderFlame;
        int w = (int)Math.Ceiling(scaled(header ? HeaderIcon : ArrowWidth));
        int h = (int)Math.Ceiling(scaled(header ? HeaderIcon : SlotSize));
        using var surface = new ImageSurface(Format.Argb32, w, h);
        using (var ctx = genContext(surface))
        {
            if (icon is Icon.Arrow or Icon.BurnArrow) DrawArrow(ctx, w, h, level, icon == Icon.BurnArrow);
            else DrawFlame(ctx, w, h, level);
        }
        var tex = new LoadedTexture(api);
        generateTexture(surface, ref tex);
        icons[(icon, step)] = tex;
        return tex;
    }

    // The arrow path (IconUtil.DrawArrowRight) spans about x 8-127, y 8-63.
    private void DrawArrow(Context ctx, int w, int h, double level, bool burns)
    {
        const double pathX = 8, pathY = 8, pathW = 119, pathH = 55;
        double s = Math.Min((w - 4) / pathW, (h - 4) / pathH);
        ctx.Translate((w - pathW * s) / 2 - pathX * s, (h - pathH * s) / 2 - pathY * s);
        ctx.Scale(s, s);

        api.Gui.Icons.DrawArrowRight(ctx, 2);
        if (level <= 0) return;
        ctx.Rectangle(pathX - 2, 0, (pathW + 4) * level, 100);
        ctx.Clip();
        using var gradient = new LinearGradient(pathX, 0, pathX + pathW, 0);
        if (burns)
        {
            gradient.AddColorStop(0, new Color(0.45, 0.1, 0.05, 1));
            gradient.AddColorStop(1, new Color(0.8, 0.25, 0.15, 1));
        }
        else
        {
            // Same colours as the firepit's arrow.
            gradient.AddColorStop(0, new Color(0, 0.4, 0, 1));
            gradient.AddColorStop(1, new Color(0.2, 0.6, 0.2, 1));
        }
        ctx.SetSource(gradient);
        api.Gui.Icons.DrawArrowRight(ctx, 0, false, false);
    }

    // The flame path (IconUtil.DrawFlame) spans about x 0-140, y 0-210; it empties from the top like the firepit's.
    private void DrawFlame(Context ctx, int w, int h, double level)
    {
        const double pathW = 140, pathH = 210;
        double s = Math.Min((w - 2) / pathW, (h - 2) / pathH);
        ctx.Translate((w - pathW * s) / 2, (h - pathH * s) / 2);
        ctx.Scale(s, s);

        api.Gui.Icons.DrawFlame(ctx);
        if (level <= 0) return;
        double top = pathH - pathH * level;
        ctx.Rectangle(0, top, pathW + 20, pathH - top + 10);
        ctx.Clip();
        using var gradient = new LinearGradient(0, pathH, 0, 0);
        gradient.AddColorStop(0, new Color(1, 1, 0, 1));
        gradient.AddColorStop(1, new Color(1, 0, 0, 1));
        ctx.SetSource(gradient);
        api.Gui.Icons.DrawFlame(ctx, 0, false, false);
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var t in icons.Values) t.Dispose();
        icons.Clear();
        foreach (var t in texts.Values) t.Dispose();
        texts.Clear();
    }
}
