using System.Collections;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using NobodyValley.Jobs;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Internal;
using StardewValley.Menus;

namespace NobodyValley;

/// <summary>
/// Lets an external agent control the local farmer of this game instance.
/// Run one game instance per agent (joined over LAN co-op); each instance's mod listens on its own port.
/// The command surface is deliberately thin: the mod moves the farmer and clicks things (tiles,
/// tools, menu buttons); the agents read the screen (the observation) and decide, like a player.
/// </summary>
internal sealed class ModEntry : Mod
{
    internal static ModEntry Instance = null!;
    internal ModConfig Config = null!;

    private BridgeServer server = null!;
    private ScreenMessages screenMessages = null!;
    private JobRunner jobs = null!;
    private MenuJanitor janitor = null!;
    private AutoStart autoStart = null!;

    private static readonly HashSet<string> Emotes = new(StringComparer.OrdinalIgnoreCase)
    {
        "happy", "sad", "heart", "exclamation", "note", "sleep", "question", "angry", "yes", "no",
        "laugh", "surprised", "hi", "music", "uh", "x", "pause", "blush", "taunt", "sick",
    };

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        Config = helper.ReadConfig<ModConfig>();
        MenuAgent.Reflection = helper.Reflection;

        server = new BridgeServer(Monitor);
        // NOBODYVALLEY_PORT pins the port (e.g. for a test instance the agents shouldn't pick up).
        if (int.TryParse(Environment.GetEnvironmentVariable("NOBODYVALLEY_PORT"), out int pinned))
            server.Start(pinned, 1);
        else
            server.Start(Config.BasePort, Config.PortRange);
        screenMessages = new ScreenMessages(server, Monitor);
        jobs = new JobRunner(Monitor, OnJobFinished);
        janitor = new MenuJanitor(helper, Monitor, () => (int)(Config.MenuDisplaySeconds * 60), () => screenMessages.Holding);
        autoStart = new AutoStart(helper, Monitor);

        new Harmony(ModManifest.UniqueID).PatchAll(typeof(ModEntry).Assembly);

        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        // The game re-applies its saved window size while starting up, so place the window a few times.
        helper.Events.GameLoop.GameLaunched += (_, _) => WindowPlacement.Apply(Monitor);
        helper.Events.GameLoop.UpdateTicked += (_, e) =>
        {
            if (e.Ticks is 120 or 600)
                WindowPlacement.Apply(Monitor);
        };
        helper.Events.GameLoop.SaveLoaded += (_, _) =>
        {
            Game1.options.pauseWhenOutOfFocus = false;
            ApplyUiScale();
            WindowPlacement.Apply(Monitor);
            WindowPlacement.SetTitle(Game1.player.Name);
            Monitor.Log($"In game as {Game1.player.Name} ({(Context.IsMainPlayer ? "host" : "farmhand")}), agent port {server.Port}.", LogLevel.Info);
            ApplySoundMute();
            server.Broadcast("world_ready", Hello());
        };
        helper.Events.GameLoop.DayStarted += (_, _) =>
        {
            Game1.options.pauseWhenOutOfFocus = false;
            ApplyUiScale();
            // The old menu janitor skipped cutscenes, so saves it ran may never have been granted
            // the Junimo-scroll mail (vanilla grants it inside the CC cutscene). Repair that state:
            // the player would have watched it. New saves get it from the playing cutscene itself.
            if (Game1.MasterPlayer.mailReceived.Contains("ccDoorUnlock") && !Game1.player.hasOrWillReceiveMail("canReadJunimoText"))
            {
                Game1.player.mailReceived.Add("canReadJunimoText");
                Monitor.Log("Granted canReadJunimoText (legacy: its cutscene was skipped by an older version).", LogLevel.Info);
            }
            server.Broadcast("day_started", new JObject { ["date"] = Observer.Observe(null)["world"]!["date"] });
        };
        helper.Events.GameLoop.DayEnding += (_, _) => jobs.Cancel("the day ended");
        helper.Events.GameLoop.DayStarted += (_, _) => screenMessages.ClearHold();
        helper.Events.GameLoop.TimeChanged += (_, e) =>
        {
            if (e.NewTime % 100 == 0)
                server.Broadcast("time", new JObject { ["time"] = Observer.FormatTime(e.NewTime), ["time_raw"] = e.NewTime });
        };
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
        {
            jobs.Cancel("returned to title");
            screenMessages.ClearHold();
            WindowPlacement.SetTitle(null);
        };
    }

    private int traceTicks;
    private Microsoft.Xna.Framework.Vector2 lastTracePos;
    private string? lastEventId; // debug: log when a cutscene starts, and where

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        server.Drain(HandleCommand);
        if (Context.IsWorldReady && Game1.CurrentEvent?.id != lastEventId)
        {
            lastEventId = Game1.CurrentEvent?.id;
            if (Game1.CurrentEvent != null)
                Monitor.Log($"EVENT {Game1.CurrentEvent.id} started at {Game1.player.currentLocation?.NameOrUniqueName} {Game1.player.TilePoint}", LogLevel.Debug);
        }
        if (traceTicks > 0 && Context.IsWorldReady)
        {
            // Debug aid (debug_trace): per-tick movement, to diagnose janky walking.
            traceTicks--;
            var p = Game1.player;
            var delta = p.Position - lastTracePos;
            lastTracePos = p.Position;
            Monitor.Log($"TRACE t={e.Ticks} tile={p.TilePoint} d=({delta.X:0.#},{delta.Y:0.#}) moving={p.isMoving()} running={p.running} "
                + $"face={p.FacingDirection} frame={p.FarmerSprite.CurrentFrame} anim={p.FarmerSprite.CurrentAnimation?.Count ?? 0} "
                + $"ctrl={(p.controller == null ? "-" : p.controller.pathToEndPoint?.Count.ToString())} speed={p.getMovementSpeed():0.##} "
                + $"canMove={p.CanMove} pause={p.FarmerSprite.PauseForSingleAnimation} single={p.FarmerSprite.currentSingleAnimation} idx={p.FarmerSprite.currentAnimationIndex} dirs={p.movementDirections.Count}", LogLevel.Debug);
        }
        if (!Context.IsWorldReady)
        {
            autoStart.Tick();
            return;
        }
        // talk_to/sleep read their own dialogue and report it in the job result; every other menu
        // (locked doors, questions, cutscenes, ...) should reach the agent as a screen_message.
        if (jobs.Current?.Action is not ("sleep"))
            screenMessages.Tick();
        // Day-end screens (ready check, shipping summary, festivals) are dismissed so a co-op day
        // never hangs; interactive menus (shops, chests, dialogs) are left for the agent.
        janitor.Tick(leaveDialoguesAlone: true);
        // A cutscene must never play with a player's interactive menu up: vanilla closes menus when
        // an event starts, and one left open deadlocks the event's script forever (it can't advance
        // or finish while a menu is up). exitThisMenu() clears the active menu - just close it here.
        if (Game1.CurrentEvent != null
            && Game1.activeClickableMenu is { } menu
            && MenuAgent.IsInteractiveMenu(menu))
        {
            menu.exitThisMenu();
            Monitor.Log($"Closed {menu.GetType().Name} left open over cutscene {Game1.CurrentEvent.id}.", LogLevel.Info);
        }
        // A cutscene/festival takes over the character: the current job can't finish during it,
        // so cancel it - the agent sees the failure next to the dialogue (screen messages relay
        // every line) and plans again once the cutscene is over, like a player who was interrupted.
        // The bus ride is exempt: it is its own scripted sequence and must answer its own ticket
        // question, so it runs through events (RunThroughEvents).
        if (Game1.CurrentEvent != null && jobs.Current != null && !jobs.Current.RunThroughEvents)
            jobs.Cancel("a cutscene started - watch it and answer any question (answer n); the plan is cancelled, plan again when it's over");
        jobs.Tick();
    }

    /// <summary>Shrink the UI (incl. the chat box), since the windows are small. Applied by the game on the next frame.</summary>
    private void ApplyUiScale()
    {
        if (Config.UiScale is { } scale)
            Game1.options.desiredUIScale = Math.Clamp(scale, 0.5f, 1.5f);
    }

    /// <summary>
    /// Mute the background music on farmhand instances. The SetVolume patch keeps the background
    /// categories the game re-adjusts (Music/Ambient per tick) muted, but leaves the game's short
    /// sound effects (Sound/Footsteps) playing everywhere; this seeds the '"Default" category (and
    /// any the game never Sets the volume of) to zero at load.
    /// </summary>
    private void ApplySoundMute()
    {
        if (Config.MuteMusicForFarmhands && Context.IsWorldReady && !Context.IsMainPlayer)
        {
            try
            {
                foreach (string name in new[] { "Music", "Ambient", "Default" })
                    Game1.audioEngine?.GetCategory(name)?.SetVolume(0f);
                Monitor.Log("Muted background music (farmhand instance).", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Couldn't mute game audio: {ex}", LogLevel.Error);
            }
        }
    }

    internal void OnChat(string from, string text, bool isPrivate) =>
        server.Broadcast("chat", new JObject { ["from"] = from, ["text"] = text, ["private"] = isPrivate });

    internal void OnHudMessage(HUDMessage message) => screenMessages.Hud(message);

    internal void OnRawScreenText(string text) => screenMessages.Raw(text);

    private void OnJobFinished(Job job, bool ok, string summary)
    {
        Monitor.Log($"Job {job.Action} {(ok ? "done" : "failed")}: {summary}", LogLevel.Info);
        server.Broadcast("job_done", new JObject { ["job_id"] = job.Id, ["action"] = job.Action, ["ok"] = ok, ["summary"] = summary });
    }

    private JObject Hello() => new()
    {
        ["port"] = server.Port,
        ["world_ready"] = Context.IsWorldReady,
        ["farmer"] = Context.IsWorldReady ? Game1.player.Name : null,
        ["is_host"] = Context.IsWorldReady && Context.IsMainPlayer,
    };

    // ------------------------------------------------------------------ commands

    private JToken? HandleCommand(JObject msg)
    {
        string cmd = (string?)msg["cmd"] ?? throw new CommandException("missing 'cmd'");
        if (cmd == "hello")
            return Hello();
        if (!Context.IsWorldReady)
            throw new CommandException("no save loaded yet");

        string? Str(string key) => string.IsNullOrWhiteSpace((string?)msg[key]) ? null : ((string?)msg[key])!.Trim();
        int Int(string key, int def) => msg[key]?.Type == JTokenType.Integer ? (int)msg[key]! : int.TryParse((string?)msg[key], out var v) ? v : def;
        // Tile commands take an optional map tile (x, y).
        Microsoft.Xna.Framework.Point? Tile() => msg["x"] != null && msg["y"] != null
            ? new Microsoft.Xna.Framework.Point(Int("x", 0), Int("y", 0)) : null;
        var at = Tile();
        if (at is { } outside && (msg["location"] == null && msg["to"] == null))
        {
            // Tile commands act on the current map; a debug_warp_player with a destination
            // location has its tile in *that* map, so it's exempt from this check.
            var loc = Game1.player.currentLocation;
            int w = loc.map.Layers[0].LayerWidth, h = loc.map.Layers[0].LayerHeight;
            if (outside.X < 0 || outside.Y < 0 || outside.X >= w || outside.Y >= h)
                throw new CommandException($"{outside.X},{outside.Y} is outside this map ({w}x{h})");
        }
        string AtText() => at is { } t ? $" at {t.X},{t.Y}" : "";

        switch (cmd)
        {
            case "observe":
                return Observer.Observe(jobs.Current);

            case "say":
            {
                var text = Str("text") ?? throw new CommandException("missing 'text'");
                if (text.Length > 300)
                    text = text[..300];
                text = text.Replace('\n', ' ');
                if (text.StartsWith('/'))  // leads with a space so the game doesn't run it as a chat command
                    text = " " + text;
                Game1.chatBox?.textBoxEnter(text);
                return new JObject { ["summary"] = "Said it in the chat." };
            }

            case "emote":
            {
                var emote = Str("emote")?.ToLowerInvariant() ?? throw new CommandException("missing 'emote'");
                if (!Emotes.Contains(emote))
                    throw new CommandException($"unknown emote; use one of: {string.Join(", ", Emotes)}");
                Game1.player.netDoEmote(emote);
                return new JObject { ["summary"] = $"You emote: {emote}." };
            }

            case "screenshot":
            {
                float scale = msg["scale"]?.Type is JTokenType.Float or JTokenType.Integer ? (float)msg["scale"]! : 0.25f;
                var name = Game1.game1.takeMapScreenshot(scale, $"nobodyvalley_{Game1.player.Name}", null);
                return new JObject { ["file"] = name, ["dir"] = Path.Combine(Game1.game1.GetScreenshotFolder(), "") };
            }

            case "debug_state":
            {
                var p = Game1.player;
                return new JObject
                {
                    ["job"] = jobs.Current?.Action,
                    ["job_summary"] = jobs.Current?.Summary,
                    ["location"] = p.currentLocation?.NameOrUniqueName,
                    ["tile"] = $"{p.TilePoint.X},{p.TilePoint.Y}",
                    ["controller"] = p.controller == null ? null : $"to {p.controller.endPoint}, {p.controller.pathToEndPoint?.Count ?? 0} steps left",
                    ["can_move"] = p.CanMove,
                    ["using_tool"] = p.UsingTool,
                    ["menu"] = Game1.activeClickableMenu?.GetType().Name,
                    ["event"] = Game1.CurrentEvent?.id,
                    ["warping"] = Game1.isWarping,
                    ["fade"] = Game1.fadeToBlack,
                    ["paused"] = Game1.paused,
                    ["freeze"] = p.freezePause,
                };
            }

            case "debug_give":
            {
                var name = Str("item") ?? throw new CommandException("missing 'item'");
                int n = Math.Clamp(Int("count", 1), 1, 999);
                var id = Game1.objectData.FirstOrDefault(o => (o.Value.Name ?? "").Equals(name, StringComparison.OrdinalIgnoreCase)).Key ?? name;
                var item = ItemRegistry.Create(id, n, allowNull: true) ?? throw new CommandException($"unknown item '{name}'");
                Game1.player.addItemToInventoryBool(item);
                return new JObject { ["gave"] = $"{n} {item.DisplayName}" };
            }

            case "debug_mail":
            {
                // Testing aid: add a mail flag (e.g. canReadJunimoText to un-scramble the bundles).
                var name = Str("mail") ?? Str("item") ?? throw new CommandException("missing 'mail'");
                Game1.player.mailReceived.Add(name);
                return new JObject { ["mail"] = name };
            }

            case "debug_money":
                Game1.player.Money = Math.Max(0, Int("amount", 100000));
                return new JObject { ["money"] = Game1.player.Money };

            case "debug_set_time":
            {
                int t = Int("time", 900);
                Game1.timeOfDay = t;
                return new JObject { ["time"] = Observer.FormatTime(t) };
            }

            case "debug_finish_buildings":
            {
                foreach (var b in Game1.getFarm().buildings)
                    if (b.daysOfConstructionLeft.Value > 0 || b.daysUntilUpgrade.Value > 0)
                        b.FinishConstruction();
                return new JObject { ["finished"] = true };
            }

            case "debug_build":
            {
                // Debug aid: place and finish a building right away (e.g. a Coop, to test the
                // animal shop without first playing through Robin's construction), like the vanilla
                // construction path but with zero build days. Small debris (weeds, twigs, little
                // stones - what a player clears with basic tools) is removed first.
                var name = Str("name") ?? Str("item") ?? throw new CommandException("missing 'name' (e.g. Coop)");
                var farm = Game1.getFarm();
                if (!Game1.buildingData.TryGetValue(name, out var data))
                    throw new CommandException($"unknown building '{name}'");
                var door = farm.GetMainFarmHouseEntry();
                // Clear the small debris near the farmhouse first (what a player would clear with
                // basic tools before building); big stumps/rocks and trees stay blocking.
                var clear = new List<KeyValuePair<Microsoft.Xna.Framework.Vector2, StardewValley.Object>>();
                foreach (var kv in farm.objects.Pairs)
                    if (kv.Value.IsWeeds() || kv.Value.IsTwig() || kv.Value.IsBreakableStone())
                        if (Math.Abs(kv.Key.X - door.X) < 26 && Math.Abs(kv.Key.Y - door.Y) < 12)
                            clear.Add(kv);
                foreach (var kv in clear)
                    farm.objects.Remove(kv.Key);
                // Scan a grid east and south of the farmhouse door for a clear spot.
                // isBuildable reads Game1.currentLocation's tile properties, so do this as if the
                // player stood on the farm (the vanilla placement view does the same).
                var previous = Game1.currentLocation;
                Game1.currentLocation = farm;
                try
                {
                    for (int dy = 1; dy < 10; dy++)
                    for (int dx = 2; dx < 24; dx++)
                    {
                        var tile = new Microsoft.Xna.Framework.Vector2(door.X + dx, door.Y + dy);
                        if (farm.buildStructure(name, tile, Game1.player, out var constructed, magicalConstruction: false))
                        {
                            constructed.FinishConstruction();
                            return new JObject { ["built"] = $"{name} at {tile.X},{tile.Y}" };
                        }
                    }
                }
                finally
                {
                    Game1.currentLocation = previous;
                }
                // Report why the (first) candidate spot wouldn't take, so a human can pick a spot.
                var probe = new Microsoft.Xna.Framework.Vector2(door.X + 4, door.Y + 4);
                var reasons = new List<string>();
                var building = StardewValley.Buildings.Building.CreateInstanceFromId(name, probe);
                for (int y = 0; y < building.tilesHigh.Value; y++)
                for (int x = 0; x < building.tilesWide.Value; x++)
                {
                    var t = probe + new Microsoft.Xna.Framework.Vector2(x, y);
                    if (!farm.isBuildable(t))
                    {
                        reasons.Add($"{t.X},{t.Y} not buildable"
                            + (farm.hasTileAt((int)t.X, (int)t.Y, "Back") ? "" : " noBackLayer")
                            + (farm.isWaterTile((int)t.X, (int)t.Y) ? " water" : "")
                            + (farm.isTilePlaceable(t) ? "" : " notPlaceable")
                            + (farm.IsTileOccupiedBy(t, StardewValley.CollisionMask.All, StardewValley.CollisionMask.None, useFarmerTile: true) ? " collides" : "")
                            + (farm.isTilePassable(t) ? "" : " impassable"));
                    }
                }
                if (building.isThereAnythingtoPreventConstruction(farm, probe) is { } blocked)
                    reasons.Add(blocked);
                throw new CommandException("no clear spot near the farmhouse"
                    + (reasons.Count > 0 ? $" (probe {probe.X},{probe.Y}: " + string.Join("; ", reasons.Take(6)) + ")" : ""));
            }

            case "debug_menu":
            {
                // Debug aid: open a standalone menu directly for testing. kind=cooking opens the
                // kitchen's cooking page exactly like the vanilla stove does (fridge contents and
                // all) - useful when the current farmhouse has no kitchen.
                var kind = Str("kind") ?? Str("menu") ?? "cooking";
                if (kind.StartsWith("cook", StringComparison.OrdinalIgnoreCase))
                {
                    Game1.player.currentLocation.ActivateKitchen();
                    return new JObject { ["menu"] = Game1.activeClickableMenu?.GetType().Name };
                }
                throw new CommandException("unknown menu kind (cooking)");
            }

            case "debug_bundles":
            {
                // Debug aid: the Community Center's bundle-spot tiles (Buildings layer indexes 1824-1833).
                var cc = Game1.currentLocation as StardewValley.Locations.CommunityCenter;
                var layer = cc?.map.GetLayer("Buildings");
                var list = new List<string>();
                for (int x = 0; layer != null && x < layer.LayerWidth; x++)
                for (int y = 0; layer != null && y < layer.LayerHeight; y++)
                {
                    int idx = cc!.getTileIndexAt(x, y, "Buildings", "indoors");
                    if (idx is >= 1824 and <= 1833)
                        list.Add($"{x},{y} ({idx})");
                }
                return new JObject { ["bundles"] = string.Join(" ", list) };
            }

            case "debug_event":
            {
                // Debug aid: start a scripted cutscene in the current location (script commands
                // like 'speak Willy "..."'; the event plays and the agent sees its dialogue).
                var script = Str("script") ?? "none/0 0/speak Willy \"This is a watched cutscene.\"/end";
                Game1.currentLocation.currentEvent = new Event(script); // the game runs it and cleans up when it ends
                return new JObject { ["event"] = script };
            }

            case "debug_warp_player":
            {
                // Debug aid: warp the local farmer somewhere without walking (location + x,y, or
                // just x,y for a tile in the current map). Accepts familiar names ("Pierre's").
                var who = Game1.player;
                string? location = Str("location") ?? Str("to");
                int wx = Int("x", (int)who.Tile.X);
                int wy = Int("y", (int)who.Tile.Y);
                Game1.activeClickableMenu = null; // a debug warp shouldn't leave a menu behind
                if (location != null)
                {
                    string? target = Places.Find(location)?.Location
                        ?? Navigation.GetLocation(location)?.NameOrUniqueName
                        ?? location;
                    Game1.warpFarmer(target, wx, wy, flip: false);
                }
                else
                {
                    who.Position = new Microsoft.Xna.Framework.Vector2(wx * 64, wy * 64);
                }
                who.controller = null;
                who.Halt();
                return new JObject { ["warped"] = $"{who.currentLocation?.NameOrUniqueName} {who.TilePoint.X},{who.TilePoint.Y}" };
            }

            case "debug_warp_npc":
            {
                // Debug aid: warp an NPC to a tile (and pause its schedule), e.g. put Pierre at his counter.
                var who = Game1.getCharacterFromName(Str("name") ?? throw new CommandException("missing 'name'"));
                if (who == null || who.currentLocation == null)
                    throw new CommandException("that NPC isn't here/loaded");
                var px = Int("x", (int)who.Tile.X);
                var py = Int("y", (int)who.Tile.Y);
                who.Position = new Microsoft.Xna.Framework.Vector2(px * 64, py * 64);
                who.controller = null;
                who.movementPause = 60 * 60 * 1000; // a game-hour
                who.faceDirection(0);
                return new JObject { ["warped"] = $"{who.Name} to {px},{py}" };
            }

            case "debug_trace":
                traceTicks = Math.Clamp(Int("ticks", 300), 1, 3600);
                return new JObject { ["tracing_ticks"] = traceTicks };

            case "debug_actions":
            {
                // Debug aid: every Action-tagged tile in the current location.
                var loc = Game1.player.currentLocation;
                var layer = loc.map.GetLayer("Buildings");
                var list = new List<string>();
                for (int x = 0; layer != null && x < layer.LayerWidth; x++)
                for (int y = 0; layer != null && y < layer.LayerHeight; y++)
                {
                    var action = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
                    if (action != null)
                        list.Add($"{x},{y}: {action}");
                }
                return new JObject { ["actions"] = string.Join("\n", list) };
            }

            case "stop":
                jobs.Cancel("stopped");
                return new JObject { ["summary"] = "Stopped." };

            case "walk":
            {
                var t = at ?? throw new CommandException("missing x, y");
                return StartJob(cmd, $"walking to {t.X},{t.Y}", j => TileJobs.Walk(j, t));
            }

            case "go_to":
            {
                var target = Str("target") ?? throw new CommandException("missing 'target'");
                return StartJob(cmd, $"walking to {target}", j => FarmJobs.GoTo(j, target));
            }

            // Click the tile like a player's right-click: villagers, doors, ripe crops, forage,
            // machines, chests, shop counters, the shipping bin, the bed...
            // Opening a shop/chest/dialogue blocks jobs (the game pauses for menus); the `use` job
            // itself must keep ticking to finish after the menu pops up.
            case "use":
            {
                var t = at ?? throw new CommandException("missing x, y");
                return StartJob(cmd, $"using {t.X},{t.Y}", j => TileJobs.Use(j, t), runDuringMenus: true);
            }

            // Left-click with the held item (equip it first): plant seeds, place things...
            case "place":
            {
                var t = at ?? throw new CommandException("missing x, y");
                return StartJob(cmd, $"placing {t.X},{t.Y}", j => TileJobs.Place(j, t));
            }

            // Swing one named tool at the tile - the vanilla use-tool action, nothing more.
            case "swing":
            {
                var tool = Str("tool") ?? throw new CommandException("missing 'tool' (axe, pickaxe, hoe, watering_can, scythe)");
                var t = at ?? throw new CommandException("missing x, y");
                return StartJob(cmd, $"swinging the {tool}{AtText()}", j => TileJobs.Swing(j, t, tool));
            }

            case "axe":
            case "pickaxe":
            case "hoe":
            case "watering_can":
            case "scythe":
            {
                var t = at ?? throw new CommandException("missing x, y");
                return StartJob(cmd, $"swinging the {cmd}{AtText()}", j => TileJobs.Swing(j, t, cmd));
            }

            // Fight: swing the equipped weapon at a tile with a monster on or near it (the game
            // decides hits and damage - this is just a player's weapon swing, repeated).
            case "sword":
            case "fight":
            case "attack":
            {
                var t = at ?? throw new CommandException("missing x, y");
                return StartJob(cmd, $"fighting at {t.X},{t.Y}", j => CombatJobs.Attack(j, t));
            }

            // Select an inventory item (the hand), like picking a toolbar slot. "nothing" = empty hand.
            case "equip":
            {
                var item = Str("item") ?? throw new CommandException("missing 'item'");
                var p = Game1.player;
                if (item.Equals("nothing", StringComparison.OrdinalIgnoreCase) || item.Equals("none", StringComparison.OrdinalIgnoreCase)
                    || item.Equals("empty", StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 0; i < p.Items.Count; i++)
                    {
                        if (p.Items[i] == null)
                        {
                            p.CurrentToolIndex = i;
                            return new JObject { ["summary"] = "Your hand is empty." };
                        }
                    }
                    throw new CommandException("your backpack is completely full - there's no empty slot to put the tool away");
                }
                var found = p.Items.FirstOrDefault(i => i != null && i.DisplayName.Contains(item, StringComparison.OrdinalIgnoreCase));
                if (found == null)
                    throw new CommandException($"you don't carry '{item}'");
                int idx = p.Items.IndexOf(found);
                p.CurrentToolIndex = idx;
                return new JObject { ["summary"] = $"Equipped {found.DisplayName} - it's in your hand now." };
            }

            // The menus: answer pop-ups (dialogues, letters, questions), click buttons in real
            // menus (shops, chests, the shipping bin, level-ups), close menus. All are paced
            // jobs - natural pauses between interactions, like a player's mouse.
            case "answer":
            {
                int option = Int("count", Int("option", 1));
                // The one thing that must keep working inside a cutscene: clicking through its
                // dialogue and answering questions (the agent hears every line as it scrolls).
                return StartJob(cmd, "answering", j => AnswerJob(j, option), runDuringMenus: true, runThroughEvents: true);
            }

            case "click":
            {
                var element = Str("element") ?? Str("item") ?? throw new CommandException("missing 'element' (what to click)");
                return StartJob(cmd, $"clicking {element}", j => MenuAgent.PacedClick(j, element, Int("count", Int("n", 1))), runDuringMenus: true);
            }

            case "close":
                return StartJob(cmd, "closing the menu", MenuAgent.PacedClose, runDuringMenus: true);

            // Type into the current text box (e.g. the name box when buying an animal at Marnie's).
            case "type":
            {
                var text = Str("text") ?? Str("item") ?? throw new CommandException("missing 'text' (what to type)");
                if (text.Length > 60)
                    text = text[..60];
                return new JObject { ["summary"] = MenuAgent.Type(text) };
            }

            // Open the backpack's crafting page, like a player pressing the menu key and picking the tab.
            case "crafting":
                Game1.activeClickableMenu = new GameMenu(GameMenu.craftingTab);
                return new JObject { ["summary"] = "Opened the crafting page - craft with click, close to leave." };

            case "check_community_center":
            {
                var cc = CommunityCenterJobs.Observe();
                var parts = new List<string> { (string?)cc["done"] ?? "Community Center" };
                foreach (var room in cc["rooms"] as JArray ?? new JArray())
                    parts.Add($"{room["room"]}: {string.Join("; ", room["needs"] as JArray ?? new JArray())}");
                if (cc["in_inventory"] is { Type: not JTokenType.Null } inv && !string.IsNullOrWhiteSpace((string?)inv))
                    parts.Add($"You carry: {inv}");
                if (cc["in_chests"] is { Type: not JTokenType.Null } chests && !string.IsNullOrWhiteSpace((string?)chests))
                    parts.Add($"In shared chests: {chests}");
                if (cc["hint"] is { Type: not JTokenType.Null } hint && !string.IsNullOrWhiteSpace((string?)hint))
                    parts.Add((string)hint!);
                return new JObject { ["summary"] = string.Join(". ", parts) };
            }

            case "map":
            {
                // The agent asks for the map when it needs tiles; observers otherwise only send
                // deltas, so the map is a tool call, not part of every turn.
                var m = MapView.Render(Game1.player);
                var parts = new List<string>
                {
                    $"Map around you at current location (you're @ at {m["you"]}):",
                    (string)m["grid"]!,
                };
                if (m["nearby"] is JObject nearby && nearby.Count > 0)
                    parts.Add("Points of interest on the map: "
                        + string.Join("; ", nearby.Properties().Select(p => $"{p.Name}: {p.Value}")));
                return new JObject { ["summary"] = string.Join("\n", parts) };
            }

            case "fish":
            {
                var where = Str("target");
                int n = Math.Clamp(Int("count", 3), 1, 10);
                return StartJob(cmd, where == null ? "fishing" : $"fishing at {where}", j => AdventureJobs.Fish(j, where, n));
            }

            case "descend":
                return StartJob(cmd, "heading down into the mines", AdventureJobs.Descend);

            case "eat":
            {
                var item = Str("item");
                return StartJob(cmd, "eating", j => FarmJobs.Eat(j, item));
            }

            case "sleep":
                return StartJob(cmd, "going to bed", FarmJobs.Sleep, runDuringMenus: true);

            default:
                throw new CommandException($"unknown command '{cmd}'");
        }
    }

    /// <summary>Click through a pop-up screen, like a player pressing the interact/close key.
    /// Wrapped in a paced job by the command switch.</summary>
    private IEnumerator AnswerJob(Job job, int option)
    {
        yield return MenuAgent.PacedWait();
        job.Summary = (string?)Answer(option)["summary"] ?? "";
        MenuAgent.ArmPacingNow();
    }

    private JObject Answer(int option)
    {
        // Cutscene questions use the game's own dialogue system (not a menu): answer the way a
        // player clicking an option would.
        if (Game1.isQuestion && Game1.questionChoices is { Count: > 0 })
        {
            int index = Math.Clamp(option - 1, 0, Game1.questionChoices.Count - 1);
            string chosen = Game1.questionChoices[index].responseText;
            Game1.currentQuestionChoice = index;
            Game1.isQuestion = false;
            if (Game1.currentSpeaker != null && Game1.currentSpeaker.CurrentDialogue.Count > 0
                && Game1.currentSpeaker.CurrentDialogue.Peek().isCurrentDialogueAQuestion())
            {
                Game1.currentDialogueCharacterIndex = 1;
                Game1.dialogueTyping = true; // the vanilla click on an option also sets these
                Game1.currentSpeaker.CurrentDialogue.Peek().chooseResponse(Game1.questionChoices[index]);
            }
            else
            {
                var loc = Game1.currentLocation;
                Game1.dialogueUp = false;
                if (Game1.eventUp && loc.afterQuestion == null)
                    loc.currentEvent?.answerDialogue(loc.lastQuestionKey, index);
                else
                    loc.answerDialogue(Game1.questionChoices[index]);
            }
            screenMessages.ClearHold();
            return new JObject { ["summary"] = $"Answered the cutscene question: {chosen}" };
        }
        if (Game1.activeClickableMenu is DialogueBox box)
        {
            if (box.isQuestion && box.responses.Length > 0)
            {
                option = Math.Max(0, option);
                int index = Math.Clamp(option <= 0 ? 1 : option, 1, box.responses.Length) - 1;
                box.selectedResponse = index;
                box.characterIndexInDialogue = Math.Max(0, (box.getCurrentString() ?? "").Length - 1);
                box.safetyTimer = 0;
                box.receiveLeftClick(0, 0);
                screenMessages.ClearHold();
                return new JObject { ["summary"] = $"Chose: {box.responses[index].responseText}" };
            }
            box.characterIndexInDialogue = Math.Max(0, (box.getCurrentString() ?? "").Length - 1);
            box.safetyTimer = 0;
            box.receiveLeftClick(0, 0);
            screenMessages.ClearHold();
            return new JObject { ["summary"] = "Continued the message." };
        }
        if (Game1.activeClickableMenu is LetterViewerMenu letter)
        {
            letter.exitThisMenu();
            screenMessages.ClearHold();
            return new JObject { ["summary"] = "Closed the letter." };
        }
        if (Game1.activeClickableMenu is ReadyCheckDialog ready)
        {
            if (ready.checkName == "sleep")
            {
                // The day-end ready check: confirm() would deadlock the co-op (this machine starts its
                // new-day transition before the others). It confirms itself once everyone is ready.
                screenMessages.ClearHold();
                return new JObject { ["summary"] = "The day is ending (ready check) - waiting for the other players; it rolls over on its own." };
            }
            ready.confirm();
            screenMessages.ClearHold();
            return new JObject { ["summary"] = "Confirmed ready." };
        }
        throw new CommandException("there's no message on screen to answer");
    }

    private JObject StartJob(string action, string activity, Func<Job, IEnumerator> body, bool runDuringMenus = false, bool runThroughEvents = false)
    {
        var job = new Job { Action = action, Activity = activity, Body = body, RunDuringMenus = runDuringMenus, RunThroughEvents = runThroughEvents };
        jobs.Start(job);
        return new JObject { ["job_id"] = job.Id, ["started"] = activity };
    }
}