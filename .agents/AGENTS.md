# NobodyValley: notes for coding agents

LLM farmers playing Stardew Valley co-op on a Mac (an office demo for NobodyWho). See `README.md`
for the architecture and how to run things. This file covers working preferences and hard-won
knowledge.

## Preferences

- **Explain before big moves.** Give a short summary of the plan or design choice, and check in
  before pivoting or building something large. Don't silently go off on a long tangent.
- **Don't touch his running stuff without asking:** game instances, saves, installed apps. If
  you need to install something system-wide (e.g. `brew install`), say so.
- **Short polls only.** Never block a tool call for long. Run anything slow (game instances,
  agents, downloads, long jobs) in the background with `nohup … &`, then poll with at most a
  20-second `sleep` per tool call. Don't call scripts that wait internally (e.g.
  `scripts/test-instance.sh`) in the foreground.
- **Temporary and downloaded files go in `ideas/`** (scratch in `ideas/tmp/`), never `/tmp`.
  `ideas/` is gitignored, and also holds reference material (StarDojo, stardew-anima, SMAPI
  source, the decompiled game, NobodyWho docs).
- Use `rg`, not `grep`. Keep shell timeouts conservative.
- **Commit work as you go.** Put yourself (the model) in the commit author field.

## Product decisions (don't undo these)

- **Agents are real player characters**: one Stardew instance per farmer, joined via LAN co-op,
  with the host also an agent. Not possessed NPCs/villagers. That idea was rejected.
- **All NobodyWho inference runs in one Python process sharing one `Model`**, with one
  `ChatAsync` per agent.
- **Model:** must run on the office Mac Mini, which has 24 GB. Currently Qwen3.5-9B Q4_K_M, about
  7 GB with 4 agents.
- **Focus: cooperative farming** of the shared farm, plus foraging, wood, crafting, a shared chest
  and chatting with villagers.
- **Agents' chat stays out of the game** (dashboard only). No diaries; context resets daily.
- **Demo polish he asked for:**
  - windowed games tiled in a grid (not fullscreen)
  - thoughts shown in a separate dashboard window (not an in-game overlay)
  - small chat text (UI scale 60%)
  - agents that don't chat much
  - natural-looking behaviour (e.g. walk along paths rather than the shortest route)
- **Unattended:** nothing may block waiting for a click. Menus and cutscenes get
  auto-dismissed, and agents go to bed on their own.

## Environment

- Game: `~/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS`
  (SMAPI 4.5.2, Stardew 1.6.15).
- Saves: `~/.config/StardewValley/Saves`. The co-op save is `Nowhere_454507774` (host Mads,
  farmhands Albert, Jonathan, Marek). `Nowhere_454507043` is a solo save, used (as a copy) by
  `scripts/test-instance.sh`. There's a backup in `ideas/tmp/`.
- .NET SDK via Homebrew; set `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.
  `cd mod/NobodyValley && dotnet build` builds and deploys into the game's `Mods/`. Games must be
  restarted to load a new DLL.
- Two displays: built-in (1512×948 usable points, index 0) and a Dell 4K (1920×1080, index 1).
  `swift scripts/screen-bounds.swift list` shows them.
- No Screen Recording permission, so you can't screenshot the screen. Use the mod's `screenshot`
  command (whole-map PNG in `~/.local/share/StardewValley/Screenshots`).
  `ideas/tmp/winbounds` (compiled from `winbounds.swift`) lists the game windows' bounds.

- **Office Mac Mini** (`nobodywho@nobodywhos-mac-mini.local`; use `.local`, not the Tailscale name): deploy and
  run with `scripts/deploy.sh --start`, check with `--status`, run commands there with `--ssh CMD` (in the
  project folder `~/nobodyvalley`). Mads' ssh agent asks him to approve every new connection, so all calls go
  through one shared connection (ControlMaster); don't call plain `ssh` there. Same game/SMAPI builds as here;
  saves sync via Steam Cloud. One display (Dell, 1920x960 usable). No `rg`. It's an M4 Pro (this laptop:
  M5 Pro), so turns are about twice as slow. Watch its disk and memory: other things run there too (an
  Android emulator was using lots of memory once).

## Testing workflow

- Single-player test instance on a copy of a save, pinned to port 8790 (outside the 8765+ range the
  agents scan, so they don't adopt it): `NV_WINDOW=x,y,w,h nohup scripts/test-instance.sh [save] > ideas/tmp/test-instance.out 2>&1 &`.
  Running it with a different save leaves the previous test instance running; kill it first. Use
  `scripts/test-instance.sh Nowhere_454507774` (a copy of the co-op save) for anything that needs a later
  date, e.g. the mines (blocked by a landslide until day 5).
- Drive the mod by hand: `cd agents && uv run python -m nw_valley.cli --port 8790 <cmd> key=value`
  (run one job at a time; a new job cancels the running one).
  Useful commands: `debug_state`, `debug_give item=Wood count=50`, `debug_set_time time=2200`.
- Test the agent loop without the game: `uv run python -m nw_valley.fake_game`, then
  `uv run nobodyvalley --base-port 9765`.
- Full demo: `nohup scripts/start-demo.sh --build --agents > ideas/tmp/start-demo.out 2>&1 &` (takes about
  a minute; `--stop` stops the games and the agents, and keeps a copy of the save with the logs). Each run
  logs to `logs/runs/<date>_<time>/` (`logs/latest`): the games' output (timestamped), `start-demo.out`,
  `agents.log`, and every LLM prompt and reply in `prompts.log`: read those to tell model confusion from
  bugs. Runs on the Mac Mini: `scripts/deploy.sh --fetch` copies them to `logs/remote-runs/`. The dashboard
  is at http://127.0.0.1:8780.
- Python-only changes don't need the games restarted: `pkill -TERM -f bin/nobodyvalley`, then
  `cd agents && nohup uv run nobodyvalley --dashboard-window 1152,34,360,948 > ../ideas/tmp/agents.out 2>&1 &`.
  (Background processes ignore SIGINT.) Each restart replays the same morning, since the game only saves overnight.
- Look up game APIs in `ideas/stardew-decompiled/` (decompiled with ilspycmd), rather than
  guessing.

## Status (end of last session, 2026-11-16 evening)

**Running:** the demo was left running on the **Mac Mini** for an hour, with a timer that stops it at about
20:00 (`scripts/deploy.sh --start --stop-after 60`). Its logs are in `~/nobodyvalley/logs/runs/2026-11-16_190012/`
there; `scripts/deploy.sh --fetch` copies them to `logs/remote-runs/` here. **Next: go through those logs**
(`agents.log` for behaviour, `prompts.log` to see what each agent was shown, the game logs for mod errors),
and sort what's a bug from what's model confusion. Older runs from that day are all appended in the Mini's
`agents/agents.log` and `agents/prompts.log`. The demo isn't running on this laptop.

Done this session (all committed and deployed):

- Performance: game instances were at ~100% CPU each (SMAPI's console thread spinning on an EOF stdin;
  now a FIFO), and `--stop` crashed every game (SIGTERM; now SIGKILL), which kept ReportCrashService busy.
- Agents don't take turns while busy unless addressed (was 14% of the turns, slowing everyone down).
  Every step's result reaches the agent, and a repeat of the running action means "keep going".
- Prompt fixes found in `prompts.log`: interiors named properly (a cabin said "Nowhere Farm"), "Parsnip
  (1 ripe, 13 growing)", rain waters the crops, "tilled soil ready for seeds", chest contents added up,
  only craft what you need, counts in item names ("Parsnip Seeds n:5"), "none"/"all" placeholders.
- Agents stay off each other's tiles (claims in tile jobs; the lists leave out teammates' planned tiles).
- Wild spring onions are forage; `water`/`harvest` don't walk to the farm for nothing.
- Personas in `agents/personas.toml` (personality + job, each agent sees the others' jobs), random looks
  per farmer, a dashboard spinner while waiting for the model, fixed-height panels, the dashboard in a
  native pywebview window, farmhands that get stuck joining restart after 20s, and `scripts/deploy.sh`.

Open issues:

- The Mac Mini (M4 Pro) is about twice as slow as this laptop (M5 Pro): ~10 s per turn with four agents
  queueing on the shared model. Ideas: a shorter prompt (the map view is the biggest part), or batching.
- Small-model confusion that's left: going to Pierre's before 9am, "clearing" tilled soil, asking the chest
  for seeds it doesn't have, `till` on already-tilled tiles (now with a hint in the error).
- Trees can be walled in by debris (the pathfinder treats debris as solid), so "can't reach" on chop.
- Screen Sharing in High Performance mode replaces the Mini's Dell with a virtual display; the launcher then
  tiles onto that. Use Standard mode, or restart the demo after disconnecting.
- Not yet tested: the mines (`descend`, bulk `mine` with monsters), buying at Willy's (Mads and Jonathan
  need a rod for their jobs), `plant`/`water` on a tile.
- The fishing catch is simulated, without the minigame. Real fishing would mean faking input
  through FishingRod's states (see `ideas/stardew-decompiled/StardewValley.Tools/FishingRod.cs`).

## Gotchas learned the hard way

- **NobodyWho:**
  - `ChatAsync.stop_generation()` doesn't stop generation; the stream just stops yielding. Don't
    rely on it; bound output with the JSON-schema sampler's `maxLength`/`maxItems`.
  - Qwen3.5 narrates (and invents results) between tool calls, so the agents use one constrained
    JSON decision per turn instead of tool calling.
  - `@tool` reads type annotations, so don't use `from __future__ import annotations` in files
    that define tools.
  - Python's `HTTPServer.server_bind()` calls `getfqdn()`, which takes ~30s on this Mac. The
    dashboard overrides it.
  - Compact constrained JSON: `"x-guidance": {"whitespace_flexible": false}` in the schema works.
- **SMAPI/Stardew:**
  - SMAPI suppresses `UpdateTicked` while the game is saving.
  - Never click menus repeatedly: `ShippingMenu.okClicked()` restarts its fade-out on every
    call, so only click when `CanReceiveInput()`.
  - `Event.skipEvent()` doesn't set `skipped`; set it yourself.
  - `LevelUpMenu.receiveLeftClick` is empty (it polls the mouse); call `okButtonClicked()`.
  - The game's pathfinder ignores characters (`pathfinding: true`), so routes go through people.
  - `logs/test.log` can contain NUL bytes; use `rg -a`.
  - Building doors need `building.doAction`, map doors need `location.checkAction`, edge warps
    need an explicit `Game1.warpFarmer`.
  - The NPC route cache ignores `Farm`, so routing uses our own BFS over warps/doors/buildings,
    which can route around blocked exits.
  - Farmhands only know NPCs in locations they've loaded.
  - Farmhands take 20–30s to join after "Joining as …"; start them one at a time. Started together with
    the host, the Steam logins collide (`k_EResultDuplicateRequest`) and nobody gets the farmhand list.
    A farmhand stuck before "Joining as" needs a fresh process; the script restarts it after 20s.
  - Walking: the vanilla `PathFindController` stops the farmer for a tick on every tile (choppy
    animation), and in the _focused_ window `UpdateControlInput` calls `player.Halt()` every tick when
    no keys are held (no animation at all). `SmoothPathController` and `HaltPatch` handle both.
    Use `debug_trace` to check.
  - SMAPI's console thread spins on `Console.ReadLine()` at EOF, so with stdin on `/dev/null`
    (any background launch) each instance burns a whole core. The launch scripts give SMAPI a
    FIFO opened read-write (`<> fifo`) as stdin, which blocks forever instead.
  - SIGTERM makes the game abort in its native teardown (Steam networking/OpenAL), and macOS's
    ReportCrashService then burns CPU for minutes on each report. The scripts stop games with SIGKILL.
  - `GameLocation.DisplayName` of interiors without their own name (farmhouse, cabins, greenhouse, coops)
    falls back to the parent's: "Nowhere Farm". Use `Observer.LocationName` for anything agents read.
  - The SMAPI launcher script drops CLI args (except after `--`). Pass settings via env vars
    (`NOBODYVALLEY_HOST/JOIN/LOAD/WINDOW`).
- **Small-model behaviour:**
  - Deterministic guards in `agent.py` beat prompt pleading: skip `till` when there's enough
    empty soil, `buy` when the shop is closed or there's no money, `sleep` before 6pm, and merge
    repeated actions.
  - The end of the prompt matters most (the role is repeated there).
  - Seeing the other agents' plans makes them coordinate.
