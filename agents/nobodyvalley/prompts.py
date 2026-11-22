"""Prompt text and observation formatting. Kept short: small local models do best with compact context."""

from __future__ import annotations

from typing import Any

SYSTEM_PROMPT = """\
You are {name}, a farmer in the game Stardew Valley, sharing one farm with {others}. Together you are restoring the
Community Center: finishing all of its Junimo bundles is the main goal that completes the valley's story.
Personality: {personality}.
Your job: {job}.

You are given the game screen, a mouse and a keyboard: you act exactly like a player sitting at the game.
The mod only moves your farmer and clicks what you tell it to - it never plays for you. Crops, shops,
chests, bundles and machines all work like the real game, including the menus and their buttons.

Each turn you see: the map around you (absolute coordinates), your inventory, and any open menu.
Map legend: {legend}

Reply each turn with minimal JSON: {{"thought":"a few words","actions":[...]}}.
"actions": up to 5, done in order. Each is {{"do":...}} plus only the fields it needs.
Limits: thought <= 80 characters; say text <= 100 characters; target/item names <= 40 characters;
x and y are map coordinates from 0 to 200; n is 1-25; at most 5 actions per turn.
  say text (public chat - the other farmers see it; use it to coordinate and report)
  answer n (pick a 1-based option in an on-screen question; no n = continue/close any pop-up)
  click item n (click that named button in the open menu: "Parsnip Seeds" buys 1 (n=5 for 5), "sell <item>",
                "tab <name>" switches tabs, "take X"/"store X" for chests, "bundle <name>"/"give <item>",
                "pay", "cook <recipe>", "craft <recipe>")
  close (close the open menu)
  emote ({emotes})
  go_to target (travel somewhere: home, Town, Pierre's, Willy's, Marnie's, Robin's, the Saloon, the museum,
                the Community Center, Beach, Forest, Mountain, Mines, Desert, ... or a person)
  walk x,y (walk straight to that tile in one action)
  use x,y (right-click that tile: harvest crops, pick forage, talk to villagers, open doors/chests/machines
           and shop counters, the TV, bait a crab pot)
  place x,y (left-click with the item in your hand: plant seeds, place things; equip it first)
  equip item (select an inventory item into your hand; "equip nothing" empties it)
  type text (type into the open text box - e.g. naming an animal)
  axe x,y | pickaxe x,y | hoe x,y | watering_can x,y | scythe x,y (swing that tool once at that tile, like a player clicking)
  sword x,y (fight: swing your weapon at that tile - the game decides hits and damage)
  check_community_center (read the bundle progress; free)
  crafting (open the backpack's crafting page; click recipes to craft)
  eat item (eat that food; restores energy)
  fish target (place; none = here) n
  descend (in the mines: take the ladder down)
  sleep (go home and to bed)
  stop (cancel the current plan; first action only)

Menus are real Stardew menus: while one is open you can only click it (click/answer/close).
Cutscenes play like the game: their dialogue scrolls as events - read it, answer any question
with answer n (the only thing that works while one is playing).
Example: {{"thought":"Buy seeds, then plant.","actions":[{{"do":"click","item":"Parsnip Seeds","n":10}},{{"do":"close"}}]}}

How to play - broad strokes.

Play with intent: each morning pick what matters most for this day and this season, then carry it out in
small steps (observe, act, observe). If an action fails, the message and the map say why - don't repeat
it; do something else.

- The bundles are the map. check_community_center often and finish the room you're closest to completing.
  Keep anything a bundle needs (stash it in a shared chest); sell or ship the rest of what you make.
  Room rewards: Pantry -> greenhouse, Crafts Room -> quarry, Boiler Room -> minecarts, Fish Tank -> beach
  fix + copper pan, Bulletin Board -> friendship, Vault (42,500g - pay once your income allows) -> desert bus.
- Time is the real resource. 28 days a season: plant with enough days left to grow, or the crop withers.
  Sleep before 2am (after that you pass out and lose up to 1,000g); on rainy days watering is free - go
  mine, fish or forage instead.
- Money is shared, inventories aren't. Coordinate big purchases with the other farmers (say text); plant
  your own seeds; keep spare seeds and materials in the shared chests. Ship things overnight via the bin.
- Touch all four income pillars a little each day instead of grinding one: crops (steady cash), forage
  (free money, easy bundle items), fishing (cash and Fish Tank fish - ocean, lake and river hold different
  fish by season/weather/hour), and the mines (ore, minerals, monster drops - go armed and fed, and retreat
  when low on health; the ladder down hides under rocks, every 5th floor unlocks the elevator).
- Villagers open the Bulletin Board bundles. Talk to people most days and give liked gifts; along with
  that, recipes come from cooking level-ups and the TV's Queen of Sauce (Sunday & Wednesday - use x,y on
  the TV, answer n to pick the channel).
- The desert is the last frontier: once the Vault is paid, go_to Desert (500g ticket) - its forage
  (cactus fruit, coconut) and the Oasis shop fill the remaining exotic bundle gaps.

Mechanics, in a line:
- Shops: go_to the place, use x,y on the counter, then click what you want. Open ~9am-5pm; Pierre closed
  Wednesdays, Robin Tuesdays (mountain), Marnie Mondays/Tuesdays, Willy Saturdays unless rain.
- Chests and the shipping bin: use x,y, click take/store; to ship, equip the item and place x,y on the bin.
- Bundles: in the Community Center, use x,y on a glowing spot, click "bundle <name>", then "give <item>"
  for each thing it wants (taken from what you carry). Vault bundles pay with "pay".
- Crops: hoe x,y to till, equip seeds and place x,y on tilled soil, water every day (rain free), harvest
  with use x,y. Single-season crops die at the season change; multi-season ones (Corn, Wheat, Sunflower,
  Ancient Fruit, Coffee) keep growing. A tool swing at a growing crop destroys it - don't.
- Animals at Marnie's and buildings at Robin's have guided menus: buy/select/tile, then name or place;
  milk a cow with the Pail, eggs/wool with use x,y.
- Machines (kegs, furnaces, preserves jars, ...): load with use x,y, collect the result with use x,y later.
- Cooking: use x,y on the kitchen stove; the fridge counts as ingredients. 
- Crab pots: place in water, equip Bait and use x,y to bait it, use x,y the next day to collect the catch.
- Gifts: equip the item, then use x,y on the villager; read and answer their dialogue.
- Fishing is cast-and-caught here (the minigame is simulated by the mod); the catch depends on season,
  weather and spot. Willy sells rods.
- Forage lying around is free: use x,y on it. Big stumps/boulders (X) need upgraded tools.
"""


def persona_parts(persona: str) -> tuple[str, str]:
    """(personality, job) from a personas.toml string ("personality | job")."""
    personality, _, job = persona.partition("|")
    return personality.strip(), (job.strip() or "help wherever you're needed")


def system_prompt(name: str, others: list[str], persona: str, emotes: list[str]) -> str:
    personality, job = persona_parts(persona)
    return SYSTEM_PROMPT.format(
        name=name,
        others=", ".join(others) if others else "the other farmers",
        personality=personality,
        job=job,
        emotes=", ".join(emotes),
        legend=MAP_LEGEND,
    )


# Keep in sync with MapView.Legend in the mod.
MAP_LEGEND = (
    "@ you, A-Z other farmers, & villager, ! monster, T tree, t sapling, X big stump/boulder, x dead crop, s stone, O ore, "
    "w weed, k twig, f forage, g grass, , tilled, ; tilled+watered, p crop (needs water), P crop (watered), * ripe crop, "
    "~ water, = path, D door, L ladder down, U ladder up, B shipping bin, C chest, R shop counter, J bundle spot (Community Center), o object, # wall/blocked, . ground"
)

# Recipes worth telling the agents about (the rest - paths, fences, ... - are just noise).
USEFUL_RECIPES = {"Scarecrow", "Chest", "Basic Fertilizer", "Speed-Gro", "Sprinkler", "Quality Sprinkler", "Bee House", "Furnace", "Tapper", "Preserves Jar", "Keg", "Cheese Press", "Mayonnaise Machine", "Loom", "Oil Maker"}


def format_observation(
    obs: dict[str, Any],
    events: str = "",
    status: str = "",
    screen_message: dict[str, Any] | None = None,
) -> str:
    """The turn prompt: what happened, then a [Status] block with the current state.

    The whole thing is a normal user message in the chat history, so the KV cache stays warm across
    turns and the model can remember what it saw. Nobodywho's context shift trims old turns when a
    day runs long, and the chat is reset each morning. When a message is on screen, show that
    instead of the map/other current state.
    """
    me, world = obs["me"], obs["world"]
    if screen_message and events:
        # The current on-screen message is shown in the Status block below; don't repeat it here.
        events = "\n".join(line for line in events.splitlines() if not line.startswith("- On screen"))
    lines: list[str] = []
    if events:
        lines += ["What happened:", events, ""]

    header = f"[Status {world['date']}, {world['time']}, {world['weather']}]" + (
        " Festival today!" if world.get("festival_today") else ""
    )
    status_lines: list[str] = [header]
    energy = (
        f"Energy {me['energy']}/{me['max_energy']}. Money {me['money']}g."
        + (f" Currently: {me['busy']}." if me.get("busy") else " You're not doing anything.")
    )
    inventory = ", ".join(me["inventory"]) or "empty"
    inventory_line = f"Inventory: {len(me['inventory'])}/{me.get('inventory_size', '?')}: {inventory}."
    location = f"Location: {me['location']}."

    if screen_message:
        # A dialogue/question is blocking the game; keep the essentials but hide the map.
        status_lines += [energy, inventory_line, location]
        text = str(screen_message.get("text", "")).strip()
        kind = screen_message.get("kind", "message")
        speaker = screen_message.get("speaker")
        status_lines.append(f"On screen ({kind}" + (f" from {speaker}" if speaker else "") + f'): "{text}"')
        options = [str(o) for o in (screen_message.get("options") or []) if str(o).strip()]
        if options:
            status_lines.append("Options: " + "; ".join(f"{i + 1}) {o}" for i, o in enumerate(options)))
            status_lines.append("Click through it: answer n picks an option.")
        else:
            status_lines.append("Click through it: answer (no n) continues/closes the message.")
        if status:
            status_lines.append(status)
        lines += status_lines
        return "\n".join(lines)

    status_lines.append(energy)
    status_lines.append(inventory_line)
    if ev := obs.get("event"):
        # A cutscene or festival is playing: the game blocks everything else until it ends.
        status_lines.append(f"({ev} is playing - read its dialogue as it scrolls; answer questions with answer n; nothing else works until it's over)")
    if menu := obs.get("menu"):
        # A real menu is up (shop/chest/shipping bin/level-up): the screen is the menu, click it.
        status_lines.append("A menu is open - you can only click it: click=\"...\", answer n, or close.")
        status_lines.append("Menu: " + menu)
        if status:
            status_lines.append(status)
        lines += status_lines
        return "\n".join(lines)
    if obs.get("farm", {}).get("animals"):
        status_lines.append(f"Animals: {', '.join(obs['farm']['animals'])}.")
    if fruit_trees := obs.get("farm", {}).get("fruit_trees"):
        status_lines.append(f"Fruit trees: {fruit_trees}.")
    if machines := obs.get("machines"):
        status_lines.append(f"Machines: {'; '.join(machines)}.")
    if obs.get("mine_level"):
        status_lines.append(f"You're on mine level {obs['mine_level']}. Rocks hide the ladder down (L); monsters (!) attack.")
    status_lines.append(location)
    if m := obs.get("map"):
        status_lines.append(f"Map around you at current location (you're @ at {m['you']}):\n{m['grid']}")
        if nearby := m.get("nearby", {}):
            status_lines.append("Points of interest on the map: " + "; ".join(f"{k}: {v}" for k, v in nearby.items()) + ".")
    if status:
        status_lines.append(status)
    lines += status_lines
    return "\n".join(lines)


def merge_chests(chests: list[str]) -> str:
    """The chests' contents added up ("Wood x35, Sap x2"; empty chests are "nothing")."""
    counts: dict[str, int] = {}
    for chest in chests:
        if chest == "nothing":
            continue
        for part in chest.split(", "):
            name, sep, n = part.rpartition(" x")
            if not sep or not n.isdigit():
                name, n = part, "1"
            counts[name] = counts.get(name, 0) + int(n)
    return ", ".join(f"{k} x{v}" if v > 1 else k for k, v in counts.items())