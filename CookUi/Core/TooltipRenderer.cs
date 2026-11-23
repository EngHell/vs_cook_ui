using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cairo;
using Vintagestory.API.Client;

namespace CookUi;

/// <summary>One block of tooltip text; highlighted lines are drawn in gold.</summary>
internal readonly record struct TooltipLine(string Text, bool Highlight = false);

/// <summary>
/// Small text popup drawn next to the mouse: one box, lines word-wrapped, highlighted ones in gold.
/// Cached until its text changes.
/// </summary>
internal sealed class TooltipRenderer : IDisposable
{
    private static readonly double[] Gold = { 1.0, 0.8, 0.3, 1.0 };
    private const double Padding = 6;
    private const double MaxWidth = 260;

    private readonly ICoreClientAPI capi;
    private LoadedTexture? tex;
    private string? key;

    public TooltipRenderer(ICoreClientAPI capi) => this.capi = capi;

    public void Render(string? text, int mx, int my) =>
        Render(string.IsNullOrEmpty(text) ? null : new[] { new TooltipLine(text) }, mx, my);

    public void Render(IReadOnlyList<TooltipLine>? lines, int mx, int my)
    {
        if (lines == null || lines.Count == 0) return;
        string newKey = string.Join("\n", lines.Select(l => (l.Highlight ? "*" : " ") + l.Text));
        if (newKey != key)
        {
            tex?.Dispose();
            tex = Build(lines);
            key = newKey;
        }
        if (tex == null) return;

        double x = mx + GuiElement.scaled(14), y = my + GuiElement.scaled(14);
        if (x + tex.Width > capi.Render.FrameWidth) x = mx - tex.Width - GuiElement.scaled(6);
        if (y + tex.Height > capi.Render.FrameHeight) y = my - tex.Height - GuiElement.scaled(6);
        capi.Render.Render2DTexturePremultipliedAlpha(tex.TextureId, x, y, tex.Width, tex.Height, 500);
    }

    private LoadedTexture Build(IReadOnlyList<TooltipLine> lines)
    {
        var white = CairoFont.WhiteSmallText();
        var gold = CairoFont.WhiteSmallText().WithColor(Gold);
        double pad = GuiElement.scaled(Padding);
        double maxWidth = GuiElement.scaled(MaxWidth);
        double lineHeight = capi.Gui.Text.GetLineHeight(white);

        // Wrap every block into physical lines, remembering which font draws each.
        var rows = new List<(string text, CairoFont font)>();
        foreach (var line in lines)
        {
            var font = line.Highlight ? gold : white;
            foreach (var part in line.Text.Split('\n'))
            {
                foreach (var wrapped in Wrap(font, part, maxWidth)) rows.Add((wrapped, font));
            }
        }

        double textWidth = rows.Count == 0 ? 0 : rows.Max(r => r.font.GetTextExtents(r.text).XAdvance);
        int w = (int)Math.Ceiling(textWidth + 2 * pad);
        int h = (int)Math.Ceiling(rows.Count * lineHeight + 2 * pad);

        using var surface = new ImageSurface(Format.Argb32, w, h);
        using (var ctx = new Context(surface))
        {
            GuiElement.RoundRectangle(ctx, 1, 1, w - 2, h - 2, 3);
            ctx.SetSourceRGBA(GuiStyle.DialogStrongBgColor);
            ctx.FillPreserve();
            ctx.SetSourceRGBA(GuiStyle.DialogBorderColor);
            ctx.LineWidth = 2;
            ctx.Stroke();

            double y = pad;
            foreach (var (text, font) in rows)
            {
                font.SetupContext(ctx);
                capi.Gui.Text.DrawTextLine(ctx, font, text, pad, y);
                y += lineHeight;
            }
        }
        var result = new LoadedTexture(capi);
        capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref result);
        return result;
    }

    /// <summary>Greedy word wrap to <paramref name="maxWidth"/> pixels.</summary>
    private static IEnumerable<string> Wrap(CairoFont font, string text, double maxWidth)
    {
        if (font.GetTextExtents(text).XAdvance <= maxWidth)
        {
            yield return text;
            yield break;
        }
        var current = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && font.GetTextExtents(candidate).XAdvance > maxWidth)
            {
                yield return current.ToString();
                current.Clear().Append(word);
            }
            else
            {
                current.Clear().Append(candidate);
            }
        }
        if (current.Length > 0) yield return current.ToString();
    }

    public void Dispose()
    {
        tex?.Dispose();
        tex = null;
        key = null;
    }
}
