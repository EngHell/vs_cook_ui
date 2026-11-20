using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace CookUi;

/// <summary>
/// Client-only mod: shows which cooking recipes the pot in an open firepit can make.
/// </summary>
public class CookUiModSystem : ModSystem
{
    private ICoreClientAPI? capi;
    private GuiCookingPreview? preview;
    private long tickListenerId;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        preview = new GuiCookingPreview(api);
        // Cheap poll: finds an open firepit dialog and refreshes only when pot contents change.
        tickListenerId = api.Event.RegisterGameTickListener(_ => preview.Update(), 100);
    }

    public override void Dispose()
    {
        if (preview != null)
        {
            capi?.Event.UnregisterGameTickListener(tickListenerId);
            preview.TryClose();
            preview.Dispose();
            preview = null;
        }
    }
}
