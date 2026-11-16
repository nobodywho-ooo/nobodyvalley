using System.Collections;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.Tools;
using xTile.Dimensions;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace NobodyValley.Jobs;

/// <summary>Reusable building blocks for jobs. All operate on the local player.</summary>
internal static class Steps
{
    public const int Up = 0, Right = 1, Down = 2, Left = 3;

    private static readonly Point[] Neighbours = { new(0, 1), new(0, -1), new(1, 0), new(-1, 0) };

    // ---------------------------------------------------------------- movement

    /// <summary>Walk to an exact tile in the current location.</summary>
    public static IEnumerator WalkTo(Point tile, StepResult r, int facing = -1)
    {
        var p = Game1.player;
        var loc = p.currentLocation;
        if (p.TilePoint == tile)
        {
            if (facing >= 0)
                p.faceDirection(facing);
            r.Ok = true;
            yield break;
        }

        var pc = new PathFindController(p, loc, tile, facing);
        if (pc.pathToEndPoint == null || pc.pathToEndPoint.Count == 0)
        {
            r.Fail($"no path to {tile.X},{tile.Y}");
            yield break;
        }

        p.setRunning(true, force: true);
        p.controller = pc;
        int timeout = pc.pathToEndPoint.Count * 40 + 180;
        while (p.controller != null && p.currentLocation == loc && timeout-- > 0)
            yield return null;

        if (p.controller != null)
        {
            p.controller = null;
            p.Halt();
        }
        r.Ok = p.currentLocation == loc && p.TilePoint == tile;
        if (!r.Ok)
            r.Error = "got stuck on the way";
    }

    /// <summary>Walk next to a tile and face it (for using a tool on it, harvesting, ...).</summary>
    public static IEnumerator WalkAdjacent(Point target, StepResult r)
    {
        var p = Game1.player;
        var loc = p.currentLocation;
        var here = p.TilePoint;

        var candidates = Neighbours
            .Select(o => new Point(target.X + o.X, target.Y + o.Y))
            .Where(t => t == here || IsStandable(loc, t))
            .OrderBy(t => Dist(t, here))
            .Take(3)
            .ToList();

        foreach (var stand in candidates)
        {
            var walk = new StepResult();
            yield return WalkTo(stand, walk, FacingTowards(stand, target));
            if (walk.Ok)
            {
                p.faceDirection(FacingTowards(stand, target));
                r.Ok = true;
                yield break;
            }
        }
        r.Fail($"can't reach {target.X},{target.Y}");
    }

    /// <summary>Walk (through any number of maps) to a location.</summary>
    public static IEnumerator GoToLocation(string locationName, StepResult r)
    {
        // Map connections we couldn't use (e.g. an exit blocked by debris); route around them.
        var blocked = new HashSet<(string, string)>();
        string? lastError = null;
        for (int hops = 0; hops < 20; hops++)
        {
            var p = Game1.player;
            var here = p.currentLocation;
            if (here.NameOrUniqueName.Equals(locationName, StringComparison.OrdinalIgnoreCase))
            {
                r.Ok = true;
                yield break;
            }

            var route = Navigation.Route(here.NameOrUniqueName, locationName, blocked);
            if (route == null || route.Count < 2)
            {
                r.Fail(lastError ?? $"don't know how to get from {here.Name} to {locationName}");
                yield break;
            }

            var exits = Navigation.ExitsTo(here, route[1])
                .OrderBy(e => Dist(e.Stand, p.TilePoint))
                .Take(3)
                .ToList();

            bool moved = false;
            var tried = new List<string>();
            foreach (var exit in exits)
            {
                var walk = new StepResult();
                yield return WalkTo(exit.Stand, walk, exit.Facing);
                if (p.currentLocation != here)
                {
                    moved = true; // stepped onto a warp on the way
                    break;
                }
                if (!walk.Ok)
                {
                    tried.Add($"exit at {exit.Stand.X},{exit.Stand.Y}: {walk.Error}");
                    continue;
                }
                tried.Add($"exit at {exit.Stand.X},{exit.Stand.Y}: used it but nothing happened");

                exit.Use(p);
                for (int i = 0; i < 300 && p.currentLocation == here; i++)
                    yield return null;
                if (p.currentLocation != here)
                {
                    moved = true;
                    break;
                }
            }
            if (!moved)
            {
                ModEntry.Instance.Monitor.Log($"Route {here.NameOrUniqueName} -> {route[1]} failed: {(exits.Count == 0 ? "no exits found" : string.Join("; ", tried))}", StardewModdingAPI.LogLevel.Debug);
                lastError = exits.Count == 0 || tried.All(t => t.EndsWith("nothing happened"))
                    ? $"couldn't get from {here.Name} to {route[1]} - the door is probably locked at this hour ({Observer.FormatTime(Game1.timeOfDay)})"
                    : $"couldn't get from {here.Name} to {route[1]} - the way is blocked";
                blocked.Add((here.NameOrUniqueName, route[1]));
                continue; // try another route
            }
            yield return Wait.Ticks(10); // let the new map settle
        }
        r.Fail("gave up: too many map transitions");
    }

    // ---------------------------------------------------------------- tools

    /// <summary>Select an inventory item (doesn't have to be on the toolbar).</summary>
    public static void Equip(Item item)
    {
        var p = Game1.player;
        int idx = p.Items.IndexOf(item);
        if (idx >= 0 && p.CurrentToolIndex != idx)
            p.CurrentToolIndex = idx;
    }

    public static T? FindTool<T>() where T : Tool => Game1.player.Items.OfType<T>().FirstOrDefault();

    public static MeleeWeapon? FindScythe() => Game1.player.Items.OfType<MeleeWeapon>().FirstOrDefault(w => w.isScythe());

    /// <summary>
    /// Swing the given tool at the faced tile, with the normal (network-synced) animation.
    /// Mirrors what pressing the use-tool button does, but always targets the tile in front.
    /// </summary>
    public static IEnumerator Swing(Tool tool)
    {
        var p = Game1.player;
        if (p.Stamina < ModEntry.Instance.Config.MinEnergy && tool is not MeleeWeapon)
            throw new JobFailed("too tired (low energy) - eat something or go to bed");

        Equip(tool);
        yield return null;

        p.lastClick = Vector2.Zero; // => tool location = tile in front of the player
        p.BeginUsingTool();

        for (int i = 0; i < 180; i++)
        {
            yield return null;
            // Upgraded tools charge while the button is held; release straight away.
            if (p.canReleaseTool && p.UsingTool)
                p.EndUsingTool();
            if (!p.UsingTool && p.CanMove)
                break;
        }
        yield return Wait.Ticks(4);
    }

    // ---------------------------------------------------------------- helpers

    public static bool IsStandable(GameLocation loc, Point t)
    {
        if (!loc.isTileOnMap(t))
            return false;
        if (!loc.isTilePassable(new Location(t.X, t.Y), Game1.viewport))
            return false;
        if (loc.isWaterTile(t.X, t.Y))
            return false;
        var box = new Rectangle(t.X * 64 + 8, t.Y * 64 + 8, 48, 48);
        return !loc.isCollidingPosition(box, Game1.viewport, isFarmer: true, 0, glider: false, Game1.player);
    }

    public static int FacingTowards(Point from, Point to)
    {
        int dx = to.X - from.X, dy = to.Y - from.Y;
        if (Math.Abs(dx) > Math.Abs(dy))
            return dx > 0 ? Right : Left;
        return dy > 0 ? Down : Up;
    }

    public static int Dist(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    public static Point ToPoint(Vector2 v) => new((int)v.X, (int)v.Y);
}
