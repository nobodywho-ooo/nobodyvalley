# NobodyValley

LLM farmers playing Stardew Valley co-op, powered by [NobodyWho](https://docs.nobodywho.ooo).
Each farmer is a real player character in its own game instance (joined over LAN co-op), and all
agents run in one Python process sharing a single local model.

```
agents/ (Python, one process)                       game instances (one per farmer)
 ├─ NobodyWho Model (loaded once, Metal)
 ├─ Agent "Alice"   (ChatAsync) ── TCP :8765 ──── SMAPI + NobodyValley mod (host)
 ├─ Agent "Bob"     (ChatAsync) ── TCP :8766 ──── SMAPI + NobodyValley mod (farmhand)
 ├─ Agent "Charlie" (ChatAsync) ── TCP :8767 ──── SMAPI + NobodyValley mod (farmhand)
 └─ Agent "David"   (ChatAsync) ── TCP :8768 ──── SMAPI + NobodyValley mod (farmhand)
```

## Running the demo

Prerequisites: Stardew Valley 1.6 + [SMAPI](https://smapi.io) 4.x, a co-op save with a cabin per
farmhand, the .NET SDK (`brew install dotnet`) and [uv](https://github.com/astral-sh/uv).

```sh
scripts/demo.sh
```

This stops anything already running, builds and installs the mod, and starts the host, which loads
the save in co-op mode. It then starts the farmhand instances one at a time; each auto-joins
`localhost` as its character. Each instance places its own window in a grid cell, and then the
agents run in the terminal. A cold start takes about 45 seconds; a farmhand that gets stuck
joining is restarted after 20 seconds. Each run's logs go to `logs/runs/<date>_<time>/`
(`logs/latest`): the games' output under `games/` (timestamped), the launcher's output, `agents.log`, and
one file per farmer with their full system prompt, turns and replies (e.g. `logs/latest/Marek.log`).

Configure it with `NV_SAVE`, `NV_FARMHANDS` and `NV_DISPLAY` (see the top of the script; put your
own values in `scripts/local.env`, gitignored — see `scripts/local.env.example`).
`swift scripts/screen-bounds.swift list` lists the displays. The script also takes subcommands:
`scripts/demo.sh build` only builds and deploys the mod, `scripts/demo.sh start`
starts the instances and agents without rebuilding, and `scripts/demo.sh stop` kills them
and keeps a copy of the save with the run's logs.

The agents also serve a live dashboard at http://127.0.0.1:8780. It has one panel per agent (what
it's doing, its latest thought, its plan, the last result). A spinner shows when an agent
is waiting for the shared model ("waiting for the model" while other agents' turns are ahead of it, then "thinking"). The launcher opens it
in a native window (the system web view, via pywebview) in a column to the right of the games, 360 points wide
(`NV_DASHBOARD_WIDTH`), or on another display with `NV_DASHBOARD_DISPLAY=1`.

The mod sets the in-game UI scale to 60%, so the chat box and menus fit the small windows. Change
`UiScale` in `Mods/NobodyValley/config.json`, or set it to `null` to keep the game's own setting.
Note that the game saves this in its (shared) options.

To run it on another Mac, e.g. a Mac Mini, use `scripts/deploy.sh --start`. It builds the mod here, copies
the mod and the agents over (the other Mac needs Steam, the game, SMAPI and uv, but not .NET) and starts the demo
there. `--status`, `--stop` and `--fetch` manage it; `NV_REMOTE=user@host` picks another machine. The saves
sync through Steam Cloud, so don't run the demo on both machines at once.

To run just the agents against instances you started yourself (any instances with the mod
installed are found automatically on ports 8765+):

```sh
cd agents && uv run nobodyvalley
```

Personalities per farmer go in `agents/personas.toml`: a `personality` and a `job` each (the job is
included in the system prompt, not repeated every turn). Put your own farmers in `agents/personas.local.toml`
(gitignored); it's used instead of `personas.toml` when it exists.

Each farmer gets a random look (the character creator's dice), seeded by their name, so it's the same
every launch. Set `"LookSeed"` in `Mods/NobodyValley/config.json` to roll new looks, or `"RandomLooks": false`
to keep the saved ones.

## How it works

### The mod (`mod/NobodyValley`, C#)

Each game instance's mod controls that instance's local farmer, and listens on the first free port
from 8765 (newline-delimited JSON). The agent gets **high-level actions** that run inside the game
as coroutine "jobs", stepping one game tick at a time. They use the real tool animations, so the
other players see the swings.

| Command                                                                                                                                                 | What it does                                                                                                                                                                                                                                                                                                                                          |
| ------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `observe`                                                                                                                                               | Status, inventory, time/weather, farm summary, what the other farmers are doing, villagers nearby, seeds for sale, shared chest, and a **map view**: an ASCII grid of the 25×17 tiles around the farmer, plus nearest-first coordinate lists (trees, stones, ore, weeds, forage, crops needing water, ripe crops, empty soil, people, exits, ladders) |
| `go_to {target}`                                                                                                                                        | Walk across maps to a place (`Saloon`, `Pierre's`, `Willy's`, `Mines`, `home`, ...) or a person; leaves the mines first if needed                                                                                                                                                                                                                     |
| `walk {x,y}`, `use {x,y}`                                                                                                                               | Walk to a tile; "click" a tile (door, person, forage, sign, ...)                                                                                                                                                                                                                                                                                      |
| `clear_debris {x,y \| count}`                                                                                                                           | Clear the weed/stone/twig at a tile, or the nearest `count` near the field                                                                                                                                                                                                                                                                            |
| `chop_trees {x,y \| count}`                                                                                                                             | Chop the tree at a tile, or the nearest `count` trees on the farm (full-grown first)                                                                                                                                                                                                                                                                  |
| `mine {x,y \| count}`                                                                                                                                   | Break the rock at a tile, or the nearest `count` rocks here (in the mines: watches health, swings at monsters)                                                                                                                                                                                                                                        |
| `till {x,y \| count}`                                                                                                                                   | Till a tile, or grow the shared field (outward from a point south of the farmhouse)                                                                                                                                                                                                                                                                   |
| `plant {seed?, x,y \| count}`                                                                                                                           | Plant at a tile, or on the nearest empty tilled soil                                                                                                                                                                                                                                                                                                  |
| `water {x,y \| count?}`, `harvest {x,y \| count?}`                                                                                                      | One tile, the nearest `count`, or everything                                                                                                                                                                                                                                                                                                          |
| `ship {item?}`                                                                                                                                          | Put things in the shipping bin (default: crops, forage, fish, animal products)                                                                                                                                                                                                                                                                        |
| `buy {item, count}`                                                                                                                                     | Buy from any shop (Pierre, Willy, Marnie, the Saloon, Oasis, Krobus, Robin, Clint); checks the owner is there                                                                                                                                                                                                                                        |
| `donate {item?}`                                                                                                                                        | Walk to the Community Center and put matching items into the Junimo bundles (Vault bundles are paid in gold). Reports the missing items and progress in `observe`                                                                                                                                                                                   |
| `cook {item}`                                                                                                                                           | Cook a known recipe (e.g. Maki Roll, Fried Egg for the Chef's bundle)                                                                                                                                                                                                                                                                                 |
| `operate {x,y?}`                                                                                                                                        | Collect a machine's output or load the right item into a furnace/keg/preserves jar/cheese press/...                                                                                                                                                                                                                                                   |
| `build {building}`                                                                                                                                      | Build or upgrade a Coop/Barn/Silo (pays Robin's cost/materials), placing it on a free farm spot                                                                                                                                                                                                                                                        |
| `buy_animal {type}`                                                                                                                                     | Buy an animal from Marnie and put it in a free coop/barn                                                                                                                                                                                                                                                                                              |
| `care_animals`                                                                                                                                          | Pet/feed all animals and collect their products                                                                                                                                                                                                                                                                                                       |
| `plant_tree {sapling?}`, `fruit`                                                                                                                        | Plant a fruit-tree sapling on the farm; shake ripe fruit off the trees                                                                                                                                                                                                                                                                                |
| `repair {target}`                                                                                                                                       | Repair the beach bridge (300 wood)                                                                                                                                                                                                                                                                                                                     |
| `eat {item?}`, `sleep`                                                                                                                                  | Restore energy; go home to bed (answers the sleep prompt)                                                                                                                                                                                                                                                                                             |
| `forage {target?, count}`                                                                                                                               | Pick up wild forage here or in another location                                                                                                                                                                                                                                                                                                       |
| `fish {target?, count}`                                                                                                                                 | Fish from the nearest shore. Real cast/hold-up animations, but the catch is **simulated** (no minigame; fish from the game's tables for the spot/season/time)                                                                                                                                                                                         |
| `descend`                                                                                                                                               | Mines: enter from the entrance (elevator once you've been deep), or take a ladder down                                                                                                                                                                                                                                                                |
| `craft {item}`                                                                                                                                          | Craft a known recipe; placeable things (scarecrow, chest, ...) get placed by the field                                                                                                                                                                                                                                                                |
| `store {item?}`, `take {item, count}`                                                                                                                   | Put things into / take things out of the shared chest on the farm                                                                                                                                                                                                                                                                                     |
| `talk_to {target, item?}`                                                                                                                               | Walk to a villager and talk (or give `item` as a gift). Reports their actual dialogue and your friendship                                                                                                                                                                                                                                             |
| `say {text}`, `emote {emote}`                                                                                                                           | Multiplayer chat message (this is how the agents talk to each other - it appears in every game window's chat and reaches the other farmers like a human's message); emote above the head                                                                                                                                                                                                                                                   |
| `stop`                                                                                                                                                  | Cancel the current job                                                                                                                                                                                                                                                                                                                                |
| `debug_state`, `debug_set_time {time}`, `debug_give {item, count}`, `debug_menu {kind}`, `debug_terrain {target?}`, `debug_trace {ticks}`, `screenshot` | Debugging aids                                                                                                                                                                                                                                                                                                                                        |

Job commands reply right away with a `job_id`. A `job_done` event (`ok`, `summary`) follows when
the job finishes. Other events: `chat` (a message from another player in the game chat - human or
agent), `time` (every in-game hour), `day_started`, `world_ready`.

Coordination between the instances uses `Farmer.modData`, which the game syncs between players.
Each farmer publishes what it's doing and the tile it's about to work on, so the others skip that
tile and can see e.g. "Alice: watering the crops".

Environment variables let a launcher skip the title screen and place the window:
`NOBODYVALLEY_HOST=<save>` (host in co-op), `NOBODYVALLEY_JOIN=<farmhand name>`,
`NOBODYVALLEY_LOAD=<save>` (single-player), `NOBODYVALLEY_WINDOW=x,y,w,h` (screen points) and
`NOBODYVALLEY_PORT=<port>` (pin the agent port). The
mod also clicks through blocking screens (cutscenes, shipping summary, level-ups, letters, dialogue
pages), after showing each for `MenuDisplaySeconds` (default 5) so spectators can see them.

### The agents (`agents/`, Python)

- **One `nobodywho.Model`, many `ChatAsync`s:** every agent has its own chat (persona, history),
  all on one loaded model. The default is Qwen3.5-9B (Q4_K_M, 5.4 GB). With 4 agents and 8k
  context each, the process uses about 7 GB. Use `--model` to try others.
- **Hybrid actions:** most work verbs take either a map tile (`{"do":"chop","x":63,"y":13}`, picked
  from the map view) or a count for bulk work (`{"do":"water","n":5}`, or no `n` = all).
- **Compact JSON:** the schema uses llguidance's `"x-guidance": {"whitespace_flexible": false}` and
  only `thought`/`actions` are required, so replies have no whitespace or empty fields (~3× fewer tokens).
- **Context:** 32k tokens per agent by default (`--n-ctx`). Qwen3.5 is a hybrid model (only 8 of 32
  layers have a KV cache), so that's ~1 GB per agent.
- **Structured decisions, not tool calls.** Each turn is one generation, constrained by NobodyWho's
  JSON-schema sampler to `{"thought", "actions": [up to 10]}`, with length limits
  on every field. Talking and emoting are actions like the rest: `{"do":"say","text":"..."}`,
  `{"do":"emote","emote":"happy"}`.
  The actions then run one after another in the game. We started with tool
  calling, but the models narrated between calls (and invented results), which made turns slow and
  long. A constrained decision is fast, bounded and always valid.
- **When an agent takes a turn:** when its plan is finished or fails, when an in-game chat message
  mentions it by name, on the hour if it's idle, or after 20s of idling. Other chat messages are
  collected and shown at its next turn, which keeps the four from chatting endlessly. While a
  plan runs, only being addressed by name gives an agent a turn (which can only reply in chat, or
  `stop` first); everything else waits for the plan to finish.
- **Quiet chat:** the prompt asks for no `say` action unless there's something new to coordinate. Each
  agent speaks at most once per 90s unless someone addresses it by name.
- **No team awareness beyond the world:** agents only know what's visible - where the others are
  and what they're doing right now (synced through the game). No jobs, plans or inventories are
  shared; coordinating happens by chatting (`say`). Tile jobs claim their tile in the game, so two
  farmers don't work the same spot.
- **Bedtime:** from 11pm agents are told to go to bed; after 1am the code sends them. Once in bed,
  an agent takes no turns until morning.
- **Guards for small models:** at most 10 actions per turn (enforced by the schema). A turn
  without actions gets a nudge. Repeating the same failure gets flagged.
- **Debugging:** every farmer's full system prompt, turns and replies go to `<run>/<Farmer>.log` (or `agents/prompts/<Farmer>.log` when run by hand). `NV_DEBUG_TOKENS=1 uv run nobodyvalley`
  echoes every generated token to stderr.
- **Agents talk in-game:** a `say` action is sent through the game's chat box, so it shows up in
  every game window's chat and reaches the other farmers through the normal `chat` event - exactly
  like a message typed by a human. There's no separate agent-only chat any more; the dashboard is
  just the status panels.
- **Fresh context every morning:** the chats are reset each day; the observation carries the state
  that matters.

Known NobodyWho issue: `ChatAsync.stop_generation()` doesn't stop the generation. The token
stream just stops yielding, and the next `ask()` waits until the full reply has been generated. So
we can't cut off runaway replies, and rely on the schema's length limits instead.

## Development

```sh
NV_WINDOW=x,y,w,h scripts/test-instance.sh [save]   # single-player test instance on a copy of a save, on port 8790
                                                  # (outside the agents' range, so they don't pick it up)
cd agents
uv run python -m nobodyvalley.cli --port 8790 observe    # poke the mod by hand
uv run python -m nobodyvalley.cli till count=5     # waits for the job and prints the result
uv run python -m nobodyvalley.cli screenshot       # whole-map screenshot into the game's Screenshots folder
uv run python -m nobodyvalley.cli debug_set_time time=900
uv run python -m nobodyvalley.fake_game            # fake game on ports 9765+ for testing the agent loop...
uv run nobodyvalley --base-port 9765               # ...without Stardew running
```

`cd mod/NobodyValley && dotnet build` builds the mod and copies it into the game's `Mods` folder.
Restart the game to load the new version.

`ideas/` holds reference material, and isn't committed: StarDojo, stardew-anima, SMAPI source, the
decompiled game (handy for finding the right game APIs), the NobodyWho docs, and scratch files.

## Status

See "Status" in `.agents/AGENTS.md` for what was done last, what's running where, and the open issues.
In short: the demo runs (here or on another Mac via `scripts/deploy.sh`); the last session ended
with an hour-long run there whose logs are waiting to be reviewed (`scripts/deploy.sh --fetch`).
