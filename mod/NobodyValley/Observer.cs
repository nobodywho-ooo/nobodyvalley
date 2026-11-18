using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewValley;
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
                    ["location"] = LocationName(f.currentLocation),
                    ["distance"] = f.currentLocation == me.currentLocation ? (int)Vector2.Distance(f.Tile, me.Tile) : null,
                    ["doing"] = PlayerState.GetActivity(f) ?? "nothing in particular",
                })),
            ["nearby_villagers"] = new JArray((me.currentLocation?.characters ?? Enumerable.Empty<NPC>())
                .Where(n => n.IsVillager && !n.IsInvisible)
                .OrderBy(n => Vector2.Distance(n.Tile, me.Tile))
                .Take(8)
                .Select(n => $"{n.displayName} ({(int)Vector2.Distance(n.Tile, me.Tile)} tiles away)")),
            ["seeds_for_sale"] = new JArray(Jobs.FarmJobs.SeedsForSale()),
            ["forage_here"] = Jobs.MoreJobs.ForageCount(me.currentLocation),
            ["map"] = MapView.Render(me),
            ["mine_level"] = me.currentLocation is StardewValley.Locations.MineShaft shaft ? shaft.mineLevel : null,
            ["chests"] = new JArray(Jobs.MoreJobs.FarmChestContents()),
            ["recipes"] = new JArray(Jobs.MoreJobs.Recipes()),
            ["machines"] = new JArray(Jobs.MoreJobs.Machines()),
            ["community_center"] = Jobs.CommunityCenterJobs.Observe(),
        };
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
    /// the greenhouse, ...) report their parent's DisplayName ("Nowhere Farm"), which made agents in their
    /// cabin think they were out on the farm.
    /// </summary>
    /// <param name="onFarm">Add "(on the farm)" to buildings on the farm.</param>
    public static string LocationName(GameLocation? loc, bool onFarm = false)
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
        return loc.DisplayName;
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
