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
  5-second `sleep` per tool call. Don't call scripts that wait internally (e.g.
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

## Testing workflow

- Single-player test instance on a copy of a save (port 8769 while the co-op game uses
  8765–8768): `NV_WINDOW=x,y,w,h nohup scripts/test-instance.sh > ideas/tmp/test-instance.out 2>&1 &`
- Drive the mod by hand: `cd agents && uv run python -m nw_valley.cli --port 8769 <cmd> key=value`.
  Useful commands: `debug_state`, `debug_give item=Wood count=50`, `debug_set_time time=2200`.
- Test the agent loop without the game: `uv run python -m nw_valley.fake_game`, then
  `uv run nw-valley --base-port 9765`.
- Full demo: `nohup scripts/start-demo.sh --agents > ideas/tmp/start-demo.out 2>&1 &`. Agent
  output goes to `agents/agents.log`; the dashboard is at http://127.0.0.1:8780.
- Look up game APIs in `ideas/stardew-decompiled/` (decompiled with ilspycmd), rather than
  guessing.

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
- **SMAPI/Stardew:**
  - SMAPI suppresses `UpdateTicked` while the game is saving.
  - Never click menus repeatedly: `ShippingMenu.okClicked()` restarts its fade-out on every
    call, so only click when `CanReceiveInput()`.
  - `Event.skipEvent()` doesn't set `skipped`; set it yourself.
  - Building doors need `building.doAction`, map doors need `location.checkAction`, edge warps
    need an explicit `Game1.warpFarmer`.
  - The NPC route cache ignores `Farm`, so routing uses our own BFS over warps/doors/buildings,
    which can route around blocked exits.
  - Farmhands only know NPCs in locations they've loaded.
  - Farmhands take 20–30s to join after "Joining as …"; start them one at a time.
  - Walking: the vanilla `PathFindController` stops the farmer for a tick on every tile (choppy
    animation), and in the *focused* window `UpdateControlInput` calls `player.Halt()` every tick when
    no keys are held (no animation at all). `SmoothPathController` and `HaltPatch` handle both.
    Use `debug_trace` to check.
  - The SMAPI launcher script drops CLI args (except after `--`). Pass settings via env vars
    (`NOBODYVALLEY_HOST/JOIN/LOAD/WINDOW`).
- **Small-model behaviour:**
  - Deterministic guards in `agent.py` beat prompt pleading: skip `till` when there's enough
    empty soil, `buy` when the shop is closed or there's no money, `sleep` before 6pm, and merge
    repeated actions.
  - The end of the prompt matters most (the role is repeated there).
  - Seeing the other agents' plans makes them coordinate.
