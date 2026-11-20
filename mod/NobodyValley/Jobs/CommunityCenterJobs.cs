using System.Collections;
using Newtonsoft.Json.Linq;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace NobodyValley.Jobs;

/// <summary>
/// Reading the Junimo bundles, and donating the items for them. The agents' long-term goal is to
/// restore the Community Center, so this is the one place that knows what every bundle still needs.
/// </summary>
internal static class CommunityCenterJobs
{
    private static Farmer Me => Game1.player;
    private static CommunityCenter Cc => Game1.RequireLocation<CommunityCenter>("CommunityCenter");

    public sealed record Ingredient(int Index, string? Id, int? Category, int Stack, int Quality, bool Done);

    public sealed record BundleInfo(string Room, int Index, string Name, string Reward, int Slots, List<Ingredient> Ingredients)
    {
        public bool Complete => Ingredients.Count(i => i.Done) >= Slots;
        public bool IsVault => Room == "Vault";
    }

    public static bool Unlocked =>
        Game1.MasterPlayer.mailReceived.Contains("ccDoorUnlock") || Game1.MasterPlayer.hasCompletedCommunityCenter();

    public static bool CanRead => Me.hasOrWillReceiveMail("canReadJunimoText");

    /// <summary>All standard/remixed bundles from the save, with their actual completion flags.</summary>
    public static List<BundleInfo> Bundles()
    {
        var done = Cc.bundlesDict();
        var result = new List<BundleInfo>();
        foreach (var (key, raw) in Game1.netWorldState.Value.BundleData)
        {
            var keyParts = key.Split('/');
            var parts = raw.Split('/');
            if (keyParts.Length != 2 || !int.TryParse(keyParts[1], out int index) || parts.Length < 3)
                continue;
            if (keyParts[0] == "Abandoned Joja Mart")
                continue; // the optional Missing Bundle, only after the Community Center is finished
            var flags = done.GetValueOrDefault(index) ?? Array.Empty<bool>();
            var ingredients = new List<Ingredient>();
            var args = ArgUtility.SplitBySpace(parts[2]);
            for (int i = 0; i + 2 < args.Length; i += 3)
            {
                string idOrCategory = args[i];
                int? category = int.TryParse(idOrCategory, out int c) && c < 0 ? c : null;
                ingredients.Add(new Ingredient(
                    i / 3,
                    category.HasValue ? null : idOrCategory,
                    category,
                    int.TryParse(args[i + 1], out int stack) ? stack : 1,
                    int.TryParse(args[i + 2], out int quality) ? quality : 0,
                    i / 3 < flags.Length && flags[i / 3]));
            }
            int slots = parts.Length > 4 && int.TryParse(parts[4], out int n) ? n : ingredients.Count;
            result.Add(new BundleInfo(
                keyParts[0],
                index,
                parts[0],
                parts[1],
                Math.Max(1, slots),
                ingredients));
        }
        return result;
    }

    public static string ItemName(Ingredient ingredient)
    {
        if (ingredient.Category is int category)
        {
            return category switch
            {
                -2 => "Mineral",
                -75 => "Foraged item",
                -4 => "Fish",
                -5 => "Egg",
                -6 => "Milk",
                _ => "item",
            };
        }
        try
        {
            return ItemRegistry.GetDataOrErrorItem(ingredient.Id!).DisplayName;
        }
        catch
        {
            return ingredient.Id ?? "item";
        }
    }

    public static string Describe(Ingredient ingredient)
    {
        string quality = ingredient.Quality >= 2 ? "gold " : ingredient.Quality == 1 ? "silver " : "";
        return $"{ingredient.Stack} {quality}{ItemName(ingredient)}";
    }

    private static Ingredient? BestNeed(Item item, IEnumerable<BundleInfo> bundles) =>
        bundles.Where(b => !b.Complete && !b.IsVault)
            .SelectMany(b => b.Ingredients)
            .Where(i => !i.Done && Matches(item, i))
            .OrderBy(i => i.Stack)
            .Cast<Ingredient?>()
            .FirstOrDefault();

    private static string HeldText(IEnumerable<Item> items, IEnumerable<BundleInfo> bundles)
    {
        var result = new List<string>();
        foreach (var group in items.Where(i => i != null).GroupBy(i => i.DisplayName))
        {
            var need = BestNeed(group.First(), bundles);
            int count = group.Sum(i => i.Stack);
            result.Add($"{group.Key} x{count}" + (need is { } n ? $" (need {n.Stack})" : ""));
        }
        return string.Join(", ", result);
    }

    /// <summary>Match an agent's item name to a bundle item, without "Wood" matching "Woodskip".</summary>
    private static bool NameMatches(string fullName, string wanted)
    {
        if (fullName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            return true;
        return fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Matches(Item? item, Ingredient ingredient)
    {
        if (item == null || ingredient.Done || item.Quality < ingredient.Quality)
            return false;
        if (ingredient.Category is int category)
        {
            // Dinosaur Eggs count as eggs for the Animal bundle in the game's own check.
            if (item.QualifiedItemId == "(O)107" && category == SObject.EggCategory)
                return true;
            return item.Category == category;
        }
        return ItemRegistry.HasItemId(item, ingredient.Id!);
    }

    /// <summary>Whether the item is still needed by an incomplete Community Center bundle.</summary>
    public static bool IsNeeded(Item? item)
    {
        if (item == null || !Unlocked)
            return false;
        foreach (var bundle in Bundles().Where(b => !b.Complete && !b.IsVault))
        {
            foreach (var ingredient in bundle.Ingredients.Where(i => !i.Done))
            {
                if (Matches(item, ingredient))
                    return true;
            }
        }
        return false;
    }

    /// <summary>The Community Center section of the observation.</summary>
    public static JObject Observe()
    {
        var bundles = Bundles();
        var incomplete = bundles.Where(b => !b.Complete).ToList();
        var rooms = incomplete
            .GroupBy(b => b.Room)
            .Select(g => new JObject
            {
                ["room"] = g.Key,
                ["needs"] = new JArray(g.Select(b => b.IsVault
                    ? $"{b.Name}: {b.Ingredients.Last().Stack:n0}g"
                    : $"{b.Name}: " + string.Join(", ", b.Ingredients.Where(i => !i.Done).Select(Describe)))),
            });
        var held = HeldText(Me.Items.Where(i => i != null && IsNeeded(i)), bundles);
        var chestItems = Game1.getFarm().objects.Values
            .OfType<StardewValley.Objects.Chest>()
            .Where(c => c.playerChest.Value)
            .SelectMany(c => c.GetItemsForPlayer())
            .Where(i => i != null && IsNeeded(i))
            .ToList();
        var inChests = HeldText(chestItems, bundles);

        string? hint = !Unlocked
            ? "The Community Center has not been opened yet."
            : !CanRead
                ? "The scrolls are still unread, but the list above is accurate - donate the items anyway. Reading the scroll and visiting the Wizard is optional."
                : null;

        return new JObject
        {
            ["done"] = $"{bundles.Count - incomplete.Count}/{bundles.Count} bundles, "
                + $"{bundles.GroupBy(b => b.Room).Count(g => g.All(b => b.Complete))}/{bundles.Select(b => b.Room).Distinct().Count()} rooms",
            ["rooms"] = new JArray(rooms),
            ["in_inventory"] = held,
            ["in_chests"] = inChests,
            ["hint"] = hint,
        };
    }

    /// <summary>
    /// Walk to the Community Center and put every matching item from the inventory into the bundles
    /// that need it. This uses the same Bundle deposit code as the Junimo note menu (but without
    /// making the agent click through the menu), so rewards and room completion happen normally.
    /// </summary>
}
