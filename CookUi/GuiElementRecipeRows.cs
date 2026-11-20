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
    private readonly Dictionary<string, LoadedTexture> textCache = new();
    private LoadedTexture? tooltipTex;
    private string? tooltipText;

    public GuiElementRecipeRows(ICoreClientAPI capi, ElementBounds bounds, List<RowView> rows, int slotCount)
        : base(capi, bounds)
    {
        this.rows = rows;
        this.slotCount = slotCount;
    }

    public static double WidthFor(int slotCount) => SlotSize + SeparatorGap + slotCount * (SlotSize + SlotGap) - SlotGap;
    public static double HeightFor(int rowCount) => Math.Max(0, rowCount * (SlotSize + RowGap) - RowGap);

    // Unscaled offsets of a slot within the element. index -1 is the meal slot.
    private static double SlotX(int index) => index < 0 ? 0 : SlotSize + SeparatorGap + index * (SlotSize + SlotGap);
    private static double RowY(int row) => row * (SlotSize + RowGap);

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        double size = scaled(SlotSize);

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            double y = Bounds.drawY + scaled(RowY(r));

            DrawSlotBg(ctx, Bounds.drawX + scaled(SlotX(-1)), y, size, row.State);

            // Separator between meal and ingredients.
            double sx = Bounds.drawX + scaled(SlotSize + SeparatorGap / 2);
            ctx.SetSourceRGBA(1, 1, 1, 0.25);
            ctx.LineWidth = scaled(1.5);
            ctx.MoveTo(sx, y + scaled(6));
            ctx.LineTo(sx, y + size - scaled(6));
            ctx.Stroke();

            for (int i = 0; i < slotCount; i++)
            {
                DrawSlotBg(ctx, Bounds.drawX + scaled(SlotX(i)), y, size, null);
            }
        }
    }

    private void DrawSlotBg(Context ctx, double x, double y, double size, PredictionState? state)
    {
        ctx.SetSourceRGBA(GuiStyle.DialogSlotBackColor);
        RoundRectangle(ctx, x, y, size, size, GuiStyle.ElementBGRadius);
        ctx.Fill();
        EmbossRoundRectangleElement(ctx, x, y, size, size, true, 2, 2);

        double[]? border = state switch
        {
            PredictionState.Ready => new[] { 0.5, 0.82, 0.5, 0.9 },
            PredictionState.TooMuch or PredictionState.RatioWrong => new[] { 0.88, 0.63, 0.31, 0.9 },
            _ => null,
        };
        if (border != null)
        {
            ctx.SetSourceRGBA(border);
            ctx.LineWidth = scaled(2);
            RoundRectangle(ctx, x + scaled(1), y + scaled(1), size - scaled(2), size - scaled(2), GuiStyle.ElementBGRadius);
            ctx.Stroke();
        }
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        double size = scaled(SlotSize);
        int mx = api.Input.MouseX, my = api.Input.MouseY;
        string? hover = null;

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            double y = Bounds.renderY + scaled(RowY(r));
            // Always render every slot; only remember the first tooltip under the mouse.
            // (Don't use `hover ??= RenderSlot(...)`: it skips rendering once hover is set.)
            var t = RenderSlot(row.Meal, Bounds.renderX + scaled(SlotX(-1)), y, size, mx, my);
            hover ??= t;
            for (int i = 0; i < row.Ingredients.Count && i < slotCount; i++)
            {
                t = RenderSlot(row.Ingredients[i], Bounds.renderX + scaled(SlotX(i)), y, size, mx, my);
                hover ??= t;
            }
        }

        RenderTooltip(hover, mx, my);
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

    private void RenderTooltip(string? text, int mx, int my)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text != tooltipText)
        {
            tooltipTex?.Dispose();
            tooltipText = text;
            var bg = new TextBackground
            {
                FillColor = GuiStyle.DialogStrongBgColor,
                BorderColor = GuiStyle.DialogBorderColor,
                BorderWidth = 2,
                Padding = 6,
                Radius = 3,
            };
            tooltipTex = api.Gui.TextTexture.GenTextTexture(text, CairoFont.WhiteSmallText(), (int)scaled(260), bg);
        }
        if (tooltipTex == null) return;

        double x = mx + scaled(14), y = my + scaled(14);
        if (x + tooltipTex.Width > api.Render.FrameWidth) x = mx - tooltipTex.Width - scaled(6);
        if (y + tooltipTex.Height > api.Render.FrameHeight) y = my - tooltipTex.Height - scaled(6);
        api.Render.Render2DTexturePremultipliedAlpha(tooltipTex.TextureId, x, y, tooltipTex.Width, tooltipTex.Height, 500);
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var t in textCache.Values) t.Dispose();
        textCache.Clear();
        tooltipTex?.Dispose();
        tooltipTex = null;
    }
}
