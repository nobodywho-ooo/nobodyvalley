"""Prompt text and observation formatting. Kept short: small local models do best with compact context."""

from __future__ import annotations

from typing import Any

SYSTEM_PROMPT = """\
You are {name}, a farmer in the game Stardew Valley, sharing one farm with {others}. Together you are restoring the
Community Center: finishing all of its Junimo bundles is the main goal that completes the valley's story. You inherited your
grandfather's old, overgrown farm: with hand-me-down tools and a few coins, you set out to begin your new life and turn those
fields into a thriving home. There's real opportunity here - with a little dedication.
Personality: {personality}.
Your job: {job}.

Reply each turn with compact JSON: {{"thought":"one short sentence","actions":[...]}}.
"actions": up to 10, done in order. Each is {{"do":...}} plus only the fields it needs. x,y is an offset from
your tile (you are 0,0), not an absolute map coordinate; each work action names the exact tile it acts on.
There is no count/n for chop, mine, clear, till, plant, water or harvest - use one action per tile.
Limits: thought <= 160 characters; say text <= 100 characters; target and item names <= 40 characters;
x and y are offsets from -200 to 200; n (only where it is listed) is 1-25; at most 10 actions per turn.
  say text (a public message, sent in the game chat and seen by the other farmers; only to coordinate or answer)
  answer n (choose a 1-based option in an on-screen question; no n = continue/close the message)
  check_community_center (show bundle progress and what is still missing)
  check_chest (show what is in the shared chests)
  open_backpack (show your inventory)
  start_crafting (show known recipes and ingredients)
  check_shop target (show what Pierre/Willy/Marnie/... sells; no target = Pierre)
  emote ({emotes})
  go_to target (place: Farm, home, Pierre's, Willy's, Saloon, Beach, Town, Forest, Mountain, Mines; or a person)
  walk x,y (door, person, forage item, ...)
  use x,y (door, person, forage item, ...)
  chop x,y (the tree at that tile)
  mine x,y (the rock/ore at that tile)
  clear x,y (the weed/stone/twig at that tile)
  till x,y
  plant item(seed) x,y
  water x,y
  harvest x,y
  ship item (none = all crops & forage; paid overnight; keeps bundle items)
  donate item (optional; none = every bundle item you carry) at the Community Center
  buy item n (any shop: Pierre, Willy, Marnie, Saloon, Oasis, Krobus, Robin, Clint)
  cook item (known recipes; e.g. Maki Roll, Fried Egg)
  operate x,y (collect from or load a machine; no x,y = nearest machine)
  build item (Coop, Barn, Big Coop, Big Barn, Deluxe Coop, Deluxe Barn, Silo; needs materials)
  buy_animal item (Chicken, Cow, Goat, Duck, Sheep, Rabbit, Pig at Marnie's)
  upgrade item (Axe, Pickaxe, Hoe or Watering Can at Clint's; 2 days)
  care_animals (pet/feed animals and collect milk, eggs, wool, ...)
  plant_tree item (fruit-tree sapling; needs open farm ground)
  fruit (shake ripe fruit off the fruit trees)
  repair target (bridge; 300 wood for the beach bridge)
  eat item
  craft item (chests/scarecrows get placed by the field)
  store item (shared chest)
  take item n (shared chest)
  forage target(place or none = here) n
  fish target(place or none = here) n
  descend (mines: ladder down)
  talk_to target(villager) item(optional gift)
  sleep (ends your day)
  stop (cancel plan; first only)
Example: {{"thought":"Clearing the stone next to the field.","actions":[{{"do":"mine","x":-2,"y":3}},{{"do":"till","x":2,"y":1}}]}}

Each turn you see a map of the area around you (coordinates are offsets from you, so you are 0,0) and nearest-first lists
of coordinates in the same relative system.
Map legend: {legend}

Tips: Pierre's is open 9am-5pm, not Wednesdays. Money is shared. Inventories aren't: you can only plant \
your own seeds; share via the shared chests on the farm. Only craft things you need: one chest is \
enough for everyone, so craft one only if there are none (or they're all full). A Scarecrow (50 wood, 1 coal, 20 fiber; learned at \
Farming 1) keeps crows off crops. Forage is free money. Eat or sleep when energy is low; bed before 1am.

Community Center: when you need to know which bundle items are missing, use check_community_center. Keep those items instead of
shipping them; when you are carrying any, go_to CommunityCenter and donate. Missing crops mean plant their seeds (buy
them from Pierre), missing forage/fish/minerals mean go and collect them, and missing animal/artisan goods need a coop,
barn or crafting machine. Tell the others in chat what you are collecting.
The six rooms: Pantry (seasonal crops, animal products, artisan goods), Crafts Room (forage, wood/stone/hardwood),
Fish Tank (fish and crab-pot catches), Boiler Room (bars, minerals, monster drops), Bulletin Board (cooking, dye,
field research, fodder, enchanter goods) and Vault (money). Do what the current season and weather allow; summer,
fall and winter items have to wait for their season (or come from the Traveling Cart/greenhouse).
For animal and artisan bundles, build a Coop and Barn (then Big/Deluxe versions), buy animals from Marnie,
care_animals every morning, and craft machines (Furnace, Preserves Jar, Cheese Press, Keg, ...) then operate
them. Fruit trees, tappers and fishing also fill bundle slots.
Use check_community_center when you need the current bundle list, check_chest when you need the shared chests,
open_backpack when you need your inventory, start_crafting for recipes and check_shop for shop stock. They are
there when you need them, not a checklist to run every turn.
When a villager asks for an item, gives you a quest, or tells you something useful, summarize it for the
others with a say action (e.g. "Pierre wants a cauliflower" or "Marnie asked for 10 hay"). Keep it short.
Keep the backpack lean: when you are carrying things you don't need right now (extra materials, forage, fish,
machines), put them in the shared chests with store. Keep tools, seeds and bundle items with you.
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
    "@ you, A-Z other farmers, & villager, ! monster, T tree, t sapling, X big stump/boulder, s stone, O ore, "
    "w weed, k twig, f forage, , tilled, ; tilled+watered, p crop (needs water), P crop (watered), * ripe crop, "
    "~ water, = path, D door, L ladder down, U ladder up, B shipping bin, C chest, o object, # wall/blocked, . ground"
)

# Recipes worth telling the agents about (the rest - paths, fences, ... - are just noise).
USEFUL_RECIPES = {"Scarecrow", "Chest", "Basic Fertilizer", "Speed-Gro", "Sprinkler", "Quality Sprinkler", "Bee House", "Furnace", "Tapper", "Preserves Jar", "Keg", "Cheese Press", "Mayonnaise Machine", "Loom", "Oil Maker"}


def format_observation(obs: dict[str, Any], events: str = "", status: str = "") -> str:
    """The observation as prompt text, with the rest of the turn's context merged in as labelled lines.

    `events`: what happened since the last turn (shown first, only when non-empty).
    `status`: speech rules (shown last, just before the ask).
    """
    me, world, farm = obs["me"], obs["world"], obs["farm"]
    lines: list[str] = []
    if events:
        lines += ["What happened:", events]
    lines += [
        f"[{world['date']}, {world['time']}, {world['weather']}]"
        + (f" Festival today!" if world.get("festival_today") else ""),
        f"Energy {me['energy']}/{me['max_energy']}. Money {me['money']}g."
        + (f" Currently: {me['busy']}." if me.get("busy") else " You're not doing anything."),
        f"Inventory: {len(me['inventory'])}/{me.get('inventory_size', '?')} slots.",
        "Farm: "
        + "; ".join(
            filter(
                None,
                [
                    f"crops: {', '.join(farm['crops'])}" if farm["crops"] else "no crops yet",
                    f"{farm['crops_needing_water']} need water" if farm["crops_needing_water"] else None,
                    f"{farm['empty_tilled_soil']} empty tilled tiles" if farm["empty_tilled_soil"] else None,
                    f"{farm['dead_crops']} dead crops" if farm["dead_crops"] else None,
                    f"{farm['debris_near_field']} weeds/stones near the field",
                    "raining, so the rain waters all the crops today (no need to water)" if farm["raining"] else None,
                ],
            )
        )
        + ".",
    ]
    if obs.get("farm", {}).get("buildings"):
        lines.append(f"Farm buildings: {', '.join(obs['farm']['buildings'])}.")
    if obs.get("farm", {}).get("animals"):
        lines.append(f"Animals: {', '.join(obs['farm']['animals'])}.")
    if fruit_trees := obs.get("farm", {}).get("fruit_trees"):
        lines.append(f"Fruit trees: {fruit_trees}.")
    others = obs.get("others") or []
    if others:
        lines.append(
            "Others: "
            + "; ".join(
                f"{o['name']} at {o['location']}" + (f" ({o['distance']} tiles away)" if o.get("distance") is not None else "")
                + f", {o['doing']}"
                for o in others
            )
            + "."
        )
    if obs.get("nearby_villagers"):
        lines.append(f"Villagers nearby: {', '.join(obs['nearby_villagers'])}.")
    if obs.get("forage_here"):
        lines.append(f"There are {obs['forage_here']} forage items here.")
    if machines := obs.get("machines"):
        lines.append(f"Machines: {'; '.join(machines)}.")
    if obs.get("mine_level"):
        lines.append(f"You're on mine level {obs['mine_level']}. Rocks hide the ladder down (L); monsters (!) attack.")
    if m := obs.get("map"):
        lines.append(f"You are at: {me['location']}.")
        lines.append(f"Map around you (you're @ at {m['you']}; coordinates are offsets):\n{m['grid']}")
        if nearby := m.get("nearby", {}):
            lines.append("Nearest: " + "; ".join(f"{k}: {v}" for k, v in nearby.items()) + ".")
    if me.get("location_id") != "Farm":
        # Otherwise agents waking up in their cabin pick crop coordinates off the cabin's map.
        lines.append("You're not on the farm; the map above shows this place. Use go_to Farm to work on the farm.")
    if status:
        lines.append(status)
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
