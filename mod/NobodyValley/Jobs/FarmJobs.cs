using System.Collections;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using SObject = StardewValley.Object;

namespace NobodyValley.Jobs;

/// <summary>Movement and day-cycle conveniences (the affordances: walk/go_to/sleep).</summary>
internal static class FarmJobs
{
    private static ModConfig Config => ModEntry.Instance.Config;
    private static Farmer Me => Game1.player;

    /// <summary>Where the shared field is centred.</summary>
    public static Point FieldCentre()
    {
        var door = Game1.getFarm().GetMainFarmHouseEntry();
        return new Point(door.X + Config.FieldOffsetX, door.Y + Config.FieldOffsetY);
    }

    // ================================================================ go_to

    public static IEnumerator GoTo(Job job, string target)
    {
        var resolved = Resolve(target) ?? throw new JobFailed(
            Game1.characterData.Keys.FirstOrDefault(k => k.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase)) is { } villager
                ? $"you don't know where {villager} is right now - try their home, the town or the saloon"
                : $"unknown place or person '{target}'");
        if (resolved.Person == null && Me.currentLocation.NameOrUniqueName == Navigation.GetLocation(resolved.Location)?.NameOrUniqueName)
        {
            // Agents that can't see what they're looking for tend to go_to where they already are, in a loop.
            job.Summary = $"You're already at {Observer.LocationName(Me.currentLocation)}; nothing changed. Use walk x,y "
                + "or interact directly.";
            yield break;
        }
        var r = new StepResult();
        yield return Steps.GoToLocation(resolved.Location, r);
        if (!r.Ok)
            throw new JobFailed(r.Error ?? "couldn't get there");

        if (resolved.Person != null)
        {
            // People move around; re-target a few times.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var who = resolved.Person();
                if (who == null || who.currentLocation != Me.currentLocation)
                    throw new JobFailed($"{target} isn't here any more");
                if (Steps.Dist(who.TilePoint, Me.TilePoint) <= 2)
                    break;
                var adj = new StepResult();
                yield return Steps.WalkAdjacent(who.TilePoint, adj);
            }
            var person = resolved.Person();
            int dist = person == null ? 99 : Steps.Dist(person.TilePoint, Me.TilePoint);
            if (dist > 2)
                throw new JobFailed($"you're in {Observer.LocationName(Me.currentLocation)} but couldn't reach {target} ({dist} tiles away)");
            Me.faceGeneralDirection(person!.Position, 0, false, false);
            job.Summary = $"You are next to {target} in {Observer.LocationName(Me.currentLocation)}.";
        }
        else
        {
            job.Summary = $"You arrived at {Observer.LocationName(Me.currentLocation)}.";
        }
    }

    private sealed record Target(string Location, Func<Character?>? Person);

    private static Target? Resolve(string target)
    {
        var q = target.Trim();
        if (q.Equals("home", StringComparison.OrdinalIgnoreCase) || q.Equals("bed", StringComparison.OrdinalIgnoreCase)
            || q.Equals("house", StringComparison.OrdinalIgnoreCase))
            return new Target(Utility.getHomeOfFarmer(Me).NameOrUniqueName, null);

        if (q.Equals("field", StringComparison.OrdinalIgnoreCase) || q.Equals("crops", StringComparison.OrdinalIgnoreCase))
            return new Target("Farm", null);

        foreach (var f in Game1.getOnlineFarmers())
        {
            if (f.Name.Equals(q, StringComparison.OrdinalIgnoreCase) && f.UniqueMultiplayerID != Me.UniqueMultiplayerID && f.currentLocation != null)
            {
                long id = f.UniqueMultiplayerID;
                return new Target(f.currentLocation.NameOrUniqueName, () => Game1.getOnlineFarmers().FirstOrDefault(x => x.UniqueMultiplayerID == id));
            }
        }

        var npc = Game1.getCharacterFromName(q);
        if (npc?.currentLocation != null)
            return new Target(npc.currentLocation.NameOrUniqueName, () => Game1.getCharacterFromName(q));

        if (Places.Find(q) is { } place)
            return new Target(place.Location, null);

        if (Navigation.GetLocation(q) is { } loc)
            return new Target(loc.NameOrUniqueName, null);

        return null;
    }

    // ================================================================ sleep

    /// <summary>Walk to bed and lie down; the game's own "go to sleep?" prompt is left for the
    /// agent to answer (like a player clicking Yes).</summary>
    public static IEnumerator Sleep(Job job)
    {
        // The day-end ready check is up: nothing to do, it rolls over on its own.
        if (Game1.activeClickableMenu is StardewValley.Menus.ReadyCheckDialog)
        {
            job.Summary = "The day is ending (ready check) - waiting for the other players; it rolls over on its own.";
            yield break;
        }
        if (Me.isInBed.Value && Me.timeWentToBed.Value != 0)
        {
            job.Summary = "You're already in bed, waiting for the others to sleep.";
            yield break;
        }
        var home = Utility.getHomeOfFarmer(Me);
        var r = new StepResult();
        yield return Steps.GoToLocation(home.NameOrUniqueName, r);
        if (!r.Ok)
            throw new JobFailed(r.Error ?? "couldn't get home");

        // Walking onto the bed pops up the vanilla "Go to sleep for the night?" question; leave it
        // on screen - the agent answers it (answer 1 = Yes), like a player clicking.
        var bed = home.GetPlayerBedSpot();
        yield return Steps.WalkTo(bed, new StepResult());
        for (int i = 0; i < 300; i++)
        {
            if (Game1.activeClickableMenu is StardewValley.Menus.DialogueBox { isQuestion: true } box && Me.currentLocation.lastQuestionKey == "Sleep")
            {
                job.Summary = "The 'go to sleep?' prompt is up - answer it (answer or click Yes to sleep).";
                yield break;
            }
            if (Game1.activeClickableMenu == null && i == 60)
            {
                // No question appeared (e.g. we were already standing on the bed): nudge the bed action.
                Me.currentLocation.checkAction(new xTile.Dimensions.Location(bed.X, bed.Y), Game1.viewport, Me);
            }
            yield return null;
        }
        throw new JobFailed("got to the bed, but the sleep prompt didn't come up");
    }

    // ================================================================ eat

    /// <summary>Eat a food from the inventory (like a player selecting it and eating it).</summary>
    public static IEnumerator Eat(Job job, string? itemName)
    {
        var food = Me.Items.OfType<SObject>()
            .Where(o => o.Edibility > 0 && (itemName == null || o.DisplayName.Contains(itemName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(o => o.sellToStorePrice())
            .FirstOrDefault() ?? throw new JobFailed(itemName == null ? "you have nothing edible" : $"you have no edible '{itemName}'");

        float before = Me.Stamina;
        Steps.Equip(food);
        yield return null;
        Me.mostRecentlyGrabbedItem = food;
        Me.eatHeldObject();
        yield return Wait.Seconds(3);
        job.Summary = $"Ate {food.DisplayName}. Energy {(int)before} -> {(int)Me.Stamina}.";
    }

    // ================================================================ helpers

    public static Point? FindActionTile(GameLocation loc, string actionPrefix, bool exact = false)
    {
        var layer = loc.map.GetLayer("Buildings");
        if (layer == null)
            return null;
        for (int x = 0; x < layer.LayerWidth; x++)
        for (int y = 0; y < layer.LayerHeight; y++)
        {
            var action = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
            if (action != null && (exact
                    ? action.Equals(actionPrefix, StringComparison.OrdinalIgnoreCase) || action.StartsWith(actionPrefix + " ", StringComparison.OrdinalIgnoreCase)
                    : action.StartsWith(actionPrefix, StringComparison.OrdinalIgnoreCase)))
                return new Point(x, y);
        }
        return null;
    }

    private static IEnumerator EnsureOnFarm()
    {
        if (Me.currentLocation is Farm)
            yield break;
        var r = new StepResult();
        yield return Steps.GoToLocation("Farm", r);
        if (!r.Ok)
            throw new JobFailed(r.Error ?? "couldn't get to the farm");
    }
}