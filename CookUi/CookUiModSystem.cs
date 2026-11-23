using System;
using System.Collections.Generic;
using System.Linq;
using CookUi.Compat.AculinaryArtillery;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace CookUi;

/// <summary>
/// Client-only mod: shows which recipes an open cooking station can make. Stations and recipe
/// sources are registered through <see cref="RegisterStation"/> and <see cref="RegisterRecipeSource"/>,
/// the same way for vanilla, for built-in mod compat and for other mods (see Core/Api.cs).
/// </summary>
public class CookUiModSystem : ModSystem
{
    /// <summary>Station id of the vanilla firepit (pot, or any container with cooking slots).</summary>
    public const string FirepitStationId = "firepit";

    private ICoreClientAPI? capi;
    private CookUiConfig? config;
    private readonly List<ICookingStation> stations = new();
    private readonly Dictionary<string, List<IRecipeSource>> sources = new();
    private readonly Dictionary<string, RecipeWindow> windows = new();
    private long tickListenerId;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    /// <summary>
    /// Adds a station (a dialog the recipe panel attaches to). Safe to call at any point on the
    /// client, before or after Cook UI started; a station without sources never shows anything.
    /// </summary>
    public void RegisterStation(ICookingStation station)
    {
        if (stations.Any(s => s.Id == station.Id))
        {
            capi?.Logger.Warning("[cookui] Station '{0}' is already registered; ignoring the new one.", station.Id);
            return;
        }
        stations.Add(station);
    }

    /// <summary>
    /// Adds recipes to a station, existing or registered later. Several sources can serve one
    /// station: each answers for the contents it recognises (pot vs saucepan in a firepit...).
    /// </summary>
    public void RegisterRecipeSource(string stationId, IRecipeSource source)
    {
        if (!sources.TryGetValue(stationId, out var list)) sources[stationId] = list = new List<IRecipeSource>();
        list.Add(source);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        config = CookUiConfig.Load(api);

        RegisterStation(new FirepitStation());
        RegisterRecipeSource(FirepitStationId, CookingRecipeSource.Vanilla(api));

        if (api.ModLoader.IsModEnabled(Aca.ModId))
        {
            RegisterRecipeSource(FirepitStationId, new SimmerRecipeSource(api));
            RegisterStation(new MixingBowlStation());
            // The bowl doesn't care whether the pot is dirty (BlockEntityMixingBowl.GetMatchingMixingRecipe).
            RegisterRecipeSource(MixingBowlStation.StationId,
                new CookingRecipeSource(api, () => Aca.MixingRecipes(api), dirtyPotsMakeFood: true));
            RegisterRecipeSource(MixingBowlStation.StationId, new KneadingRecipeSource(api));
        }

        // Cheap poll: finds open station dialogs and refreshes only when contents change.
        tickListenerId = api.Event.RegisterGameTickListener(_ => Tick(), 100);
    }

    private void Tick()
    {
        if (capi == null || config == null) return;
        foreach (var station in stations)
        {
            if (!windows.TryGetValue(station.Id, out var window))
            {
                // Created lazily, so stations registered by other mods at any time are picked up.
                if (!sources.TryGetValue(station.Id, out var list)) sources[station.Id] = list = new List<IRecipeSource>();
                windows[station.Id] = window = new RecipeWindow(capi, config, station, list);
            }
            try
            {
                window.Update();
            }
            catch (Exception e)
            {
                capi.Logger.Error("[cookui] Station '{0}' failed to update: {1}", station.Id, e);
            }
        }
    }

    public override void Dispose()
    {
        capi?.Event.UnregisterGameTickListener(tickListenerId);
        foreach (var w in windows.Values)
        {
            w.TryClose();
            w.Dispose();
        }
        windows.Clear();
    }
}
