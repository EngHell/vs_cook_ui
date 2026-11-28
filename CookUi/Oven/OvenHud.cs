using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CookUi.Oven;

/// <summary>
/// Read-only panel for the clay oven, shown while the player looks at one. Vanilla's oven has
/// no dialog and the server only takes items through right-clicks, so this stays client-only:
/// loading and unloading still works the vanilla way, the panel just shows what's going on.
/// </summary>
internal sealed class OvenHud : HudElement
{
    /// <summary>Distance between the crosshair and the panel's left edge.</summary>
    private const double CrosshairGap = 60;

    private readonly CookUiConfig config;
    private readonly OvenView view = new();
    private string composedKey = "";

    public OvenHud(ICoreClientAPI capi, CookUiConfig config) : base(capi)
    {
        this.config = config;
    }

    public override bool Focusable => false;
    public override bool ShouldReceiveMouseEvents() => false;
    public override bool ShouldReceiveKeyboardEvents() => false;

    /// <summary>Called periodically by the mod system; shows, refreshes or hides the panel.</summary>
    public void Update()
    {
        var oven = config.ShowOvenPanel ? LookedAtOven() : null;
        if (oven == null)
        {
            if (IsOpened()) TryClose();
            return;
        }

        view.Read(capi.World, oven);
        // The static layer holds the title and slot backgrounds.
        string key = Lang.CurrentLocale + "|" + view.Title + "|" + view.Rows.Length;
        if (key != composedKey || SingleComposer == null)
        {
            composedKey = key;
            Compose();
        }
        if (!IsOpened()) TryOpen();
    }

    private BlockEntityOven? LookedAtOven()
    {
        var pos = capi.World.Player?.CurrentBlockSelection?.Position;
        return pos == null ? null : capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityOven;
    }

    private void Compose()
    {
        double width = GuiElementOvenRows.Width;
        double height = GuiElementOvenRows.HeightFor(view.Rows.Length);

        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        var rowsBounds = ElementBounds.Fixed(0, 0, width, height);
        bgBounds.WithChildren(rowsBounds);

        // Right of the crosshair, vertically centred, so it never covers the oven itself.
        double offsetX = width / 2 + GuiStyle.ElementToDialogPadding + CrosshairGap;
        var dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle)
            .WithFixedAlignmentOffset(offsetX, 0);

        SingleComposer?.Dispose();
        SingleComposer = capi.Gui.CreateCompo("cookui-oven", dialogBounds)
            .AddShadedDialogBG(bgBounds, false)
            .BeginChildElements(bgBounds)
                .AddInteractiveElement(new GuiElementOvenRows(capi, rowsBounds, view), "oven")
            .EndChildElements()
            .Compose();
    }
}
