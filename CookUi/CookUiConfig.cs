using System;
using Vintagestory.API.Client;

namespace CookUi;

/// <summary>Client settings, saved to ModConfig/cookui.json.</summary>
public class CookUiConfig
{
    public const string FileName = "cookui.json";

    /// <summary>Open the recipe panel automatically whenever a firepit with a pot is opened.</summary>
    public bool AutoOpen { get; set; } = true;

    /// <summary>Show the clay oven panel while looking at an oven.</summary>
    public bool ShowOvenPanel { get; set; } = true;

    public static CookUiConfig Load(ICoreClientAPI capi)
    {
        try
        {
            var cfg = capi.LoadModConfig<CookUiConfig>(FileName);
            if (cfg != null) return cfg;
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[cookui] Could not read {0}, using defaults: {1}", FileName, e.Message);
        }
        var fresh = new CookUiConfig();
        fresh.Save(capi);
        return fresh;
    }

    public void Save(ICoreClientAPI capi)
    {
        try { capi.StoreModConfig(this, FileName); }
        catch (Exception e) { capi.Logger.Warning("[cookui] Could not save {0}: {1}", FileName, e.Message); }
    }
}
