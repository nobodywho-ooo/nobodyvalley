using System.Collections;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using NobodyValley.Jobs;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace NobodyValley;

/// <summary>
/// Lets an external agent control the local farmer of this game instance.
/// Run one game instance per agent (joined over LAN co-op); each instance's mod listens on its own port.
/// </summary>
internal sealed class ModEntry : Mod
{
    internal static ModEntry Instance = null!;
    internal ModConfig Config = null!;

    private BridgeServer server = null!;
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

        server = new BridgeServer(Monitor);
        server.Start(Config.BasePort, Config.PortRange);
        jobs = new JobRunner(Monitor, OnJobFinished);
        janitor = new MenuJanitor(helper, Monitor);
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
        helper.Events.GameLoop.TimeChanged += (_, e) =>
        {
            if (e.NewTime % 100 == 0)
                server.Broadcast("time", new JObject { ["time"] = Observer.FormatTime(e.NewTime), ["time_raw"] = e.NewTime });
        };
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => jobs.Cancel("returned to title");
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        server.Drain(HandleCommand);
        if (!Context.IsWorldReady)
        {
            autoStart.Tick();
            return;
        }
        if (Config.AutoDismissMenus)
            janitor.Tick(leaveDialoguesAlone: jobs.Current?.RunDuringMenus == true);
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
                // Debug aid: whole-map screenshot of the current location (saved in the game's Screenshots folder).
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
                // Testing aid: add an item (by display name or id) to the inventory.
                var name = Str("item") ?? throw new CommandException("missing 'item'");
                int n = Math.Clamp(Int("count", 1), 1, 999);
                var id = Game1.objectData.FirstOrDefault(o => (o.Value.Name ?? "").Equals(name, StringComparison.OrdinalIgnoreCase)).Key ?? name;
                var item = ItemRegistry.Create(id, n, allowNull: true) ?? throw new CommandException($"unknown item '{name}'");
                Game1.player.addItemToInventoryBool(item);
                return new JObject { ["gave"] = $"{n} {item.DisplayName}" };
            }

            case "debug_set_time":
            {
                // Testing aid: jump the clock (single-player / host only).
                int t = Int("time", 900);
                Game1.timeOfDay = t;
                return new JObject { ["time"] = Observer.FormatTime(t) };
            }

            case "stop":
                jobs.Cancel("stopped");
                return new JObject { ["summary"] = "Stopped." };

            case "go_to":
            {
                var target = Str("target") ?? throw new CommandException("missing 'target'");
                return StartJob(cmd, $"walking to {target}", j => FarmJobs.GoTo(j, target));
            }
            case "clear_debris":
            {
                int n = Math.Clamp(Int("count", 10), 1, 25);
                return StartJob(cmd, "clearing debris from the field", j => FarmJobs.ClearDebris(j, n));
            }
            case "till":
            {
                int n = Math.Clamp(Int("count", 10), 1, 25);
                return StartJob(cmd, "tilling soil in the field", j => FarmJobs.Till(j, n));
            }
            case "plant":
            {
                var seed = Str("seed");
                int n = Math.Clamp(Int("count", 99), 1, 200);
                return StartJob(cmd, $"planting {seed ?? "seeds"}", j => FarmJobs.Plant(j, seed, n));
            }
            case "water":
                return StartJob(cmd, "watering the crops", FarmJobs.Water);
            case "harvest":
                return StartJob(cmd, "harvesting crops", FarmJobs.Harvest);
            case "ship":
            {
                var item = Str("item");
                return StartJob(cmd, "shipping produce", j => FarmJobs.Ship(j, item));
            }
            case "buy":
            {
                var item = Str("item") ?? throw new CommandException("missing 'item'");
                int n = Int("count", 1);
                return StartJob(cmd, $"buying {item} at Pierre's", j => FarmJobs.Buy(j, item, n));
            }
            case "eat":
            {
                var item = Str("item");
                return StartJob(cmd, "eating", j => FarmJobs.Eat(j, item));
            }
            case "sleep":
                return StartJob(cmd, "going to bed", FarmJobs.Sleep, runDuringMenus: true);
            case "forage":
            {
                var where = Str("target");
                int n = Math.Clamp(Int("count", 10), 1, 30);
                return StartJob(cmd, where == null ? "foraging" : $"foraging in {where}", j => MoreJobs.Forage(j, where, n));
            }
            case "chop_trees":
            {
                int n = Math.Clamp(Int("count", 1), 1, 5);
                return StartJob(cmd, "chopping trees", j => MoreJobs.ChopTrees(j, n));
            }
            case "craft":
            {
                var item = Str("item") ?? throw new CommandException("missing 'item'");
                return StartJob(cmd, $"crafting {item}", j => MoreJobs.Craft(j, item));
            }
            case "store":
            {
                var item = Str("item");
                return StartJob(cmd, "putting things in the chest", j => MoreJobs.Store(j, item));
            }
            case "take":
            {
                var item = Str("item") ?? throw new CommandException("missing 'item'");
                int n = Math.Clamp(Int("count", 999), 1, 999);
                return StartJob(cmd, $"taking {item} from the chest", j => MoreJobs.Take(j, item, n));
            }
            case "talk_to":
            {
                var who = Str("target") ?? throw new CommandException("missing 'target'");
                var gift = Str("item");
                return StartJob(cmd, gift == null ? $"talking to {who}" : $"giving {who} a gift", j => MoreJobs.TalkTo(j, who, gift), runDuringMenus: true);
            }

            default:
                throw new CommandException($"unknown command '{cmd}'");
        }
    }

    private JObject StartJob(string action, string activity, Func<Job, IEnumerator> body, bool runDuringMenus = false)
    {
        var job = new Job { Action = action, Activity = activity, Body = body, RunDuringMenus = runDuringMenus };
        jobs.Start(job);
        return new JObject { ["job_id"] = job.Id, ["started"] = activity };
    }
}
