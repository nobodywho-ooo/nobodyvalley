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
  say text (the public game chat - seen by the other farmers; only to coordinate or answer)
  answer n (choose a 1-based option in an on-screen question; no n = continue/close any pop-up)
  click item n (click that named button in the open menu - e.g. "Parsnip Seeds" to buy; n times if you give n, e.g. buy 5)
  close (close the open menu)
  emote ({emotes})
  go_to target (travel somewhere: Farm, Pierre's, Willy's, Marnie's, Saloon, Beach, Town, Forest, Mountain, Mines, home; or a person)
  walk x,y (walk straight to that tile in one action)
  use x,y (right-click that tile: harvest ripe crops, pick up forage, open chests/shipping bin/machines/doors, talk to villagers, click a shop counter)
  place x,y (left-click with the item in your hand, to plant/place it; equip it first)
  equip item (select an inventory item into your hand; "equip nothing" empties your hand)
  type text (type into the open text box - e.g. the name box when you buy an animal)
  axe x,y | pickaxe x,y | hoe x,y | watering_can x,y | scythe x,y (swing that tool once at that tile, like a player clicking)
  sword x,y (fight monsters: swing your weapon at that tile - the game decides hits and damage)
  check_community_center (read the bundle progress; free)
  crafting (open the backpack's crafting page; click recipes to craft)
  eat item (eat that food; restores energy)
  fish target(place or none = here) n
  descend (in the mines: take the ladder down)
  sleep (go home and to bed; answer the sleep prompt)
  stop (cancel the current plan; first action only)

Menus are real Stardew menus: while one is open you can only click it (click/answer/close).
Cutscenes play like the game: their dialogue scrolls as events - read it, and answer any question
with answer n (which is the only thing that works while one is playing).
Example: {{"thought":"Buy seeds, then plant.","actions":[{{"do":"click","item":"Parsnip Seeds","n":10}},{{"do":"close"}}]}}

Tips - read the screen:
- Shops (Pierre's, Willy's, Marnie's, the Saloon, ...): go_to the place, then use x,y on the counter.
  The shop menu appears and its whole list is shown. click="<item name>" buys 1 (add n=5 for 5);
  click="sell <item>" sells what you carry (or just its name when the shop doesn't sell it).
  click="tab <name>" switches the shop's categories; close when done.
- Chests and the shipping bin: use x,y on them; the menu lists both sides. click="take X" takes X out,
  click="store X" puts one in, close when done. To ship things, equip the item and click it on the bin
  (place x,y on the bin) - it's sold overnight.
- Community Center bundles: in the Community Center, use x,y on a glowing bundle; the bundle menu opens.
  click="bundle <name>", then click="give <item>" for each thing it still wants (it takes from what you
  carry). Vault bundles are paid with click="pay" (gold is shared). Craft chests/scarecrows to store gear
  and protect crops: crafting opens the crafting page (click="craft <recipe>", needs materials).
- Animals: at Marnie's Ranch, use x,y on the counter and answer the shop question. click="buy <animal>",
  then pick its home (click="place in <building>"), then name it (type the name, then click="done").
  Pet them with use x,y on them; eggs and wool are picked up with use x,y; a cow is milked by equipping
  the Milk Pail and using it on the cow.
- Buildings: at Robin's (the mountain, Tue-Sun), use x,y on her counter and answer the menu
  (Construct). Browse with click="next"/"prev", pick one with click="select <name>", then click="build"
  and choose a spot on the farm: click="tile X,Y". The menu lists the gold cost and materials (money is
  shared; materials come from your backpack and the shared chests). Upgrades work the same: select the
  upgrade and click the existing building on the farm. Demolish/move also ask for a tile click.
- Gifts: equip the item you want to give, then use x,y on the villager.
- Crops: till the soil (hoe x,y), plant seeds (equip them, then place x,y on tilled soil), water every
  day (watering_can x,y - rain free), and use x,y on ready crops to harvest. Wheat and fiber are cut
  with scythe x,y. Crops die at season change unless multi-season (Corn, Wheat, Sunflower, Ancient Fruit, Coffee).
- Cooking: at the stove in a kitchen (your house), use x,y on it - the cooking menu opens with the
  recipes you know; the fridge and mini-fridges count as ingredients. click="cook <name>" makes one
  (n=N for N). Cook early meals give energy for the work ahead.
- Tools do what they do, no more: the axe cuts trees (several swings) and twigs; the pickaxe breaks
  stones/ore and flattens empty tilled soil; the hoe tills soil (and cuts weeds on it); the scythe cuts
  grass (hay goes to a silo if there's room), weeds and dead crops; a watering can waters soil.
  Swinging a tool at a growing crop destroys it - that's the game, so don't.
- Money is shared; inventories aren't: you can only plant your own seeds; put extra in the shared chests.
- Forage lying around is free money: use x,y on it. Big stumps/boulders (X) need upgraded tools.
- Shops are open about 9am-5pm (Pierre closed Wednesdays, Robin Tuesdays, Marnie Mondays/Tuesdays, Willy
  Saturdays unless rain). Buy seeds in season; Plant with enough days left, or the crop withers.
- Sleep by midnight (full energy next day; after 2am you pass out and lose up to 1,000g). Everyone must
  sleep for a new day to start.
- Community Center: check_community_center shows what the bundles still need; keep those items (store
  them in the shared chest), and put them in the bundles when the option appears in the Junimo hut menu.
  Rooms reward: Pantry -> greenhouse, Crafts Room -> quarry, Boiler Room -> minecarts, Fish Tank -> beach
  fix + copper pan, Bulletin Board -> friendship, Vault (42,500g) -> desert bus.
- The Mines: 120 floors, the ladder is under rocks; every 5th floor unlocks the elevator. Copper floors
  2-39, iron 41+, gold 81+. Monsters (the ! on the map) hurt you - leave or fight back with your sword.
- Fishing: ocean at the Beach, lake at the Mountain, river in the Forest/Town; which fish depends on
  the season, weather and hour. Willy sells rods.
- Combat: in the mines monsters hit back. Fight with sword x,y (equip a sword first; the rusty
  sword works). Their drops (Slime, Bat Wing, Solar/Void Essence, Bone Fragment) feed the
  Adventurer's bundle; leave if you're low on health.
- A failed action stops your plan: read the map before choosing tiles, walk only on passable ground
  (~ water, # blocked), and don't repeat an action that just failed - try something else.
- Talk to villagers with use x,y; read their dialogue and answer it. Tell the other farmers in chat what
  you're doing, briefly.
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