using System.Collections;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;
using StardewValley.Internal;
using StardewValley.TokenizableStrings;
using SObject = StardewValley.Object;

namespace NobodyValley.Jobs;

/// <summary>
/// Buildings and animals. The Community Center's Animal/Artisan bundles need a coop and barn,
/// and the farm needs their products every day, so these are high-level jobs rather than menus.
/// </summary>
internal static class HomesteadJobs
{
    private static Farmer Me => Game1.player;
    private static StardewValley.Farm FarmLocation => Game1.getFarm();

    private static bool NameMatches(string full, string wanted) =>
        full.Equals(wanted, StringComparison.OrdinalIgnoreCase)
        || full.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.Equals(wanted, StringComparison.OrdinalIgnoreCase));

    private static (string Id, BuildingData Data)? FindBuilding(string name)
    {
        // An exact key/name wins over a general type (so "Coop" doesn't pick "Big Coop").
        foreach (var (id, data) in Game1.buildingData)
        {
            string display = TokenParser.ParseText(data.Name) ?? id;
            string general = TokenParser.ParseText(data.NameForGeneralType) ?? display;
            if (id.Equals(name, StringComparison.OrdinalIgnoreCase) || display.Equals(name, StringComparison.OrdinalIgnoreCase))
                return (id, data);
            if (NameMatches(general, name) && data.BuildingToUpgrade == null)
                return (id, data);
        }
        return null;
    }

    private static bool HasResources(BuildingData data)
    {
        if (Me.Money < data.BuildCost)
            return false;
        foreach (var material in data.BuildMaterials ?? new List<BuildingMaterial>())
        {
            if (!Me.Items.ContainsId(material.ItemId, material.Amount))
                return false;
        }
        return true;
    }

    private static void ConsumeResources(BuildingData data)
    {
        foreach (var material in data.BuildMaterials ?? new List<BuildingMaterial>())
            Me.Items.ReduceId(material.ItemId, material.Amount);
        Me.Money -= data.BuildCost;
    }

    /// <summary>Build a new building or upgrade an existing one (Coop -> Big Coop -> Deluxe Coop).</summary>
    public static IEnumerator Build(Job job, string name)
    {
        var found = FindBuilding(name);
        if (found == null)
            throw new JobFailed($"unknown building '{name}'. Try Coop, Barn, Big Coop, Big Barn, Deluxe Coop, Deluxe Barn or Silo");
        var (id, data) = found.Value;

        if (data.BuildingToUpgrade is { Length: > 0 } upgradeFrom)
        {
            var building = FarmLocation.buildings.FirstOrDefault(b => b.buildingType.Value == upgradeFrom);
            if (building == null)
                throw new JobFailed($"you need a {upgradeFrom} before you can upgrade it to a {TokenParser.ParseText(data.Name)}");
            if (!HasResources(data))
                throw new JobFailed($"not enough materials/money for {TokenParser.ParseText(data.Name)} (needs {Describe(data)})");

            ConsumeResources(data);
            building.upgradeName.Value = id;
            building.daysUntilUpgrade.Value = Math.Max(data.BuildDays, 1);
            if (data.BuildDays < 1)
                building.FinishConstruction();
            else
                Game1.netWorldState.Value.MarkUnderConstruction("Robin", building);
            job.Summary = $"Started the upgrade to {TokenParser.ParseText(data.Name)}; it will be ready in {Math.Max(data.BuildDays, 1)} day(s).";
            yield break;
        }

        if (!HasResources(data))
            throw new JobFailed($"not enough materials/money for {TokenParser.ParseText(data.Name)} (needs {Describe(data)})");

        var centre = FarmJobs.FieldCentre();
        var candidates = new List<Vector2>();
        for (int dx = -22; dx <= 22; dx++)
        for (int dy = -18; dy <= 18; dy++)
            candidates.Add(new Vector2(centre.X + dx, centre.Y + dy));
        // A second ring, in case the area around the field is full or blocked by the pond.
        for (int x = 2; x < FarmLocation.map.Layers[0].LayerWidth - 2; x += 2)
        for (int y = 2; y < FarmLocation.map.Layers[0].LayerHeight - 2; y += 2)
            candidates.Add(new Vector2(x, y));

        foreach (var tile in candidates.Distinct())
        {
            if (!FarmLocation.buildStructure(id, tile, Me, out var constructed))
                continue;
            ConsumeResources(data);
            job.Summary = $"Built a {TokenParser.ParseText(data.Name)} at {tile.X},{tile.Y}"
                + (constructed.daysOfConstructionLeft.Value > 0 ? $"; it will be ready in {constructed.daysOfConstructionLeft.Value} day(s)." : ".");
            yield break;
        }
        throw new JobFailed($"couldn't find a free {data.Size.X}x{data.Size.Y} spot on the farm for the {TokenParser.ParseText(data.Name)}; clear some debris/grass first");
    }

    private static string Describe(BuildingData data)
    {
        var parts = new List<string>();
        if (data.BuildCost > 0)
            parts.Add($"{data.BuildCost}g");
        foreach (var material in data.BuildMaterials ?? new List<BuildingMaterial>())
        {
            string itemName = ItemRegistry.GetDataOrErrorItem(material.ItemId).DisplayName;
            parts.Add($"{material.Amount} {itemName}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Buy the next tool upgrade from Clint (Axe, Pickaxe, Hoe, Watering Can).</summary>
    public static IEnumerator UpgradeTool(Job job, string name)
    {
        var stock = ShopBuilder.GetShopStock("ClintUpgrade");
        var match = stock.FirstOrDefault(x => x.Key.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match.Key == null)
            match = stock.FirstOrDefault(x => NameMatches(x.Key.DisplayName, name));
        if (match.Key == null)
            throw new JobFailed($"Clint has no upgrade for '{name}' today. He has: {string.Join(", ", stock.Select(x => x.Key.DisplayName).Distinct())}");

        var r = new StepResult();
        yield return Steps.GoToLocation("Blacksmith", r);
        if (!r.Ok)
            throw new JobFailed(r.Error ?? "couldn't get to the Blacksmith");
        if (Me.currentLocation.getCharacterFromName("Clint") == null)
            throw new JobFailed("Clint isn't at the counter (the Blacksmith is open about 9am-5pm)");
        if (Me.toolBeingUpgraded.Value != null)
            throw new JobFailed($"your {Me.toolBeingUpgraded.Value.DisplayName} is already being upgraded");

        int price = match.Key.salePrice();
        if (Me.Money < price)
            throw new JobFailed($"the {match.Key.DisplayName} upgrade costs {price}g but you only have {Me.Money}g");
        Me.Money -= price;
        if (!match.Key.actionWhenPurchased("ClintUpgrade"))
            throw new JobFailed($"couldn't start the {match.Key.DisplayName} upgrade (do you have the previous tool?)");
        job.Summary = $"Started upgrading to a {match.Key.DisplayName} for {price}g; Clint will have it ready in 2 days.";
    }

    /// <summary>Buy an animal at Marnie's and put it in a free coop/barn.</summary>
    public static IEnumerator BuyAnimal(Job job, string type)
    {
        var stock = Utility.getPurchaseAnimalStock(FarmLocation);
        var match = stock.FirstOrDefault(o => o.Name.Equals(type, StringComparison.OrdinalIgnoreCase))
            ?? stock.FirstOrDefault(o => o.DisplayName.Equals(type, StringComparison.OrdinalIgnoreCase))
            ?? stock.FirstOrDefault(o => NameMatches(o.Name, type) || NameMatches(o.DisplayName, type));
        if (match == null)
            throw new JobFailed($"Marnie doesn't sell '{type}' today. She has: {string.Join(", ", stock.Select(o => o.DisplayName).Distinct())}");
        if (match.Type != null)
            throw new JobFailed(string.IsNullOrWhiteSpace(match.Type)
                ? $"you don't have the building Marnie requires for a {match.DisplayName}"
                : match.Type);

        var r = new StepResult();
        yield return Steps.GoToLocation("AnimalShop", r);
        if (!r.Ok)
            throw new JobFailed(r.Error ?? "couldn't get to Marnie's Ranch");
        if (Me.currentLocation.getCharacterFromName("Marnie") == null)
            throw new JobFailed("Marnie isn't at the counter (her shop is open about 9am-5pm)");

        string animalType = match.Name;
        var animal = new FarmAnimal(animalType, Game1.Multiplayer.getNewID(), Me.UniqueMultiplayerID);
        var building = FarmLocation.buildings.FirstOrDefault(b => animal.CanLiveIn(b)
            && b.GetIndoors() is AnimalHouse house && !house.isFull());
        if (building == null)
            throw new JobFailed($"there's no free space in a {animal.displayHouse}; build a Coop for chickens/ducks or a Barn for cows/goats/pigs first");

        int price = match.salePrice();
        if (Me.Money < price)
            throw new JobFailed($"a {match.DisplayName} costs {price}g but you only have {Me.Money}g");

        Me.Money -= price;
        animal.Name = Dialogue.randomName();
        animal.displayName = animal.Name;
        ((AnimalHouse)building.GetIndoors()).adoptAnimal(animal);
        Game1.playSound("purchaseClick");
        job.Summary = $"Bought a {match.DisplayName} named {animal.displayName} for {price}g; it lives in the {TokenParser.ParseText(building.GetData().Name)}.";
    }

    /// <summary>Pet all animals, feed them from hay in the silo, and collect their products.</summary>
    public static IEnumerator CareAnimals(Job job)
    {
        if (Me.currentLocation is not StardewValley.Farm)
        {
            var r = new StepResult();
            yield return Steps.GoToLocation("Farm", r);
            if (!r.Ok)
                throw new JobFailed(r.Error ?? "couldn't get to the farm");
        }

        // Put carried hay into the silo (buy it from Marnie), so the troughs can be filled.
        int hay = Me.Items.CountId("(O)178");
        if (hay > 0 && FarmLocation.GetHayCapacity() > 0)
        {
            int leftover = FarmLocation.tryToAddHay(hay);
            int added = hay - leftover;
            if (added > 0)
                Me.Items.ReduceId("(O)178", added);
        }

        int fed = 0;
        foreach (var building in FarmLocation.buildings)
        {
            if (building.GetIndoors() is AnimalHouse house)
            {
                house.feedAllAnimals();
                fed += house.animals.Pairs.Count();
            }
        }

        int petted = 0, collected = 0;
        foreach (var animal in FarmLocation.getAllFarmAnimals().ToList())
        {
            animal.pet(Me, is_auto_pet: true);
            petted++;
            if (animal.currentProduce.Value == null || !animal.isAdult())
                continue;
            var produced = ItemRegistry.Create<SObject>("(O)" + animal.currentProduce.Value);
            produced.Quality = animal.produceQuality.Value;
            if (!Me.addItemToInventoryBool(produced))
                throw new JobFailed($"collected {collected} products but your inventory is full; ship/store something first");
            animal.currentProduce.Value = null;
            animal.ReloadTextureIfNeeded();
            collected++;
        }

        job.Summary = FarmLocation.getAllFarmAnimals().Count == 0
            ? "You don't have any animals yet - build a coop/barn and buy some from Marnie."
            : $"Petted {petted} animal(s), fed the houses, and collected {collected} product(s).";
    }
}
