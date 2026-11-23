using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace CookUi;

/// <summary>
/// Draws rows of "[meal] | [ing] [ing] [ing] [ing]", mirroring the pot's slots.
/// Ingredients still needed show as dark "shadow" icons, optional ones fainter still.
/// Hovering any icon shows its (localized) name. Reads the mouse position directly,
/// so the panel never needs to take mouse events away from the firepit dialog.
/// </summary>
internal class GuiElementRecipeRows : GuiElement
{
    public const double SlotSize = 40;
    public const double SlotGap = 4;
    public const double SeparatorGap = 14;
    public const double RowGap = 6;
    /// <summary>How long each icon of a rotating slot stays up.</summary>
    private const float CycleSeconds = 1f;

    private static readonly int RequiredColor = ColorUtil.ToRgba(170, 95, 95, 95);
    private static readonly int OptionalColor = ColorUtil.ToRgba(90, 70, 70, 70);

    private readonly List<RowView> rows;
    private readonly int slotCount;
    private readonly ElementBounds clipBounds;
    private readonly Dictionary<string, LoadedTexture> textCache = new();
    private readonly TooltipRenderer tooltip;
    private readonly Dictionary<ItemStack, ItemSlot> slotCache = new(ReferenceEqualityComparer.Instance);
    private LoadedTexture? slotTex, readyTex, warnTex, sepTex;
    private SlotView? hover;
    /// <summary>Drives rotating icons; paused while the mouse is over a recipe row.</summary>
    private float cycleClock;
    private int cycleFrame;

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

        // Hovering a recipe freezes the rotation so the icons hold still while the tooltip is read.
        bool overRow = mouseInView && OverAnyRow(mx, my);
        if (!overRow)
        {
            cycleClock += deltaTime;
            if (cycleClock >= CycleSeconds)
            {
                cycleClock %= CycleSeconds;
                cycleFrame++;
            }
        }

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            double y = Bounds.renderY + scaled(RowY(r));
            // Skip rows scrolled fully out of view.
            if (y + size < clipBounds.renderY || y > clipBounds.renderY + clipBounds.InnerHeight) continue;

            var bg = row.State switch
            {
                MatchState.Ready => readyTex,
                MatchState.TooMuch or MatchState.WrongAmounts => warnTex,
                _ => slotTex,
            };
            double mealX = Bounds.renderX + scaled(SlotX(-1));
            Blit(bg, mealX, y);
            if (sepTex != null) Blit(sepTex, Bounds.renderX + scaled(SlotSize + SeparatorGap / 2) - sepTex.Width / 2.0, y);

            // Always render every slot; only remember the first tooltip under the mouse.
            // (Don't use `hover ??= RenderSlot(...)`: it skips rendering once hover is set.)
            var t = RenderSlot(row.Result, mealX, y, size, mx, my);
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

    private bool OverAnyRow(int mx, int my)
    {
        double left = Bounds.renderX, right = left + scaled(WidthFor(slotCount));
        if (mx < left || mx >= right) return false;
        double size = scaled(SlotSize);
        for (int r = 0; r < rows.Count; r++)
        {
            double y = Bounds.renderY + scaled(RowY(r));
            if (my >= y && my < y + size) return true;
        }
        return false;
    }

    /// <summary>
    /// Draws the hovered slot's tooltip. Call during the GUI pass but after the composer has
    /// rendered (scroll clip already released), e.g. from the dialog's OnRenderGUI after base.
    /// Not from PostRenderInteractiveElements: that runs in OnFinalizeFrame, where the GUI
    /// shader isn't bound and 2D drawing crashes.
    /// </summary>
    public void RenderTooltip()
    {
        tooltip.Render(hover == null ? null : TooltipLines(hover), api.Input.MouseX, api.Input.MouseY);
    }

    private List<TooltipLine> TooltipLines(SlotView view)
    {
        var lines = new List<TooltipLine>();
        if (!string.IsNullOrEmpty(view.Tooltip)) lines.Add(new TooltipLine(view.Tooltip));
        if (view.Categories is { Count: > 1 } cats)
        {
            int current = view.Cycle is { Count: > 1 } cycle ? cycleFrame % cycle.Count : 0;
            for (int i = 0; i < cats.Count; i++)
            {
                lines.Add(new TooltipLine("- " + cats[i], i == current));
            }
        }
        return lines;
    }

    /// <summary>The icon to draw this frame: the current step of the rotation, or the fixed slot.</summary>
    private ItemSlot? CurrentSlot(SlotView view)
    {
        var stack = view.Cycle is { Count: > 1 } cycle ? cycle[cycleFrame % cycle.Count] : view.Icon;
        if (stack == null) return null;
        // The renderer wants slots; make one per stack once, not every frame.
        if (!slotCache.TryGetValue(stack, out var slot)) slotCache[stack] = slot = new DummySlot(stack);
        return slot;
    }

    private void Blit(LoadedTexture? tex, double x, double y)
    {
        if (tex == null) return;
        api.Render.Render2DTexturePremultipliedAlpha(tex.TextureId, x, y, tex.Width, tex.Height, 50);
    }

    /// <returns>The slot when the mouse is over it and it has a tooltip.</returns>
    private SlotView? RenderSlot(SlotView view, double x, double y, double size, int mx, int my)
    {
        if (view.Choices is { Count: > 0 } choices)
        {
            RenderMosaic(view, choices, x, y, size);
        }
        else if (CurrentSlot(view) is { Itemstack: not null } slot)
        {
            int color = view.Kind switch
            {
                SlotKind.Required => RequiredColor,
                SlotKind.Optional => OptionalColor,
                _ => -1,
            };
            api.Render.RenderItemstackToGui(slot, x + size / 2, y + size / 2, 100,
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
        return inside && (view.Tooltip != null || view.Categories != null) ? view : null;
    }

    /// <summary>Up to four small icons in a 2×2 grid; with more choices the last cell says "+N".</summary>
    private void RenderMosaic(SlotView view, List<SlotView> choices, double x, double y, double size)
    {
        int color = view.Kind == SlotKind.Required ? RequiredColor : OptionalColor;
        bool overflow = choices.Count > 4;
        int icons = overflow ? 3 : choices.Count;
        double cell = size / 2;
        for (int i = 0; i < icons; i++)
        {
            var slot = CurrentSlot(choices[i]);
            if (slot?.Itemstack == null) continue;
            double cx = x + (i % 2) * cell + cell / 2;
            double cy = y + (i / 2) * cell + cell / 2;
            api.Render.RenderItemstackToGui(slot, cx, cy, 100, (float)(cell * 0.62), color, true, false, false);
        }
        if (overflow)
        {
            var tex = Text("+" + (choices.Count - 3), new[] { 0.8, 0.8, 0.8, 1.0 });
            api.Render.Render2DTexturePremultipliedAlpha(tex.TextureId,
                x + cell + (cell - tex.Width) / 2, y + cell + (cell - tex.Height) / 2, tex.Width, tex.Height, 200);
        }
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
