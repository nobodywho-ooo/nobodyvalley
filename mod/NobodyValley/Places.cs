namespace NobodyValley;

/// <summary>Curated destinations that villagers can walk to (all reachable via the vanilla NPC route graph).</summary>
internal static class Places
{
    public sealed record Place(string Location, string Description, params string[] Aliases);

    public static readonly Place[] All =
    {
        new("Town", "Pelican Town square", "town", "pelican town", "square"),
        new("Saloon", "the Stardrop Saloon (Gus' bar)", "saloon", "stardrop", "bar", "pub"),
        new("SeedShop", "Pierre's General Store", "pierre", "pierre's", "general store", "shop", "store"),
        new("JojaMart", "JojaMart", "joja"),
        new("CommunityCenter", "the old Community Center", "community center", "cc"),
        new("ArchaeologyHouse", "the museum & library", "museum", "library"),
        new("Hospital", "Harvey's clinic", "clinic", "hospital", "doctor"),
        new("Blacksmith", "Clint's blacksmith", "blacksmith", "clint"),
        new("ManorHouse", "Mayor Lewis' manor", "manor", "lewis", "mayor"),
        new("Beach", "the beach", "beach", "sea", "ocean"),
        new("FishShop", "Willy's fish shop", "fish shop", "willy"),
        new("Mountain", "the mountain lake", "mountain", "mountains", "lake"),
        new("ScienceHouse", "Robin's carpenter shop", "carpenter", "robin", "science house"),
        new("Forest", "Cindersap Forest", "forest", "cindersap", "woods", "river"),
        new("Mine", "the mines (entrance)", "mines", "mine", "the mines", "mining", "cave"),
        new("AnimalShop", "Marnie's ranch", "ranch", "marnie"),
        new("BusStop", "the bus stop", "bus stop", "bus"),
        new("Railroad", "the railroad and bathhouse path", "railroad", "train", "tracks"),
        new("Desert", "the Calico Desert", "calico desert", "calico", "desert", "the desert"),
        new("SandyHouse", "the Oasis (Sandy's shop, in the desert)", "oasis", "sandy's shop", "desert shop"),
        new("Farm", "the shared farm", "farm", "shipping bin", "bin", "field", "crops", "chest"),
        new("FarmHouse", "your farmhouse (the kitchen is upstairs)", "farmhouse", "home house", "the house"),
        new("HaleyHouse", "2 Willow Lane (Haley & Emily's house)", "haley's house", "emily's house", "willow lane"),
        new("SamHouse", "1 Willow Lane (Sam's family house)", "sam's house"),
        new("JoshHouse", "1 River Road (Alex's family house)", "alex's house", "river road"),
        new("Trailer", "Pam & Penny's trailer", "trailer", "pam", "penny's house"),
        new("SebastianRoom", "Sebastian's basement room", "sebastian's room", "basement"),
        new("LeahHouse", "Leah's cottage", "leah's house", "cottage"),
        new("ElliottHouse", "Elliott's beach cabin", "elliott's house", "cabin"),
        new("WizardHouse", "the Wizard's tower", "wizard", "tower"),
    };

    public static Place? Find(string query)
    {
        var q = Normalize(query);
        if (q.Length == 0)
            return null;
        return All.FirstOrDefault(p => Normalize(p.Location) == q || Normalize(p.Description) == q)
            ?? All.FirstOrDefault(p => p.Aliases.Any(a => Normalize(a) == q))
            ?? All
                .SelectMany(p => p.Aliases.Select(a => (p, a: Normalize(a))))
                .Where(x => q.Contains(x.a))
                .OrderByDescending(x => x.a.Length) // "willy's fish shop" -> FishShop, not SeedShop
                .Select(x => x.p)
                .FirstOrDefault();
    }

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Replace("the ", "").Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '\'').ToArray()).Trim();
}
