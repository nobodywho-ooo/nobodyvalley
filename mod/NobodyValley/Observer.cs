using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace NobodyValley;

/// <summary>Builds the JSON snapshot that agents base their decisions on.</summary>
internal static class Observer
{
    public static JObject Observe(Jobs.Job? currentJob)
    {
        var me = Game1.player;
        var farm = Game1.getFarm();

        return new JObject
        {
            ["me"] = new JObject
            {
                ["name"] = me.Name,
                ["location"] = LocationName(me.currentLocation, onFarm: true),
                ["location_id"] = me.currentLocation?.NameOrUniqueName,
                ["tile"] = new JArray(me.TilePoint.X, me.TilePoint.Y),
                ["energy"] = (int)me.Stamina,
                ["max_energy"] = me.MaxStamina,
                ["health"] = me.health,
                ["money"] = me.Money,
                ["holding"] = me.CurrentItem?.DisplayName,
                ["busy"] = currentJob?.Activity,
                ["inventory_size"] = me.MaxItems,
                ["inventory"] = new JArray(me.Items.Where(i => i != null).Select(i => i.Stack > 1 ? $"{i.DisplayName} x{i.Stack}" : i.DisplayName)),
            },
            ["world"] = new JObject
            {
                ["time"] = FormatTime(Game1.timeOfDay),
                ["time_raw"] = Game1.timeOfDay,
                ["date"] = $"{Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth)} {Utility.getSeasonNameFromNumber(Game1.seasonIndex)} {Game1.dayOfMonth}, year {Game1.year}",
                ["weather"] = Weather(),
                ["festival_today"] = Utility.isFestivalDay() ? "yes - the town is busy with a festival" : null,
            },
            ["farm"] = FarmSummary(farm),
            ["others"] = new JArray(Game1.getOnlineFarmers()
                .Where(f => f.UniqueMultiplayerID != me.UniqueMultiplayerID)
                .Select(f => new JObject
                {
                    ["name"] = f.Name,
                    ["location"] = LocationName(f.currentLocation, at: f.TilePoint),
                    ["distance"] = f.currentLocation == me.currentLocation ? (int)Vector2.Distance(f.Tile, me.Tile) : null,
                    ["doing"] = PlayerState.GetActivity(f) ?? "nothing in particular",
                })),
            ["nearby_villagers"] = new JArray((me.currentLocation?.characters ?? Enumerable.Empty<NPC>())
                .Where(n => n.IsVillager && !n.IsInvisible)
                .OrderBy(n => Vector2.Distance(n.Tile, me.Tile))
                .Take(8)
                .Select(n => $"{n.displayName} ({(int)Vector2.Distance(n.Tile, me.Tile)} tiles away)")),
            ["forage_here"] = Jobs.MoreJobs.ForageCount(me.currentLocation),
            ["map"] = MapView.Render(me),
            ["mine_level"] = me.currentLocation is StardewValley.Locations.MineShaft shaft ? shaft.mineLevel : null,
            ["machines"] = new JArray(Jobs.MoreJobs.Machines()),
            ["menu"] = OpenMenu(),
        };
    }

    /// <summary>What's blocking the game right now (an open chest/inventory menu or the day-end ready
    /// check), or null if nothing is.</summary>
    public static string? OpenMenu()
    {
        if (Game1.activeClickableMenu == null)
            return null;
        // The day-end/festival ready check. Kept in the status so agents can see it's up even after
        // the one-off screen message grows stale, and wait for the new day instead of re-planning.
        if (Game1.activeClickableMenu is ReadyCheckDialog readyCheck)
            return readyCheck.checkName == "sleep"
                ? "The day is ending: waiting for the other players (ready check)."
                : $"A ready check is up ({readyCheck.checkName}): waiting for the other players.";
        if (Game1.activeClickableMenu is not ItemGrabMenu menu)
            return Game1.activeClickableMenu is GameMenu ? "Your backpack menu is open and blocks you - close it with `answer`." : null;

        var items = menu.ItemsToGrabMenu?.actualInventory?.Where(i => i != null)
            .Select(i => i!.Stack > 1 ? $"{i.DisplayName} x{i.Stack}" : i.DisplayName)
            .ToList();
        string contents = items is not { Count: > 0 } ? "nothing" : string.Join(", ", items);

        if (menu.sourceItem is Chest chest)
        {
            string what = chest.fridge.Value ? "the fridge" : "a chest";
            return $"A menu is open for {what} next to you. It contains: {contents}. "
                + "Use take to take things out, store to put things in, or answer to close it.";
        }
        // The "items that didn't fit" menu (e.g. a gift/overflow with a full backpack).
        return $"A menu is open showing items that didn't fit in your backpack: {contents}. "
            + $"Use take to take them, store to make room, or answer to close it (anything left is lost).";
    }

    private static JObject FarmSummary(Farm farm)
    {
        int tilledEmpty = 0, unwatered = 0, dead = 0;
        var fieldTiles = new List<Vector2>();
        var crops = new Dictionary<string, (int total, int ready)>();
        foreach (var pair in farm.terrainFeatures.Pairs)
        {
            if (pair.Value is not HoeDirt dirt)
                continue;
            if (dirt.crop == null)
            {
                tilledEmpty++;
                continue;
            }
            if (dirt.crop.dead.Value)
            {
                dead++;
                continue;
            }
            string name = CropName(dirt.crop);
            var c = crops.GetValueOrDefault(name);
            crops[name] = (c.total + 1, c.ready + (dirt.readyForHarvest() ? 1 : 0));
            fieldTiles.Add(pair.Key);
            if (dirt.needsWatering() && !dirt.isWatered())
                unwatered++;
        }

        var centre = Jobs.FarmJobs.FieldCentre();
        int debris = farm.objects.Pairs.Count(p =>
            (p.Value.IsWeeds() || p.Value.IsBreakableStone() || p.Value.IsTwig())
            && Math.Abs(p.Key.X - centre.X) <= 12 && Math.Abs(p.Key.Y - centre.Y) <= 12);

        return new JObject
        {
            // "14 Parsnip (1 ready to harvest)" got read as "14 parsnips are ready"; spell out both numbers.
            ["crops"] = new JArray(crops.Select(c => $"{c.Key} (" + (c.Value.ready == 0 ? $"{c.Value.total} growing"
                : c.Value.ready == c.Value.total ? $"{c.Value.ready} ripe" : $"{c.Value.ready} ripe, {c.Value.total - c.Value.ready} growing") + ")")),
            // Where the crops are (the map view only covers the area around the farmer).
            ["crops_around"] = fieldTiles.Count == 0 ? null
                : $"{(int)fieldTiles.Average(t => t.X)},{(int)fieldTiles.Average(t => t.Y)}",
            ["crops_needing_water"] = unwatered,
            ["empty_tilled_soil"] = tilledEmpty,
            ["dead_crops"] = dead,
            ["debris_near_field"] = debris,
            // Grass is what you scythe for hay; the silo holds it for the animals (240 per silo).
            ["grass"] = farm.terrainFeatures.Values.Count(tf => tf is Grass),
            ["hay"] = farm.GetHayCapacity() == 0
                ? "0/0 (no silo - build one to store hay)"
                : $"{farm.piecesOfHay.Value}/{farm.GetHayCapacity()}",
            ["raining"] = farm.IsRainingHere(),
            ["buildings"] = new JArray(farm.buildings.Select(b =>
                (StardewValley.TokenizableStrings.TokenParser.ParseText(b.GetData()?.Name) ?? b.buildingType.Value)
                + (b.isUnderConstruction() ? " (building)" : ""))),
            ["animals"] = new JArray(farm.getAllFarmAnimals()
                .GroupBy(a => a.displayType)
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key)),
            ["fruit_trees"] = farm.terrainFeatures.Values.OfType<FruitTree>().Any()
                ? $"{farm.terrainFeatures.Values.OfType<FruitTree>().Count()} trees, "
                    + $"{farm.terrainFeatures.Values.OfType<FruitTree>().Sum(t => t.fruit.Count)} ripe fruit"
                : null,
        };
    }

    /// <summary>
    /// A location's name as the agents should see it. Interiors without a name of their own (houses, cabins,
    /// the greenhouse, ...) report their parent's DisplayName, which made agents in their cabin think they
    /// were out on the farm.
    /// </summary>
    /// <param name="onFarm">Add "(on the farm)" to buildings on the farm.</param>
    /// <param name="at">The tile to name the relative position from (defaults to the local player).</param>
    public static string LocationName(GameLocation? loc, bool onFarm = false, Point? at = null)
    {
        if (loc == null)
            return "?";
        string suffix = onFarm && loc.GetParentLocation() is Farm ? " (a building on the farm)" : "";
        if (loc is StardewValley.Locations.FarmHouse house) // also cabins
        {
            var owner = house.owner;
            if (owner != null && owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
                return "your house" + suffix;
            return (owner != null && !string.IsNullOrEmpty(owner.Name) ? $"{owner.Name}'s house" : "a farmhand cabin") + suffix;
        }
        var parent = loc.GetParentLocation();
        if (parent != null && loc.DisplayName == parent.DisplayName)
            return loc.Name + suffix; // e.g. Greenhouse, Coop, Barn
        if (loc is Farm)
        {
            // The farm's save name ("Nowhere Farm") is confusing noise; call it "Farm", and say which
            // side of it the tile is on unless it's the middle ("north-east side of the farm" / "Farm").
            return RelativePart(loc, at ?? Game1.player.TilePoint) is { } part ? $"{part} side of the farm" : "Farm";
        }
        return loc.DisplayName;
    }

    /// <summary>Which third of a location a tile sits in: "north-east", "south", "west", ... (null = the middle).</summary>
    private static string? RelativePart(GameLocation loc, Point tile)
    {
        if (loc.map?.Layers == null || loc.map.Layers.Count == 0)
            return null;
        int w = loc.map.Layers[0].LayerWidth, h = loc.map.Layers[0].LayerHeight;
        string ns = tile.Y < h / 3 ? "north" : tile.Y >= 2 * h / 3 ? "south" : "";
        string ew = tile.X < w / 3 ? "west" : tile.X >= 2 * w / 3 ? "east" : "";
        if (ns == "" && ew == "")
            return null; // the middle
        return string.Join("-", new[] { ns, ew }.Where(s => s != ""));
    }

    /// <summary>What a crop gives. Wild spring onions/ginger are "forage crops", without an indexOfHarvest.</summary>
    public static string CropName(Crop crop)
    {
        if (crop.forageCrop.Value)
            return ItemRegistry.GetDataOrErrorItem(crop.whichForageCrop.Value == Crop.forageCrop_gingerID ? "(O)829" : "(O)399").DisplayName;
        return ItemRegistry.GetDataOrErrorItem(crop.indexOfHarvest.Value).DisplayName;
    }

    private static string Weather()
    {
        if (Game1.isLightning) return "stormy";
        if (Game1.isRaining) return "rainy";
        if (Game1.isSnowing) return "snowy";
        if (Game1.isDebrisWeather) return "windy";
        return "sunny";
    }

    public static string FormatTime(int t)
    {
        int h = t / 100 % 24, m = t % 100;
        string ampm = h < 12 ? "AM" : "PM";
        int h12 = h % 12 == 0 ? 12 : h % 12;
        return $"{h12}:{m:00} {ampm}";
    }
}
