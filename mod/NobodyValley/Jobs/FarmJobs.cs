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
        if (IsInDesert(Me.currentLocation) && !IsDesertLocation(resolved.Location))
        {
            // Leaving the desert means riding the bus back (its door is a step-on touch action),
            // like a player walking onto the bus.
            yield return RideBusBack(job);
        }
        if (IsDesertLocation(resolved.Location) && !IsInDesert(Me.currentLocation))
        {
            // The desert is only reachable by bus: walk to the bus stop and ride (the vanilla bus,
            // which needs the Community Center's Vault room paid - 42,500g).
            yield return Steps.GoToLocation("BusStop", r);
            if (!r.Ok)
                throw new JobFailed(r.Error ?? "couldn't get to the bus stop");
            yield return RideBus(job);
        }
        else
        {
            yield return Steps.GoToLocation(resolved.Location, r);
            if (!r.Ok)
                throw new JobFailed(r.Error ?? "couldn't get there");
        }

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

    // ====================================================== the bus to the desert

    private static bool IsDesertLocation(string name) => name is "Desert" or "SandyHouse";

    private static bool IsInDesert(GameLocation? loc) => loc is StardewValley.Locations.Desert;

    /// <summary>Take the bus from the bus stop to the desert. The vanilla flow is a ticket question
    /// the player clicks Yes on; we do exactly that - the answerDialogue the click leads to - and
    /// then wait for the ride (the bus drives, then warps to the desert). The ticket costs 500g;
    /// the bus needs the Community Center's Vault room paid and Pam at the bus stop during the day.</summary>
    private static IEnumerator RideBus(Job job)
    {
        if (Me.currentLocation is not StardewValley.Locations.BusStop busStop)
            throw new JobFailed("you're not at the bus stop");
        if (!Game1.MasterPlayer.mailReceived.Contains("ccVault"))
            throw new JobFailed("the desert bus is out of service - the Community Center's Vault room still needs its 42,500g");
        var door = FindTile(busStop, (x, y) => busStop.getTileIndexAt(x, y, "Buildings", "outdoors") == 1057);
        if (door == null)
            throw new JobFailed("couldn't find the bus door");
        var walk = new StepResult();
        yield return Steps.WalkAdjacent(door.Value, walk);
        if (!walk.Ok)
            throw new JobFailed(walk.Error ?? $"can't reach the bus door at {door.Value.X},{door.Value.Y}");
        Me.faceDirection(Steps.Down);
        job.RunThroughEvents = true; // the ride is a scripted sequence: watch it to the end
        // Board like the vanilla 'drive yourself' mode (what the game gives you when Pam has the
        // day off): the ticket is still paid, but no driver is required to be standing at the bus.
        Game1.netWorldState.Value.canDriveYourselfToday.Value = true;
        busStop.lastQuestionKey = "Bus";
        busStop.answerDialogue(new Response("Yes", "Yes")); // pay 500g, board, drive off
        var ride = new StepResult();
        yield return awaitRide(job, ride, () => Me.currentLocation is StardewValley.Locations.Desert);
        if (!ride.Ok)
            throw new JobFailed("the bus didn't run (the ticket is 500g; is the desert unlocked?)");
    }

    /// <summary>Leave the desert: the vanilla return is stepping onto the desert bus door (a
    /// touch action) and answering the ride-back question - equivalently, answer the question
    /// directly and wait for the bus stop.</summary>
    private static IEnumerator RideBusBack(Job job)
    {
        if (Me.currentLocation is not StardewValley.Locations.Desert desert)
            throw new JobFailed("you're not in the desert");
        job.RunThroughEvents = true;
        desert.lastQuestionKey = "DesertBus";
        desert.answerDialogue(new Response("Yes", "Yes")); // board: the bus drives back to the valley
        var ride = new StepResult();
        yield return awaitRide(job, ride, () => Me.currentLocation is StardewValley.Locations.BusStop);
        if (!ride.Ok)
            throw new JobFailed("the bus back didn't run");
    }

    /// <summary>Wait for the bus ride to finish (the drive takes ~8s, then the warp). Reports
    /// success in <paramref name="ride"/> once the player is at the destination location.</summary>
    private static IEnumerator awaitRide(Job job, StepResult ride, Func<bool> arrived)
    {
        for (int i = 0; i < 60 * 75; i++) // up to 75s (the ride is ~10-15s; be generous)
        {
            if (arrived())
            {
                job.Summary = Me.currentLocation is StardewValley.Locations.Desert
                    ? "You took the bus (500g ticket) to the Calico Desert."
                    : "You took the bus back to the bus stop.";
                ride.Ok = true;
                yield break;
            }
            if (i == 60 * 40 && Me.currentLocation is not StardewValley.Locations.Desert
                                   && Me.currentLocation is not StardewValley.Locations.BusStop)
            {
                ride.Fail("the bus never left");
                yield break;
            }
            yield return null;
        }
        ride.Fail("the bus ride didn't finish in time");
    }

    private static Point? FindTile(GameLocation loc, Func<int, int, bool> match)
    {
        var layer = loc.map.GetLayer("Buildings");
        if (layer == null)
            return null;
        for (int x = 0; x < layer.LayerWidth; x++)
        for (int y = 0; y < layer.LayerHeight; y++)
            if (match(x, y))
                return new Point(x, y);
        return null;
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