using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using xTile.Dimensions;

namespace NobodyValley;

/// <summary>Routing between maps via warps, doors and building entrances.</summary>
internal static class Navigation
{
    /// <summary>A way out of a location into a neighbouring one.</summary>
    public sealed record Exit(Point Stand, int Facing, Action<Farmer> Use);

    public static GameLocation? GetLocation(string name) =>
        Game1.getLocationFromName(name) ?? Game1.getLocationFromName(name, isStructure: true);

    /// <summary>Names of locations directly reachable from <paramref name="loc"/>.</summary>
    public static IEnumerable<string> Neighbours(GameLocation loc)
    {
        foreach (var w in loc.warps)
            yield return w.TargetName;
        foreach (var d in loc.doors.Values)
            yield return d;
        foreach (var b in loc.buildings)
        {
            if (b.humanDoor.X >= 0 && b.GetIndoors() != null)
                yield return b.GetIndoorsName();
        }
    }

    /// <summary>Breadth-first search over the location graph. Returns location names including start and end.</summary>
    public static List<string>? Route(string from, string to, ISet<(string, string)>? blocked = null)
    {
        var prev = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [from] = null };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur.Equals(to, StringComparison.OrdinalIgnoreCase))
            {
                var path = new List<string>();
                for (string? n = cur; n != null; n = prev[n])
                    path.Add(n);
                path.Reverse();
                return path;
            }
            var loc = GetLocation(cur);
            if (loc == null)
                continue;
            foreach (var next in Neighbours(loc))
            {
                if (next == null || prev.ContainsKey(next) || IsOffLimits(next) || blocked?.Contains((cur, next)) == true)
                    continue;
                prev[next] = cur;
                queue.Enqueue(next);
            }
        }
        return null;
    }

    /// <summary>Places that are pointless/hazardous to route through.</summary>
    private static bool IsOffLimits(string name) =>
        name.StartsWith("UndergroundMine") || name.StartsWith("VolcanoDungeon") || name == "Temp";

    public static IEnumerable<Exit> ExitsTo(GameLocation loc, string next)
    {
        // Building doors (farmhouse, cabins, barns, ...).
        foreach (var b in loc.buildings)
        {
            if (b.humanDoor.X < 0 || !b.HasIndoorsName(next))
                continue;
            var door = b.getPointForHumanDoor();
            var building = b;
            yield return new Exit(new Point(door.X, door.Y + 1), Jobs.Steps.Up,
                who => building.doAction(new Vector2(door.X, door.Y), who));
        }

        // Map doors (shops, houses): stand below, "click" the door so the vanilla lock rules apply.
        foreach (var pair in loc.doors.Pairs)
        {
            if (!pair.Value.Equals(next, StringComparison.OrdinalIgnoreCase))
                continue;
            var door = pair.Key;
            yield return new Exit(new Point(door.X, door.Y + 1), Jobs.Steps.Up,
                who => loc.checkAction(new Location(door.X, door.Y), Game1.viewport, who));
        }

        // Plain warps, usually along a map edge (often off-map), so stand on the closest walkable tile.
        foreach (var w in loc.warps)
        {
            if (!w.TargetName.Equals(next, StringComparison.OrdinalIgnoreCase))
                continue;
            var warpTile = new Point(w.X, w.Y);
            var stand = ClosestStandable(loc, warpTile, 3);
            if (stand == null)
                continue;
            var warp = w;
            yield return new Exit(stand.Value, Jobs.Steps.FacingTowards(stand.Value, warpTile),
                who => Game1.warpFarmer(warp.TargetName, warp.TargetX, warp.TargetY, warp.flipFarmer.Value));
        }
    }

    public static Point? ClosestStandable(GameLocation loc, Point centre, int radius)
    {
        Point? best = null;
        int bestDist = int.MaxValue;
        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
        {
            var t = new Point(centre.X + dx, centre.Y + dy);
            int d = Math.Abs(dx) + Math.Abs(dy);
            if (d < bestDist && Jobs.Steps.IsStandable(loc, t))
            {
                best = t;
                bestDist = d;
            }
        }
        return best;
    }
}
