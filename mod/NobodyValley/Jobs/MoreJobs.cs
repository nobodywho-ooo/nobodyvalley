using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace NobodyValley.Jobs;

/// <summary>Small observation helpers for the map view and the farm summary.</summary>
internal static class MoreJobs
{
    private static Farmer Me => Game1.player;

    /// <summary>Forage items lying around, and wild spring onions (which the game implements as crops).</summary>
    public static IEnumerable<Vector2> ForageTiles(GameLocation loc) =>
        loc.objects.Pairs.Where(p => p.Value.IsSpawnedObject && p.Value.isForage()).Select(p => p.Key)
            .Concat(loc.terrainFeatures.Pairs.Where(p => p.Value is HoeDirt d && IsWildOnion(d)).Select(p => p.Key));

    public static int ForageCount(GameLocation? loc) => loc == null ? 0 : ForageTiles(loc).Count();

    public static bool IsWildOnion(HoeDirt d) =>
        d.crop is { forageCrop.Value: true } crop && crop.whichForageCrop.Value == Crop.forageCrop_springOnionID && d.readyForHarvest();

    /// <summary>The farm's machines (kegs, furnaces, ...) and what they're doing, as a player sees them.</summary>
    public static List<string> Machines()
    {
        var farm = Game1.getFarm();
        return farm.objects.Pairs
            .Where(p => p.Value.GetMachineData() != null)
            .OrderBy(p => Vector2.DistanceSquared(p.Key, Me.Tile))
            .Take(20)
            .Select(p =>
            {
                var o = p.Value;
                string state = o.readyForHarvest.Value && o.heldObject.Value != null
                    ? $"ready: {o.heldObject.Value.DisplayName}"
                    : o.heldObject.Value != null
                        ? $"working: {o.heldObject.Value.DisplayName}"
                        : "empty";
                return $"{o.DisplayName} at {(int)p.Key.X},{(int)p.Key.Y} ({state})";
            })
            .ToList();
    }
}