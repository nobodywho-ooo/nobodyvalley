using System.Text;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace NobodyValley;

/// <summary>
/// An ASCII picture of the area around the farmer, plus nearest-first lists of interesting tiles.
/// (Small models are bad at counting columns in a grid, so the lists carry the exact coordinates.)
/// </summary>
internal static class MapView
{
    private const int HalfWidth = 12, HalfHeight = 8; // 25 x 17 tiles
    private const int ListLength = 5;

    private static readonly HashSet<string> OreIds = new() { "751", "290", "764", "765", "95", "843", "844", "849", "850", "VolcanoGoldNode", "VolcanoCoalNode0", "VolcanoCoalNode1" };

    public const string Legend =
        "@ you, A-Z other farmers, & villager, ! monster, T tree, t sapling, X big stump/boulder, s stone, O ore, " +
        "w weed, k twig, f forage, g grass, , tilled, ; tilled+watered, p crop (needs water), P crop (watered), * ripe crop, " +
        "~ water, = path, D door, L ladder down, U ladder up, B shipping bin, C chest, o object, # wall/blocked, . ground";

    public static JObject Render(Farmer me)
    {
        var loc = me.currentLocation;
        var c = me.TilePoint;
        int w = loc.map.Layers[0].LayerWidth, h = loc.map.Layers[0].LayerHeight;
        int x0 = Math.Clamp(c.X - HalfWidth, 0, Math.Max(0, w - (2 * HalfWidth + 1)));
        int y0 = Math.Clamp(c.Y - HalfHeight, 0, Math.Max(0, h - (2 * HalfHeight + 1)));
        int x1 = Math.Min(w - 1, x0 + 2 * HalfWidth), y1 = Math.Min(h - 1, y0 + 2 * HalfHeight);

        var people = new Dictionary<Point, (char glyph, string name)>();
        foreach (var f in loc.farmers)
            if (f != me)
                people[f.TilePoint] = (char.ToUpperInvariant(f.Name.FirstOrDefault('?')), f.Name);
        foreach (var npc in loc.characters)
            people[npc.TilePoint] = npc is Monster m ? ('!', m.displayName) : ('&', npc.displayName);
        people[c] = ('@', "you");

        var doors = DoorTiles(loc);
        var lists = new Dictionary<string, List<Point>>();
        void Note(string list, Point p) => (lists.TryGetValue(list, out var l) ? l : lists[list] = new List<Point>()).Add(p);

        var sb = new StringBuilder();
        sb.Append("    ");
        for (int x = x0; x <= x1; x++)
            sb.Append(x % 5 == 0 ? (char)('0' + x / 10 % 10) : ' ');
        sb.Append('\n').Append("    ");
        for (int x = x0; x <= x1; x++)
            sb.Append((char)('0' + x % 10));
        sb.Append('\n');
        for (int y = y0; y <= y1; y++)
        {
            sb.Append(y.ToString().PadLeft(3)).Append(' ');
            for (int x = x0; x <= x1; x++)
            {
                var p = new Point(x, y);
                char g = people.TryGetValue(p, out var who) ? who.glyph : Glyph(loc, p, doors, Note);
                sb.Append(g);
            }
            sb.Append('\n');
        }
        // Objects/terrain just outside the drawn 25x17 window (a chest by the door, machines, debris):
        // without this the nearest-first lists below only cover what's already on the map grid.
        ScanBeyondView(loc, c, x0, y0, x1, y1, doors, Note);

        var nearby = new JObject();
        foreach (var (name, points) in lists)
        {
            var nearest = points.Distinct().OrderBy(p => Math.Abs(p.X - c.X) + Math.Abs(p.Y - c.Y)).Take(ListLength);
            nearby[name] = string.Join(" ", nearest.Select(p => $"{p.X},{p.Y}"));
        }
        var peopleList = people.Where(kv => kv.Value.glyph != '@'
                && kv.Key.X >= x0 && kv.Key.X <= x1 && kv.Key.Y >= y0 && kv.Key.Y <= y1)
            .OrderBy(kv => Math.Abs(kv.Key.X - c.X) + Math.Abs(kv.Key.Y - c.Y)).Take(6)
            .Select(kv => $"{kv.Value.name} {kv.Key.X},{kv.Key.Y}");
        if (peopleList.Any())
            nearby["people"] = string.Join(", ", peopleList);
        var exits = Exits(loc).Where(e => e.at.X >= x0 && e.at.X <= x1 && e.at.Y >= y0 && e.at.Y <= y1)
            .OrderBy(e => Math.Abs(e.at.X - c.X) + Math.Abs(e.at.Y - c.Y)).Take(6)
            .Select(e => $"{e.name} {e.at.X},{e.at.Y}");
        if (exits.Any())
            nearby["exits"] = string.Join(", ", exits);

        return new JObject
        {
            ["you"] = $"{c.X},{c.Y}",
            ["width"] = w,
            ["height"] = h,
            ["view"] = new JArray(x0, y0, x1, y1),
            ["grid"] = sb.ToString().TrimEnd('\n'),
            ["nearby"] = nearby,
        };
    }

    private static char Glyph(GameLocation loc, Point p, Dictionary<Point, string> doors, Action<string, Point> note)
    {
        var v = new Vector2(p.X, p.Y);
        if (doors.ContainsKey(p))
            return 'D';
        if (loc is MineShaft or Mine)
        {
            int idx = loc.getTileIndexAt(p, "Buildings");
            if (idx is 173 or 174)
            {
                note("ladder down", p);
                return 'L';
            }
            if (idx == 115)
                return 'U';
        }
        foreach (var clump in loc.resourceClumps)
        {
            if (clump.occupiesTile(p.X, p.Y))
            {
                note("big stumps/boulders (need upgraded tools)", p);
                return 'X';
            }
        }
        if (loc.objects.TryGetValue(v, out var obj))
        {
            if (obj.IsWeeds()) { note("weeds", p); return 'w'; }
            if (obj.IsTwig()) { note("twigs", p); return 'k'; }
            if (obj.IsBreakableStone())
            {
                if (OreIds.Contains(obj.ItemId)) { note("ore", p); return 'O'; }
                note("stones", p);
                return 's';
            }
            if (obj.IsSpawnedObject && obj.isForage()) { note("forage", p); return 'f'; }
            if (obj is Chest { playerChest.Value: true }) { note("chests", p); return 'C'; }
            return 'o';
        }
        if (loc.terrainFeatures.TryGetValue(v, out var tf))
        {
            switch (tf)
            {
                case Tree tree:
                    if (tree.growthStage.Value >= Tree.treeStage) { note("trees", p); return 'T'; }
                    return 't';
                case FruitTree:
                    return 'T';
                case HoeDirt dirt:
                    if (Jobs.MoreJobs.IsWildOnion(dirt)) { note("forage", p); return 'f'; }
                    if (dirt.crop == null)
                    {
                        note("tilled soil ready for seeds", p);
                        return dirt.isWatered() ? ';' : ',';
                    }
                    if (dirt.readyForHarvest()) { note("ripe crops", p); return '*'; }
                    if (dirt.needsWatering() && !dirt.isWatered()) { note("crops needing water", p); return 'p'; }
                    return 'P';
                case Grass:
                    note("grass", p);
                    return 'g';
                case Flooring:
                    return '=';
            }
        }
        if (loc.getBuildingAt(v) is { } building)
        {
            if (building is ShippingBin) { note("shipping bin", p); return 'B'; }
            return '#';
        }
        if (loc.isWaterTile(p.X, p.Y))
            return '~';
        if (!Jobs.Steps.IsStandable(loc, p))
            return '#';
        return loc.doesTileHaveProperty(p.X, p.Y, "Type", "Back")?.ToLowerInvariant() == "stone" ? '=' : '.';
    }

    /// <summary>Fill the nearest-first lists with things outside the drawn area too (within ~30 tiles).</summary>
    private static void ScanBeyondView(GameLocation loc, Point c, int x0, int y0, int x1, int y1, Dictionary<Point, string> doors, Action<string, Point> note)
    {
        bool InView(Point p) => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;
        bool Near(Vector2 t) => Math.Abs(t.X - c.X) + Math.Abs(t.Y - c.Y) <= 30;
        foreach (var pair in loc.objects.Pairs)
        {
            var p = Jobs.Steps.ToPoint(pair.Key);
            if (!InView(p) && Near(pair.Key))
                Glyph(loc, p, doors, note);
        }
        foreach (var pair in loc.terrainFeatures.Pairs)
        {
            var p = Jobs.Steps.ToPoint(pair.Key);
            // Crops anywhere on this map, so an agent at its cabin door still finds the field.
            // Grass too: agents scythe it for hay (a patch is several tiles wide).
            if (!InView(p) && (pair.Value is HoeDirt || pair.Value is Grass || (Near(pair.Key) && pair.Value is Tree)))
                Glyph(loc, p, doors, note);
        }
    }

    private static Dictionary<Point, string> DoorTiles(GameLocation loc)
    {
        var doors = new Dictionary<Point, string>();
        foreach (var pair in loc.doors.Pairs)
            doors[pair.Key] = pair.Value;
        foreach (var b in loc.buildings)
            if (b.humanDoor.X >= 0 && b.GetIndoors() is { } indoors)
                doors[b.getPointForHumanDoor()] = indoors.NameOrUniqueName;
        return doors;
    }

    /// <summary>Where you can leave this map, and to where.</summary>
    private static IEnumerable<(string name, Point at)> Exits(GameLocation loc)
    {
        var seen = new HashSet<string>();
        foreach (var w in loc.warps)
        {
            var target = Navigation.GetLocation(w.TargetName);
            string name = target != null ? Observer.LocationName(target) : w.TargetName;
            if (seen.Add(name))
                yield return (name, new Point(Math.Clamp(w.X, 0, loc.map.Layers[0].LayerWidth - 1), Math.Clamp(w.Y, 0, loc.map.Layers[0].LayerHeight - 1)));
        }
        foreach (var (p, name) in DoorTiles(loc))
        {
            var display = Navigation.GetLocation(name) is { } target ? Observer.LocationName(target) : name;
            if (seen.Add(display))
                yield return (display, p);
        }
    }
}
