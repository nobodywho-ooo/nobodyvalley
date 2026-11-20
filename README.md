# NobodyValley

LLM farmers playing Stardew Valley co-op, powered by [NobodyWho](https://docs.nobodywho.ooo).
Each farmer is a real player character in its own game instance (joined over LAN co-op), and all
agents run in one Python process sharing a single local model.

```
agents/ (Python, one process)                       game instances (one per farmer)
 ├─ Model: one NobodyWho Model per distinct local model (shared by farmers on it), or Qwen3.8-Flash-Next on the ASCENTs
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
own values in `scripts/local.env`, gitignored — see `scripts/local.env.example`). `NV_MODEL` picks the
agents' model: `gemma4-e2b`, `gemma4-12b`, `qwen3.5-9b` (default), or `qwen3.8-flash-next`, which runs on
the ASCENTs (`../on-prem`) and is reached over the OpenAI-compatible API — set `NV_VLLM_URL` (default
`http://ascent1.local:8000/v1`) and `NV_VLLM_API_KEY` (the ASCENTs' vLLM key) for it.
`swift scripts/screen-bounds.swift list` lists the displays. The script also takes subcommands:
`scripts/demo.sh build` only builds and deploys the mod, `scripts/demo.sh start`
starts the instances and agents without rebuilding, and `scripts/demo.sh stop` kills them
and keeps a copy of the save with the run's logs.

The agents also serve a live dashboard at http://127.0.0.1:8780. It has one panel per agent (what
it's doing, its latest thought, its plan, the last result). A spinner shows when an agent
is waiting for the model ("waiting for the model" while it re-reads the history and the new prompt, then "thinking" once its first token arrives). The launcher opens it
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
from 8765 (newline-delimited JSON). The mod is deliberately **thin**: it moves the farmer and
clicks things (tiles, tools, menu buttons) - it never plays for you. Crops, shops, chests, tools,
bundles and machines all behave like the real game; the agents read the observation (the map, the
inventory, any open menu) and decide, exactly like a player with a screen, a mouse and a keyboard.

| Command                                                                                                                                                 | What it does                                                                                                                                                                                                                                                                                                                                          |
| ------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `observe`                                                                                                                                               | Status, inventory, time/weather, farm summary, what the other farmers are doing, people/monsters nearby, **any open menu** (shops, chests, the shipping bin, level-ups are serialized for the agent to click), and a **map view**: an ASCII grid of the 25×17 tiles around the farmer plus nearest-first lists (trees, stones, ore, weeds, forage, crops needing water, ripe crops, empty soil, grass, counters, exits) |
| `go_to {target}`                                                                                                                                        | Walk across maps to a place (`Saloon`, `Pierre's`, `Willy's`, `Mines`, `home`, ...) or a person                                                                                                                                                                                                                                                      |
| `walk {x,y}`                                                                                                                                            | Walk straight to a tile of the current map                                                                                                                                                                                                                                                                                                            |
| `use {x,y}`                                                                                                                                             | Right-click that tile like a player: harvest ripe crops, pick forage, talk to villagers, open chests/doors/machines, click a shop counter (`R` on the map) or the shipping bin - whatever the game does when you click it                                                                                                                              |
| `place {x,y}`                                                                                                                                           | Left-click with the item in your hand: plant seeds on tilled soil, place things; equip the item first. Left-clicking the shipping bin with a shippable item in hand ships it (sold overnight)                                                                                                                                                         |
| `equip {item}`                                                                                                                                          | Select an inventory item into your hand (like picking a toolbar slot); `equip nothing` empties it                                                                                                                                                                                                                                                      |
| `axe {x,y}`, `pickaxe {x,y}`, `hoe {x,y}`, `watering_can {x,y}`, `scythe {x,y}`                                                                         | Swing that tool once at the tile - exactly the vanilla use-tool action (trees/rocks/soil/grass/crops/monsters: the game decides the outcome)                                                                                                                                                                                                          |
| `answer {n}`                                                                                                                                            | Click through pop-ups like a player: continue a message, close a letter, pick option `n` in a question                                                                                                                                                                                                                                                 |
| `click {element}` (with `n` for repeats)                                                                                                                | Click a named button in the open menu: `click "Parsnip Seeds"` buys in a shop (vanilla cursor flow - item lands in your backpack), `click "store X"` / `"take X"` in a chest, `click "next"/"prev"` scrolls, `click "left"/"right"` picks a profession at a level-up                                                                                |
| `close`                                                                                                                                                 | Close the open menu (like pressing the menu key)                                                                                                                                                                                                                                                                                                      |
| `check_community_center`                                                                                                                                | Read the Junimo bundle progress. Donating: in the Community Center, `use x,y` on a bundle spot (`J` on the map), click a bundle, then `click "give <item>"` per ingredient (Vault: `click "pay"`) - the real Junimo bundle menu                                                            |
| `crafting`                                                                                                                                              | Open the backpack's crafting page (like pressing the menu key and picking the tab); it lists the recipes, `click "craft <name>"` crafts one onto your cursor and clicks it into your bag (n=N for N)                       |
| `eat {item}`, `sleep`, `fish {target?, count}`, `descend`                                                                                               | Eat food for energy; go home to bed (the sleep prompt is answered via `answer`); fish (cast is animated, the catch is simulated from the game's fish tables); take the mine ladder down                                                                                                                                                                 |
| `say {text}`, `emote {emote}`, `stop`                                                                                                                   | Chat in-game (reaches the other farmers); emote overhead; cancel the current action                                                                                                                                                                                                                                                                   |
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
`NOBODYVALLEY_PORT=<port>` (pin the agent port). Only un-attendable screens are auto-dismissed (the overnight
shipping summary - the co-op dawn can't continue without it - and festivals, after `MenuDisplaySeconds` so
spectators see them). Interactive menus - shops, chests, dialogues, letters, level-ups - stay up for the agents
to read and click, and **cutscenes play**: each spoken line is relayed to the agent, who answers any question
with `answer n`. (An older version skipped cutscenes; saves it ran may lack the Junimo-scroll mail, which the
mod repairs at load by granting it.)

### The agents (`agents/`, Python)

- **Per-farmer models** (`model` key in the persona table, e.g. `personas.toml`; default from
  `--model`/`NV_MODEL`, or a raw GGUF path / `huggingface:` reference). Choices: `qwen3.5-9b`
  (the default), `gemma4-e2b`, `gemma4-12b` — each **distinct** local model is loaded once as one
  `nobodywho.Model` and shared by the farmers on it (only the sizes of *different* models in use
  add up in RAM: ~3.1/5.7/7.1 GB each; the demo machine has 24 GB) — and `qwen3.8-flash-next`,
  which runs **remotely on the ASCENTs** (`../on-prem`): farmers on it share one client to the
  vLLM OpenAI-compatible endpoint (`NV_VLLM_URL`, default `http://ascent1.local:8000/v1`, with
  `NV_VLLM_API_KEY`) and get the same JSON-schema-constrained decisions via vLLM's guided
  decoding. The committed `personas.toml` demos all four at once (Alice=Gemma4-E2B,
  Bob=Gemma4-12B, Charlie=Qwen3.5-9B, David=Flash-Next).
- **Hybrid actions:** most work verbs take either a map tile (`{"do":"chop","x":63,"y":13}`, picked
  from the map view) or a count for bulk work (`{"do":"water","n":5}`, or no `n` = all).
- **Compact JSON:** the schema uses llguidance's `"x-guidance": {"whitespace_flexible": false}` and
  only `thought`/`actions` are required, so replies have no whitespace or empty fields (~3× fewer tokens).
- **Context:** 16k tokens per agent by default (`--n-ctx`). Qwen3.5 is a hybrid model (only 8 of 32
  layers have a KV cache), so that's ~500 MB per agent. Pass `--n-ctx 65536` for the old 64k setting.
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
- **Bedtime:** from 11pm agents are told to go to bed; at midnight the code sends them. Once in bed,
  an agent takes no turns until morning.
- **Guards for small models:** at most 5 actions per turn and a thought of at most 80 characters
  (both enforced by the schema) to keep reasoning cheap. A turn without actions gets a nudge.
  Repeating the same failure gets flagged.
- **Debugging:** every farmer's full system prompt, turns and replies go to `<run>/<Farmer>.log` (or `agents/prompts/<Farmer>.log` when run by hand). `NV_DEBUG_TOKENS=1 uv run nobodyvalley`
  echoes every generated token to stderr. `NV_DEBUG_TIMING=1` adds a per-turn timing breakdown (time
  to first token = prefill, decode ms/token, prefill size from `ctx_before`
  + `prompt_tokens`, slowest token gap) plus the per-day reset time, so you can tell slow
  generation from slow prefill from cache resets.
- **Agents talk in-game:** a `say` action is sent through the game's chat box, so it shows up in
  every game window's chat and reaches the other farmers through the normal `chat` event - exactly
  like a message typed by a human. There's no separate agent-only chat any more; the dashboard is
  just the status panels.
- **No history compaction:** each turn's observation is a normal user message in the chat, so the
  KV cache stays warm across turns (no re-prefill every turn). When the context gets full, NobodyWho's
  context shift trims the oldest turns, and the whole chat is reset fresh each morning anyway.
- **Day-end ready check:** the multiplayer "waiting for the other players" dialog is reported to the
  agent (`answer` confirms it), won by the mod's menu janitor after a few seconds, and confirmed by
  the `sleep` job, so a co-op day never hangs waiting for a farmhand who is already in bed.
- **Watchdog:** the agent process tracks how long each game instance has gone without answering
  (any reply or event counts). After 5 minutes of silence (`NV_STALE_AFTER`) it warns, probes the
  instance with a `hello`, and reconnects the agent the moment the game answers again. A farmhand
  whose instance is confirmed hung gets killed (opt-in: `NV_WATCHDOG_KILL=1`) so it can't deadlock
  the whole co-op at the next day transition; the host is never killed. Agents also back off and
  give up (instead of looping forever) when their game stops answering - see "MARKED DEAD" in the
  logs or the red badge on the dashboard.

Known NobodyWho issue: `ChatAsync.stop_generation()` doesn't stop the generation. The token
stream just stops yielding, and the next `ask()` waits until the full reply has been generated. So
we can't cut off runaway replies, and rely on the schema's length limits instead.

## Development

Watchdog knobs (also settable through `scripts/local.env`):

```sh
NV_STALE_AFTER=300     # seconds of total silence before an instance counts as hung (default 300)
NV_WATCHDOG_KILL=1    # 1 = kill a hung farmhand's process so the co-op continues without it
```

```sh
NV_WINDOW=x,y,w,h scripts/test-instance.sh [save]   # single-player test instance on a copy of a save, on port 8790
                                                  # (outside the agents' range, so they don't pick it up)
cd agents
uv run python -m nobodyvalley.cli --port 8790 observe    # poke the mod by hand
uv run python -m nobodyvalley.cli use x=5 y=18     # waits for the job and prints the result
uv run python -m nobodyvalley.cli click 'Parsnip Seeds'   # click the open menu's button
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
