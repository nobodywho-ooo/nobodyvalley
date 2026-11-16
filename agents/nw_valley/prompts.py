"""Prompt text and observation formatting. Kept short: small local models do best with compact context."""

from __future__ import annotations

from typing import Any

# Default (personality, job) pairs, by game instance order (host first). personas.toml can override them.
DEFAULT_PERSONAS = [
    ("Friendly but bossy; you like organising the others.", "buy seeds at Pierre's and plant them"),
    ("A quiet hard worker who says little.", "clear debris, till soil, and chop trees for wood"),
    ("Cheerful and chatty.", "water the crops every morning, then visit villagers with forage as gifts"),
    ("Thrifty; you love money.", "harvest and ship crops; when nothing's ripe, forage (Forest/Mountain/Beach) and ship it"),
]

SYSTEM_PROMPT = """\
You are {name}, a farmer in Stardew Valley, sharing one farm with {others}. Together you grow the farm: \
clear land, till, plant, water every day, harvest, ship crops for money, buy more seeds.
Personality: {personality}
Your job: {job}. Leave the others' jobs to them unless yours is done.

Reply each turn with compact JSON: {{"thought":"one short sentence","actions":[...]}}, optionally with
"say" (a message to the other farmers, under 15 words; only to coordinate or answer) and "emote" ({emotes}).
"actions": up to 4, done in order. Each is {{"do":...}} plus only the fields it needs. x,y is a tile on the map
you're shown; actions with x,y walk there and act on that tile. Without x,y, they work on the nearest n things.
  go_to target (place: Farm, home, Pierre's, Willy's, Saloon, Beach, Town, Forest, Mountain, Mines; or a person)
  walk x,y | use x,y (door, person, forage item, ...)
  chop x,y|n (trees) | mine x,y|n (rocks, ore) | clear x,y|n (weeds, stones, twigs)
  till x,y|n | plant item(seed) x,y|n | water x,y|n (neither = all) | harvest x,y|n (neither = all)
  ship item (none = all crops & forage; paid overnight) | buy item n (Pierre's seeds; Willy's rods & bait)
  eat item | craft item (chests/scarecrows get placed by the field) | store item / take item n (shared chest)
  forage target(place or none = here) n | fish target(place or none = here) n | descend (mines: ladder down)
  talk_to target(villager) item(optional gift) | sleep (ends your day) | stop (cancel plan; first only)
Example: {{"thought":"Clearing the stone next to the field.","actions":[{{"do":"mine","x":62,"y":17}},{{"do":"till","n":4}}]}}

Each turn you see a map of the area around you (x across the top, y down the side) and nearest-first lists of
coordinates. Map legend: {legend}

Tips: Pierre's is open 9am-5pm, not Wednesdays. Money is shared. Inventories aren't: you can only plant \
your own seeds; share via the chest (crafted from 50 wood). A Scarecrow (50 wood, 1 coal, 20 fiber; learned at \
Farming 1) keeps crows off crops. Forage is free money. Eat or sleep when energy is low; bed before 1am.
"""


def persona_parts(persona: str | tuple[str, str]) -> tuple[str, str]:
    """(personality, job) from a DEFAULT_PERSONAS entry or a personas.toml string ("personality | job")."""
    if isinstance(persona, tuple):
        return persona
    personality, _, job = persona.partition("|")
    return personality.strip(), (job.strip() or "help wherever you're needed")


def system_prompt(name: str, others: list[str], persona: str | tuple[str, str], emotes: list[str]) -> str:
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
USEFUL_RECIPES = {"Scarecrow", "Chest", "Basic Fertilizer", "Speed-Gro", "Sprinkler", "Quality Sprinkler", "Bee House", "Furnace"}


def format_observation(obs: dict[str, Any]) -> str:
    me, world, farm = obs["me"], obs["world"], obs["farm"]
    lines = [
        f"[{world['date']}, {world['time']}, {world['weather']}]"
        + (f" Festival today!" if world.get("festival_today") else ""),
        f"You are at: {me['location']}. Energy {me['energy']}/{me['max_energy']}. Money {me['money']}g."
        + (f" Currently: {me['busy']}." if me.get("busy") else " You're not doing anything."),
        f"Inventory{f' ({len(me["inventory"])}/{me["inventory_size"]} slots)' if me.get('inventory_size') else ''}: "
        f"{', '.join(me['inventory']) or 'empty'}.",
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
                    "raining" if farm["raining"] else None,
                ],
            )
        )
        + ".",
    ]
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
    # Leave out the fruit tree saplings (thousands of gold): just noise early on.
    seeds = [x for x in obs.get("seeds_for_sale", []) if "Sapling" not in x]
    if seeds:
        lines.append(f"Pierre sells: {', '.join(seeds)}.")
    if obs.get("chests"):
        lines.append("Shared chest: " + "; ".join(obs["chests"]) + ".")
    if obs.get("forage_here"):
        lines.append(f"There are {obs['forage_here']} forage items here.")
    useful = [r for r in obs.get("recipes", []) if r.split(" (")[0] in USEFUL_RECIPES]
    if useful:
        lines.append(f"Crafting: {', '.join(useful)}.")
    if obs.get("mine_level"):
        lines.append(f"You're on mine level {obs['mine_level']}. Rocks hide the ladder down (L); monsters (!) attack.")
    if m := obs.get("map"):
        lines.append(f"Map around you (you're @ at {m['you']}):\n{m['grid']}")
        if m.get("nearby"):
            lines.append("Nearest: " + "; ".join(f"{k}: {v}" for k, v in m["nearby"].items()) + ".")
    return "\n".join(lines)
