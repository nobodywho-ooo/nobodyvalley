using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;

namespace NobodyValley;

/// <summary>
/// A* for the farmer that prefers roads and paths (like villagers do), walks around crops instead of
/// across them, and avoids needless zig-zags, so agents look like they're walking somewhere
/// rather than taking a robotic shortest route.
/// </summary>
internal static class Pathing
{
    private static readonly Point[] Dirs = { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) };

    private const int TurnPenalty = 3;
    private const int PersonPenalty = 60; // walk around people rather than into them
    private const int MinStepCost = 5; // keeps the heuristic admissible
    private const int NodeLimit = 40000;

    /// <summary>Path from start to end (inclusive of both), or null if none was found.</summary>
    /// <param name="blocked">Tiles to treat as impassable (e.g. where someone stood in our way).</param>
    public static Stack<Point>? FindPath(GameLocation loc, Point start, Point end, Character who, ISet<Point>? blocked = null)
    {
        if (start == end)
            return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // The game's collision test ignores people while pathfinding, so steer around them ourselves.
        var people = new HashSet<Point>(loc.characters.Where(c => c != who).Select(c => c.TilePoint));
        foreach (var f in loc.farmers)
            if (f != who)
                people.Add(f.TilePoint);
        var path = Search(loc, start, end, who, people, blocked, out int expanded);
        long ms = sw.ElapsedMilliseconds;
        int shortest = PathFindController.findPath(start, end, PathFindController.isAtEndPoint, loc, who, 10000)?.Count ?? -1;
        ModEntry.Instance.Monitor.Log(
            $"Path in {loc.NameOrUniqueName} {start}->{end}: {(path == null ? "none" : $"{path.Count} steps (shortest {shortest}), {path.Count(t => StepCost(loc, t) <= 6)} on roads/paths")}, "
            + $"{expanded} nodes, {ms}ms", StardewModdingAPI.LogLevel.Debug);
        return path;
    }

    private static Stack<Point>? Search(GameLocation loc, Point start, Point end, Character who, HashSet<Point> people, ISet<Point>? blocked, out int expanded)
    {

        int w = loc.map.Layers[0].LayerWidth, h = loc.map.Layers[0].LayerHeight;
        var open = new PriorityQueue<(Point p, int dir), int>();
        var cost = new Dictionary<Point, int> { [start] = 0 };
        var cameFrom = new Dictionary<Point, Point>();
        var dirAt = new Dictionary<Point, int> { [start] = -1 };
        var passable = new Dictionary<Point, bool>();
        open.Enqueue((start, -1), 0);

        expanded = 0;
        while (open.TryDequeue(out var cur, out _))
        {
            if (cur.p == end)
                return Reconstruct(cameFrom, start, end);
            if (++expanded > NodeLimit)
                return null;
            if (cur.dir != dirAt.GetValueOrDefault(cur.p, -2))
                continue; // stale queue entry

            for (int d = 0; d < 4; d++)
            {
                var next = new Point(cur.p.X + Dirs[d].X, cur.p.Y + Dirs[d].Y);
                bool isEnd = next == end;
                if (!isEnd && (next.X < 0 || next.Y < 0 || next.X >= w || next.Y >= h))
                    continue;
                if (!isEnd && (blocked?.Contains(next) == true || !IsPassable(loc, next, who, passable)))
                    continue;

                int step = StepCost(loc, next) + (cur.dir >= 0 && cur.dir != d ? TurnPenalty : 0)
                    + (people.Contains(next) && !isEnd ? PersonPenalty : 0);
                int newCost = cost[cur.p] + step;
                if (cost.TryGetValue(next, out int old) && old <= newCost)
                    continue;
                cost[next] = newCost;
                cameFrom[next] = cur.p;
                dirAt[next] = d;
                int heuristic = (Math.Abs(end.X - next.X) + Math.Abs(end.Y - next.Y)) * MinStepCost;
                open.Enqueue((next, d), newCost + heuristic);
            }
        }
        return null;
    }

    /// <summary>Cost of stepping onto a tile: cheap on roads and paths, expensive on crops.</summary>
    private static int StepCost(GameLocation loc, Point t)
    {
        var v = new Vector2(t.X, t.Y);
        if (loc.terrainFeatures.TryGetValue(v, out var tf))
        {
            switch (tf)
            {
                case Flooring:
                    return 5; // a path someone built
                case HoeDirt { crop: not null }:
                    return 30; // don't trample the crops
                case HoeDirt:
                    return 14;
            }
        }
        return loc.doesTileHaveProperty(t.X, t.Y, "Type", "Back")?.ToLowerInvariant() switch
        {
            // Ratios cap how far out of the way they'll go for a path (here at most 10/5 = 2x).
            "stone" => 5, // town roads, cobblestone
            "wood" => 5, // bridges, boardwalks, floors
            "dirt" => 6, // trails through the forest/mountain, the farm's bare ground
            "grass" => 10,
            _ => 9,
        };
    }

    private static bool IsPassable(GameLocation loc, Point t, Character who, Dictionary<Point, bool> cache)
    {
        if (cache.TryGetValue(t, out bool ok))
            return ok;
        // Same collision test as the game's own pathfinder.
        ok = !loc.isCollidingPosition(new Rectangle(t.X * 64 + 1, t.Y * 64 + 1, 62, 62), Game1.viewport, who is Farmer, 0, glider: false, who, pathfinding: true);
        cache[t] = ok;
        return ok;
    }

    private static Stack<Point> Reconstruct(Dictionary<Point, Point> cameFrom, Point start, Point end)
    {
        var path = new Stack<Point>();
        for (var p = end; p != start; p = cameFrom[p])
            path.Push(p);
        path.Push(start);
        return path;
    }
}

/// <summary>
/// Follows a precomputed path like <see cref="PathFindController"/>, but without the vanilla habit of
/// stopping for a tick on every tile (which restarts the walk animation and turns off running, so
/// walking looks choppy and the legs never finish a cycle).
/// </summary>
internal sealed class SmoothPathController : PathFindController
{
    private readonly Farmer farmer;
    private readonly bool run;

    public SmoothPathController(Stack<Point> path, GameLocation location, Farmer farmer, Point endPoint, int facing, bool run)
        : base(path, location, farmer, endPoint)
    {
        this.farmer = farmer;
        this.run = run;
        finalFacingDirection = facing;
    }

    protected override void moveCharacter(GameTime time)
    {
        var box = farmer.GetBoundingBox();
        var target = TileRect(pathToEndPoint.Peek());

        // Reached this waypoint: move on to the next one without stopping.
        while (Reached(box, target))
        {
            pathToEndPoint.Pop();
            if (pathToEndPoint.Count == 0)
            {
                farmer.Halt();
                if (finalFacingDirection != -1)
                    farmer.faceDirection(finalFacingDirection);
                endBehaviorFunction?.Invoke(farmer, location);
                return;
            }
            target = TileRect(pathToEndPoint.Peek());
        }

        if (run && !farmer.running)
            farmer.setRunning(true, force: true);

        farmer.movementDirections.Clear();
        if (box.Left < target.Left && box.Right < target.Right)
            farmer.SetMovingRight(true);
        else if (box.Right > target.Right && box.Left > target.Left)
            farmer.SetMovingLeft(true);
        else if (box.Top <= target.Top)
            farmer.SetMovingDown(true);
        else if (box.Bottom >= target.Bottom - 2)
            farmer.SetMovingUp(true);
        farmer.MovePosition(time, Game1.viewport, location);
    }

    private static Rectangle TileRect(Point p)
    {
        var r = new Rectangle(p.X * 64, p.Y * 64, 64, 64);
        r.Inflate(-2, 0);
        return r;
    }

    private static bool Reached(Rectangle box, Rectangle target) =>
        (target.Contains(box) || (box.Width > target.Width && target.Contains(box.Center))) && target.Bottom - box.Bottom >= 2;
}
