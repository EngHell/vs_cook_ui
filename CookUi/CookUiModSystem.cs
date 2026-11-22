using System.Collections.Generic;
using CookUi.Compat.AculinaryArtillery;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace CookUi;

/// <summary>
/// Client-only mod: shows which recipes an open cooking station (firepit pot, and with
/// A Culinary Artillery its mixing bowl and saucepans) can make.
/// </summary>
public class CookUiModSystem : ModSystem
{
    private ICoreClientAPI? capi;
    private readonly List<RecipeWindow> windows = new();
    private long tickListenerId;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        var config = CookUiConfig.Load(api);

        windows.Add(new FirepitPotWindow(api, config));
        if (api.ModLoader.IsModEnabled(Aca.ModId))
        {
            windows.Add(new MixingBowlWindow(api, config));
            windows.Add(new SaucepanWindow(api, config));
        }

        // Cheap poll: finds open station dialogs and refreshes only when contents change.
        tickListenerId = api.Event.RegisterGameTickListener(_ =>
        {
            foreach (var w in windows) w.Update();
        }, 100);
    }

    public override void Dispose()
    {
        capi?.Event.UnregisterGameTickListener(tickListenerId);
        foreach (var w in windows)
        {
            w.TryClose();
            w.Dispose();
        }
        windows.Clear();
    }
}
