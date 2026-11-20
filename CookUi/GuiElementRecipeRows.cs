using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace CookUi;

public enum SlotKind { Empty, Present, Required, Optional }

/// <summary>One square in a recipe row: an item icon, a small amount label and a hover text.</summary>
public sealed class SlotView
{
    public SlotKind Kind;
    public ItemSlot? Slot;
    public string? Tooltip;
    public string? Count;
    /// <summary>Amount is wrong (uneven stack size); count is drawn in orange.</summary>
    public bool Warn;
}

public sealed class RowView
{
    public required SlotView Meal;
    public required PredictionState State;
    public required List<SlotView> Ingredients;
}

/// <summary>
/// Draws rows of "[meal] | [ing] [ing] [ing] [ing]", mirroring the pot's slots.
/// Ingredients still needed show as dark "shadow" icons, optional ones fainter still.
/// Hovering any icon shows its (localized) name. Reads the mouse position directly,
/// so the panel never needs to take mouse events away from the firepit dialog.
/// </summary>
public class GuiElementRecipeRows : GuiElement
{
    public const double SlotSize = 40;
    public const double SlotGap = 4;
    public const double SeparatorGap = 14;
    public const double RowGap = 6;

    private static readonly int RequiredColor = ColorUtil.ToRgba(170, 95, 95, 95);
    private static readonly int OptionalColor = ColorUtil.ToRgba(90, 70, 70, 70);

    private readonly List<RowView> rows;
    private readonly int slotCount;
    private readonly ElementBounds clipBounds;
    private readonly Dictionary<string, LoadedTexture> textCache = new();
    private readonly TooltipRenderer tooltip;
    private LoadedTexture? slotTex, readyTex, warnTex, sepTex;
    private string? hover;

    /// <param name="clipBounds">Visible (scrolled) area; slots outside it don't react to the mouse.</param>
    public GuiElementRecipeRows(ICoreClientAPI capi, ElementBounds bounds, ElementBounds clipBounds, List<RowView> rows, int slotCount)
        : base(capi, bounds)
    {
        this.rows = rows;
        this.slotCount = slotCount;
        this.clipBounds = clipBounds;
        tooltip = new TooltipRenderer(capi);
    }

    public static double WidthFor(int slotCount) => SlotSize + SeparatorGap + slotCount * (SlotSize + SlotGap) - SlotGap;
    public static double HeightFor(int rowCount) => Math.Max(0, rowCount * (SlotSize + RowGap) - RowGap);

    // Unscaled offsets of a slot within the element. index -1 is the meal slot.
    private static double SlotX(int index) => index < 0 ? 0 : SlotSize + SeparatorGap + index * (SlotSize + SlotGap);
    private static double RowY(int row) => row * (SlotSize + RowGap);

    // Backgrounds are textures drawn every frame (not the composer's static layer) so they scroll with the rows.
    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        int size = (int)scaled(SlotSize);
        slotTex = SlotTexture(size, null);
        readyTex = SlotTexture(size, new[] { 0.5, 0.82, 0.5, 0.9 });
        warnTex = SlotTexture(size, new[] { 0.88, 0.63, 0.31, 0.9 });

        int w = Math.Max(2, (int)scaled(1.5));
        using var sepSurface = new ImageSurface(Format.Argb32, w, size);
        using (var sctx = genContext(sepSurface))
        {
            sctx.SetSourceRGBA(1, 1, 1, 0.25);
            sctx.Rectangle(0, scaled(6), w, size - scaled(12));
            sctx.Fill();
        }
        sepTex = new LoadedTexture(api);
        generateTexture(sepSurface, ref sepTex);
    }

    private LoadedTexture SlotTexture(int size, double[]? border)
    {
        using var surface = new ImageSurface(Format.Argb32, size, size);
        using (var ctx = genContext(surface))
        {
            ctx.SetSourceRGBA(GuiStyle.DialogSlotBackColor);
            RoundRectangle(ctx, 0, 0, size, size, GuiStyle.ElementBGRadius);
            ctx.Fill();
            EmbossRoundRectangleElement(ctx, 0, 0, size, size, true, 2, 2);
            if (border != null)
            {
                ctx.SetSourceRGBA(border);
                ctx.LineWidth = scaled(2);
                RoundRectangle(ctx, scaled(1), scaled(1), size - scaled(2), size - scaled(2), GuiStyle.ElementBGRadius);
                ctx.Stroke();
            }
        }
        var tex = new LoadedTexture(api);
        generateTexture(surface, ref tex);
        return tex;
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        double size = scaled(SlotSize);
        int mx = api.Input.MouseX, my = api.Input.MouseY;
        bool mouseInView = clipBounds.PointInside(mx, my);
        hover = null;

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            double y = Bounds.renderY + scaled(RowY(r));
            // Skip rows scrolled fully out of view.
            if (y + size < clipBounds.renderY || y > clipBounds.renderY + clipBounds.InnerHeight) continue;

            var bg = row.State switch
            {
                PredictionState.Ready => readyTex,
                PredictionState.TooMuch or PredictionState.RatioWrong => warnTex,
                _ => slotTex,
            };
            double mealX = Bounds.renderX + scaled(SlotX(-1));
            Blit(bg, mealX, y);
            if (sepTex != null) Blit(sepTex, Bounds.renderX + scaled(SlotSize + SeparatorGap / 2) - sepTex.Width / 2.0, y);

            // Always render every slot; only remember the first tooltip under the mouse.
            // (Don't use `hover ??= RenderSlot(...)`: it skips rendering once hover is set.)
            var t = RenderSlot(row.Meal, mealX, y, size, mx, my);
            if (mouseInView) hover ??= t;
            for (int i = 0; i < slotCount; i++)
            {
                double x = Bounds.renderX + scaled(SlotX(i));
                Blit(slotTex, x, y);
                if (i >= row.Ingredients.Count) continue;
                t = RenderSlot(row.Ingredients[i], x, y, size, mx, my);
                if (mouseInView) hover ??= t;
            }
        }
    }

    // Drawn after the scroll clip is released, so the popup isn't cut off.
    public override void PostRenderInteractiveElements(float deltaTime)
    {
        tooltip.Render(hover, api.Input.MouseX, api.Input.MouseY);
    }

    private void Blit(LoadedTexture? tex, double x, double y)
    {
        if (tex == null) return;
        api.Render.Render2DTexturePremultipliedAlpha(tex.TextureId, x, y, tex.Width, tex.Height, 50);
    }

    /// <returns>The slot's tooltip when the mouse is over it.</returns>
    private string? RenderSlot(SlotView view, double x, double y, double size, int mx, int my)
    {
        if (view.Slot?.Itemstack != null)
        {
            int color = view.Kind switch
            {
                SlotKind.Required => RequiredColor,
                SlotKind.Optional => OptionalColor,
                _ => -1,
            };
            api.Render.RenderItemstackToGui(view.Slot, x + size / 2, y + size / 2, 100,
                (float)(size * 0.58), color, true, false, false);
        }

        if (view.Count != null)
        {
            double[] c = view.Warn ? new[] { 0.95, 0.65, 0.3, 1.0 }
                : view.Kind == SlotKind.Required ? new[] { 0.95, 0.55, 0.55, 1.0 }
                : view.Kind == SlotKind.Optional ? new[] { 0.7, 0.7, 0.7, 1.0 }
                : new[] { 1.0, 1.0, 1.0, 1.0 };
            var tex = Text(view.Count, c);
            api.Render.Render2DTexturePremultipliedAlpha(tex.TextureId,
                x + size - tex.Width - scaled(3), y + size - tex.Height - scaled(1), tex.Width, tex.Height, 200);
        }

        bool inside = mx >= x && mx < x + size && my >= y && my < y + size;
        return inside ? view.Tooltip : null;
    }

    private LoadedTexture Text(string text, double[] color)
    {
        string key = text + "|" + string.Join(",", color);
        if (textCache.TryGetValue(key, out var tex)) return tex;
        var font = CairoFont.WhiteDetailText().WithFontSize(13).WithColor(color).WithWeight(FontWeight.Bold);
        font.StrokeColor = new double[] { 0, 0, 0, 1 };
        font.StrokeWidth = RuntimeEnv.GUIScale * 1.5;
        tex = api.Gui.TextTexture.GenTextTexture(text, font);
        textCache[key] = tex;
        return tex;
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var t in textCache.Values) t.Dispose();
        textCache.Clear();
        tooltip.Dispose();
        slotTex?.Dispose();
        readyTex?.Dispose();
        warnTex?.Dispose();
        sepTex?.Dispose();
    }
}
