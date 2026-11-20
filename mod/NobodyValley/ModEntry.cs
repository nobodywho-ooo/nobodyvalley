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
            if (Config.RandomLooks)
                Looks.Randomize(Monitor, Config.LookSeed);
            WindowPlacement.Apply(Monitor);
            WindowPlacement.SetTitle(Game1.player.Name);
            Monitor.Log($"In game as {Game1.player.Name} ({(Context.IsMainPlayer ? "host" : "farmhand")}), agent port {server.Port}.", LogLevel.Info);
            server.Broadcast("world_ready", Hello());
        };
        helper.Events.GameLoop.DayStarted += (_, _) =>
        {
            Game1.options.pauseWhenOutOfFocus = false;
            ApplyUiScale();
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

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        server.Drain(HandleCommand);
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
        // A festival warp takes over the character: drop any pending job (it can't tick during the
        // event anyway), so the agent re-plans fresh once the festival is over.
        if (Game1.CurrentEvent is { isFestival: true } && jobs.Current != null)
            jobs.Cancel("the festival started");
        jobs.Tick();
    }

    /// <summary>Shrink the UI (incl. the chat box), since the windows are small. Applied by the game on the next frame.</summary>
    private void ApplyUiScale()
    {
        if (Config.UiScale is { } scale)
            Game1.options.desiredUIScale = Math.Clamp(scale, 0.5f, 1.5f);
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
        if (at is { } outside)
        {
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
            // menus (shops, chests, the shipping bin, level-ups), close menus.
            case "answer":
                return Answer(Int("count", Int("option", 1)));

            case "click":
            {
                var element = Str("element") ?? Str("item") ?? throw new CommandException("missing 'element' (what to click)");
                return new JObject { ["summary"] = MenuAgent.Click(element, Int("count", Int("n", 1))) };
            }

            case "close":
                return new JObject { ["summary"] = MenuAgent.Close() };

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

    /// <summary>Click through a pop-up screen, like a player pressing the interact/close key.</summary>
    private JObject Answer(int option)
    {
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

    private JObject StartJob(string action, string activity, Func<Job, IEnumerator> body, bool runDuringMenus = false)
    {
        var job = new Job { Action = action, Activity = activity, Body = body, RunDuringMenus = runDuringMenus };
        jobs.Start(job);
        return new JObject { ["job_id"] = job.Id, ["started"] = activity };
    }
}