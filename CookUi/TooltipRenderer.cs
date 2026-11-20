using System;
using Vintagestory.API.Client;

namespace CookUi;

/// <summary>Small text popup drawn next to the mouse, cached until its text changes.</summary>
public sealed class TooltipRenderer : IDisposable
{
    private readonly ICoreClientAPI capi;
    private LoadedTexture? tex;
    private string? text;

    public TooltipRenderer(ICoreClientAPI capi) => this.capi = capi;

    public void Render(string? newText, int mx, int my)
    {
        if (string.IsNullOrEmpty(newText)) return;
        if (newText != text)
        {
            tex?.Dispose();
            text = newText;
            var bg = new TextBackground
            {
                FillColor = GuiStyle.DialogStrongBgColor,
                BorderColor = GuiStyle.DialogBorderColor,
                BorderWidth = 2,
                Padding = 6,
                Radius = 3,
            };
            tex = capi.Gui.TextTexture.GenTextTexture(newText, CairoFont.WhiteSmallText(), (int)GuiElement.scaled(260), bg);
        }
        if (tex == null) return;

        double x = mx + GuiElement.scaled(14), y = my + GuiElement.scaled(14);
        if (x + tex.Width > capi.Render.FrameWidth) x = mx - tex.Width - GuiElement.scaled(6);
        if (y + tex.Height > capi.Render.FrameHeight) y = my - tex.Height - GuiElement.scaled(6);
        capi.Render.Render2DTexturePremultipliedAlpha(tex.TextureId, x, y, tex.Width, tex.Height, 500);
    }

    public void Dispose()
    {
        tex?.Dispose();
        tex = null;
        text = null;
    }
}
