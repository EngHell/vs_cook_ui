using System;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CookUi.Oven;

/// <summary>One oven slot as the panel shows it: what's in, what it turns into and how far along it is.</summary>
internal sealed class OvenRow
{
    public ItemStack? Input;
    /// <summary>What the item turns into once this baking stage ends; null if it doesn't change any more.</summary>
    public ItemStack? Result;
    /// <summary>The next stage is a burnt one (charred bread...), so the arrow warns instead.</summary>
    public bool Burns;
    /// <summary>Progress through the current stage, 0 to 1.</summary>
    public float Progress;
    public float Temp;
    /// <summary>Temperature the item must pass before it bakes at all.</summary>
    public float BakeTemp;

    /// <summary>This row holds the oven's firewood (it shares the first bakeable slot).</summary>
    public bool IsFuel;
    /// <summary>Remaining fuel, 0 to 1; 0 while unlit.</summary>
    public float FuelLeft;
    public float FuelSeconds;

    // Recomputed only when the slot's contents change.
    internal ItemStack? cachedFor;
    internal float levelFrom, levelTo = 1;
}

/// <summary>
/// Snapshot of a clay oven, refreshed from its client-side block entity every tick. The server
/// already syncs everything shown here (oven and per-item temperature, bake level, fuel time):
/// we only read it. Private fields are read by reflection and skipped if a game update renames them.
/// </summary>
internal sealed class OvenView
{
    private static readonly FieldInfo? BakingDataField =
        typeof(BlockEntityOven).GetField("bakingData", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? FuelBurnTimeField =
        typeof(BlockEntityOven).GetField("fuelBurnTime", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>BlockEntityOven.TryIgnite: fuelBurnTime = 45 + 5 per firewood.</summary>
    private const float BaseBurnTime = 45, BurnTimePerFuel = 5;
    /// <summary>BlockEntityOven.OnBurnTick runs fuel time down 1.25× faster than real time.</summary>
    private const float BurnTimeSpeed = 1.25f;
    /// <summary>BlockEntityOven.HeatInput: items only bake once they reach this, whatever their own browning point.</summary>
    private const float MinBakeTemp = 100;

    public string Title = "";
    public float OvenTemp;
    public bool Burning;
    public string? Hint;
    public OvenRow[] Rows = Array.Empty<OvenRow>();

    public void Read(IWorldAccessor world, BlockEntityOven oven)
    {
        Title = oven.Block.GetPlacedBlockName(world, oven.Pos);
        OvenTemp = oven.ovenTemperature;
        Burning = oven.IsBurning;
        bool fuelMode = oven.HasFuel;

        int count = Math.Max(1, oven.bakeableCapacity);
        if (Rows.Length != count)
        {
            Rows = new OvenRow[count];
            for (int i = 0; i < count; i++) Rows[i] = new OvenRow();
        }

        var data = BakingDataField?.GetValue(oven) as OvenItemData[];
        float fuelTime = FuelBurnTimeField?.GetValue(oven) is float f ? f : 0;
        bool hasBakeables = false;

        for (int i = 0; i < count; i++)
        {
            var row = Rows[i];
            var stack = oven.Inventory[i]?.Itemstack;
            row.Input = stack;
            row.IsFuel = fuelMode && i == 0;

            if (stack == null)
            {
                row.Result = null;
                row.cachedFor = null;
                continue;
            }

            if (row.IsFuel)
            {
                float max = BaseBurnTime + BurnTimePerFuel * stack.StackSize;
                row.FuelLeft = Burning ? GameMath.Clamp(fuelTime / max, 0, 1) : 0;
                row.FuelSeconds = fuelTime / BurnTimeSpeed;
                row.Result = null;
                row.cachedFor = null;
                continue;
            }

            hasBakeables = true;
            if (row.cachedFor == null || !SameStack(world, row.cachedFor, stack))
            {
                Describe(world, row, stack);
                row.cachedFor = stack.Clone();
            }

            var d = data != null && i < data.Length ? data[i] : null;
            row.Temp = d?.temp ?? 0;
            row.BakeTemp = Math.Max(MinBakeTemp, d is { BrowningPoint: > 0 } ? d.BrowningPoint : 160);
            row.Progress = d == null || row.levelTo <= row.levelFrom ? 0
                : GameMath.Clamp((d.BakedLevel - row.levelFrom) / (row.levelTo - row.levelFrom), 0, 1);
        }

        // Same advice as the oven's own block info.
        Hint = null;
        if (!Burning && OvenTemp <= 25) Hint = Lang.Get("clayoven-preheat-warning");
        else if (!Burning && OvenTemp < MinBakeTemp && hasBakeables) Hint = Lang.Get("Reheat to continue baking");
    }

    private static bool SameStack(IWorldAccessor world, ItemStack a, ItemStack b) =>
        a.StackSize == b.StackSize && a.Equals(world, b, GlobalConstants.IgnoredStackAttributes);

    /// <summary>Works out the stage's result the same way BlockEntityOven.IncrementallyBake does.</summary>
    private static void Describe(IWorldAccessor world, OvenRow row, ItemStack input)
    {
        var props = BakingProperties.ReadFrom(input);
        row.levelFrom = props?.LevelFrom ?? 0;
        row.levelTo = props?.LevelTo ?? 1;
        row.Result = null;

        if (props?.ResultCode is { } code)
        {
            var loc = new AssetLocation(code);
            ItemStack? result = input.Class == EnumItemClass.Block
                ? world.GetBlock(loc) is { } block ? new ItemStack(block) : null
                : world.GetItem(loc) is { } item ? new ItemStack(item) : null;
            if (result != null)
            {
                // Pies carry their filling over this way; without it they'd show as empty crusts.
                try { input.Collectible.GetCollectibleInterface<IBakeableCallback>()?.OnBaked(input.Clone(), result); }
                catch (Exception) { /* cosmetic only */ }
            }
            row.Result = result;
        }
        else
        {
            var comb = input.Collectible.GetCombustibleProperties(world, input, null);
            if (comb?.SmeltingType == EnumSmeltType.Bake && comb.SmeltedStack?.ResolvedItemstack is { } smelted)
            {
                row.Result = smelted.Clone();
            }
        }

        // No generic "burnt" flag exists; vanilla's burnt stages (bread, pies) are all called charred.
        row.Burns = row.Result?.Collectible?.Code?.Path.Contains("charred") == true;
    }
}
