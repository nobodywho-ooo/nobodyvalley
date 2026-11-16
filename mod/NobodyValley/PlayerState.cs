using Microsoft.Xna.Framework;
using StardewValley;

namespace NobodyValley;

/// <summary>
/// Per-farmer state stored in <c>Farmer.modData</c>, which the game syncs to all players.
/// This is how the agents see what the others are doing and avoid working on the same tile.
/// </summary>
internal static class PlayerState
{
    private const string ActivityKey = "nobodywho.NobodyValley/activity";
    private const string ClaimKey = "nobodywho.NobodyValley/claim";

    public static void SetActivity(string? activity) => Set(ActivityKey, activity);

    public static string? GetActivity(Farmer f) => f.modData.TryGetValue(ActivityKey, out var v) ? v : null;

    /// <summary>Mark the tile this farmer is about to work on.</summary>
    public static void SetClaim(Vector2? tile) => Set(ClaimKey, tile is { } t ? $"{(int)t.X},{(int)t.Y}" : null);

    /// <summary>Tiles currently claimed by other online farmers.</summary>
    public static HashSet<Vector2> OthersClaims()
    {
        var set = new HashSet<Vector2>();
        foreach (var f in Game1.getOnlineFarmers())
        {
            if (f.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
                continue;
            if (f.modData.TryGetValue(ClaimKey, out var v))
            {
                var parts = v.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
                    set.Add(new Vector2(x, y));
            }
        }
        return set;
    }

    private static void Set(string key, string? value)
    {
        var p = Game1.player;
        if (p == null)
            return;
        if (value == null)
            p.modData.Remove(key);
        else
            p.modData[key] = value;
    }
}
