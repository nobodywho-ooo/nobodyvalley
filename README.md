# NobodyValley

LLM farmers playing Stardew Valley co-op, powered by [NobodyWho](https://docs.nobodywho.ooo).
Each farmer is a real player character in its own game instance (joined over LAN co-op), and all
agents run in one Python process sharing a single local model.

```
agents/ (Python, one process)                       game instances (one per farmer)
 ├─ NobodyWho Model (loaded once, Metal)
 ├─ Agent "Mads"     (ChatAsync) ── TCP :8765 ──── SMAPI + NobodyValley mod (host)
 ├─ Agent "Albert"   (ChatAsync) ── TCP :8766 ──── SMAPI + NobodyValley mod (farmhand)
 ├─ Agent "Jonathan" (ChatAsync) ── TCP :8767 ──── SMAPI + NobodyValley mod (farmhand)
 └─ Agent "Marek"    (ChatAsync) ── TCP :8768 ──── SMAPI + NobodyValley mod (farmhand)
```

## Running the demo

Prerequisites: Stardew Valley 1.6 + [SMAPI](https://smapi.io) 4.x, a co-op save with a cabin per
farmhand, the .NET SDK (`brew install dotnet`) and [uv](https://github.com/astral-sh/uv).

```sh
scripts/start-demo.sh --build --agents
```

This builds and installs the mod and starts the host, which loads the save in co-op mode. It then
starts the farmhand instances one at a time; each auto-joins `localhost` as its character. Each
instance places its own window in a grid cell, and then the agents run in the terminal. A cold
start takes about 45 seconds.

Configure it with `NV_SAVE`, `NV_FARMHANDS` and `NV_DISPLAY` (see the top of the script).
`swift scripts/screen-bounds.swift list` lists the displays. `scripts/start-demo.sh --stop`
kills the instances.

The agents also serve a live dashboard at http://127.0.0.1:8780. It has one panel per agent (what
it's doing, its latest thought, its plan, the last result) and the chat log. The launcher opens it
as a borderless Chrome window in a column to the right of the games, 360 points wide
(`NV_DASHBOARD_WIDTH`), or on another display with `NV_DASHBOARD_DISPLAY=1`.

The mod sets the in-game UI scale to 60%, so the chat box and menus fit the small windows. Change
`UiScale` in `Mods/NobodyValley/config.json`, or set it to `null` to keep the game's own setting.
Note that the game saves this in its (shared) options.

To run just the agents against instances you started yourself (any instances with the mod
installed are found automatically on ports 8765+):

```sh
cd agents && uv run nw-valley
```

Personalities per farmer go in `agents/personas.toml`.

## How it works

### The mod (`mod/NobodyValley`, C#)

Each game instance's mod controls that instance's local farmer, and listens on the first free port
from 8765 (newline-delimited JSON). The agent gets **high-level actions** that run inside the game
as coroutine "jobs", stepping one game tick at a time. They use the real tool animations, so the
other players see the swings.

| Command | What it does |
|---|---|
| `observe` | Status, inventory, time/weather, farm summary (crops, needs water, ready...), what the other farmers are doing, villagers nearby, seeds for sale |
| `go_to {target}` | Walk across maps to a place (`Saloon`, `Pierre's`, `home`, ...) or a person |
| `clear_debris {count}` | Clear weeds/stones/twigs near the shared field |
| `till {count}` | Till soil, growing the shared field outward from a point south of the farmhouse |
| `plant {seed?, count}` | Plant seeds on empty tilled soil |
| `water` | Water every crop that needs it, refilling the can at the nearest water |
| `harvest` | Harvest everything that's ready |
| `ship {item?}` | Put crops into the shipping bin |
| `buy {item, count}` | Walk to Pierre's and buy (checks Pierre is there) |
| `eat {item?}`, `sleep` | Restore energy; go home to bed (answers the sleep prompt) |
| `forage {target?, count}` | Pick up wild forage (leeks, daffodils, ...) here or in another location |
| `chop_trees {count}` | Chop trees on the farm (full-grown first) and collect the wood |
| `craft {item}` | Craft a known recipe; placeable things (scarecrow, chest, ...) get placed by the field |
| `store {item?}`, `take {item, count}` | Put things into / take things out of the shared chest on the farm |
| `talk_to {target, item?}` | Walk to a villager and talk (or give `item` as a gift). Reports their actual dialogue and your friendship |
| `say {text}`, `emote {emote}` | Multiplayer chat message; emote above the head |
| `stop` | Cancel the current job |
| `debug_state`, `debug_set_time {time}`, `debug_give {item, count}`, `screenshot` | Debugging aids |

Job commands reply right away with a `job_id`. A `job_done` event (`ok`, `summary`) follows when
the job finishes. Other events: `chat` (a message from another player), `time` (every in-game
hour), `day_started`, `world_ready`.

Coordination between the instances uses `Farmer.modData`, which the game syncs between players.
Each farmer publishes what it's doing and the tile it's about to work on, so the others skip that
tile and can see e.g. "Albert: watering the crops".

Environment variables let a launcher skip the title screen and place the window:
`NOBODYVALLEY_HOST=<save>` (host in co-op), `NOBODYVALLEY_JOIN=<farmhand name>`,
`NOBODYVALLEY_LOAD=<save>` (single-player) and `NOBODYVALLEY_WINDOW=x,y,w,h` (screen points). The
mod also clicks through the overnight menus (shipping summary, level-ups) and skippable cutscenes.

### The agents (`agents/`, Python)

- **One `nobodywho.Model`, many `ChatAsync`s:** every agent has its own chat (persona, history),
  all on one loaded model. The default is Qwen3.5-9B (Q4_K_M, 5.4 GB). With 4 agents and 8k
  context each, the process uses about 7 GB. Use `--model` to try others.
- **Structured decisions, not tool calls.** Each turn is one generation, constrained by NobodyWho's
  JSON-schema sampler to `{"thought", "say", "emote", "actions": [up to 4]}`, with length limits
  on every field. The actions then run one after another in the game. We started with tool
  calling, but the models narrated between calls (and invented results), which made turns slow and
  long. A constrained decision is fast, bounded and always valid.
- **When an agent takes a turn:** when its plan is finished or fails, when a chat message
  mentions it by name, on the hour if it's idle, or after 20s of idling. Other chat messages are
  collected and shown at its next turn, which keeps the four from chatting endlessly. A turn
  taken mid-plan can only reply in chat (or `stop` first).
- **Quiet chat:** the prompt asks for `say: ""` unless there's something new to coordinate. Each
  agent speaks at most once per 90s unless someone addresses it by name.
- **Team awareness:** each prompt includes what the other agents have planned, since they all run
  in the same process.
- **Bedtime:** from 11pm agents are told to go to bed; after 1am the code sends them. Once in bed,
  an agent takes no turns until morning.
- **Guards for small models:** at most 4 actions per turn (enforced by the schema). A turn
  without actions gets a nudge. Repeating the same failure gets flagged.
- **Debugging:** `NV_DEBUG_TOKENS=1 uv run nw-valley` echoes every generated token to stderr.
- **Daily memory:** at the start of each day, each agent writes a short diary entry. Its chat is
  then reset with the last few diary entries in the system prompt.

Known NobodyWho issue: `ChatAsync.stop_generation()` doesn't stop the generation. The token
stream just stops yielding, and the next `ask()` waits until the full reply has been generated. So
we can't cut off runaway replies, and rely on the schema's length limits instead.

## Development

```sh
scripts/test-instance.sh            # single-player test instance on a copy of a save (doesn't touch the real one)
cd agents
uv run python -m nw_valley.cli observe          # poke the mod by hand
uv run python -m nw_valley.cli till count=5     # waits for the job and prints the result
uv run python -m nw_valley.cli screenshot       # whole-map screenshot into the game's Screenshots folder
uv run python -m nw_valley.cli debug_set_time time=900
uv run python -m nw_valley.fake_game            # fake game on ports 9765+ for testing the agent loop...
uv run nw-valley --base-port 9765               # ...without Stardew running
```

`cd mod/NobodyValley && dotnet build` builds the mod and copies it into the game's `Mods` folder.
Restart the game to load the new version.

`ideas/` holds reference material, and isn't committed: StarDojo, stardew-anima, SMAPI source, the
decompiled game (handy for finding the right game APIs), the NobodyWho docs, and scratch files.
