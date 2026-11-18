"""Prompt text and observation formatting. Kept short: small local models do best with compact context."""

from __future__ import annotations

import re
from typing import Any

SYSTEM_PROMPT = """\
You are {name}, a farmer in the game Stardew Valley, sharing one farm with {others}. Together you try to complete the game.
Personality: {personality}.
Your job: {job}.

Reply each turn with compact JSON: {{"thought":"one short sentence","actions":[...]}}.
"actions": up to 10, done in order. Each is {{"do":...}} plus only the fields it needs. x,y is a tile on the map
you're shown; actions with x,y walk there and act on that tile. Without x,y, they work on the nearest n things.
  say text (a message to the other farmers, only to coordinate or answer)
  emote ({emotes})
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
your own seeds; share via the shared chests on the farm. Only craft things you need: one chest is \
enough for everyone, so craft one only if there are none (or they're all full). A Scarecrow (50 wood, 1 coal, 20 fiber; learned at \
Farming 1) keeps crows off crops. Forage is free money. Eat or sleep when energy is low; bed before 1am.
"""


def persona_parts(persona: str) -> tuple[str, str]:
    """(personality, job) from a personas.toml string ("personality | job")."""
    personality, _, job = persona.partition("|")
    return personality.strip(), (job.strip() or "help wherever you're needed")


def system_prompt(name: str, others: list[str], persona: str, emotes: list[str],
                  roles: dict[str, str] | None = None) -> str:
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


def format_observation(obs: dict[str, Any], taken: set[str] | None = None) -> str:
    """The observation as prompt text. `taken`: "x,y" tiles that teammates here will work on (left out of the lists)."""
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
                    "raining, so the rain waters all the crops today (no need to water)" if farm["raining"] else None,
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
    if chests := obs.get("chests"):
        lines.append(f"Shared chests ({len(chests)} on the farm, no need to craft more): {merge_chests(chests) or 'all empty'}.")
    if obs.get("forage_here"):
        lines.append(f"There are {obs['forage_here']} forage items here.")
    useful = [r for r in obs.get("recipes", []) if r.split(" (")[0] in USEFUL_RECIPES]
    if useful:
        lines.append(f"Crafting: {', '.join(useful)}.")
    if obs.get("mine_level"):
        lines.append(f"You're on mine level {obs['mine_level']}. Rocks hide the ladder down (L); monsters (!) attack.")
    if m := obs.get("map"):
        lines.append(f"Map around you (you're @ at {m['you']}):\n{m['grid']}")
        raw = m.get("nearby", {})
        nearby = {k: v for k, v in ((k, _drop_tiles(v, taken or set())) for k, v in raw.items()) if v}
        if nearby:
            dropped = any(nearby.get(k) != v for k, v in raw.items())
            lines.append("Nearest: " + "; ".join(f"{k}: {v}" for k, v in nearby.items()) + "."
                         + (" (Tiles the others here are about to work on are left out.)" if dropped else ""))
    if me.get("location_id") != "Farm":
        # Otherwise agents waking up in their cabin pick crop coordinates off the cabin's map.
        lines.append("You're not on the farm, so the map and coordinates above are of this place, not the farm. "
                     "Farm work without x,y (e.g. water, harvest n) walks to the farm by itself.")
    return "\n".join(lines)


def _drop_tiles(coords: str, taken: set[str]) -> str:
    """Remove taken tiles from a list like "63,17 65,17" (lists with names, like exits, are left alone)."""
    parts = coords.split(" ")
    if not taken or not all(re.fullmatch(r"\d+,\d+", p) for p in parts):
        return coords
    return " ".join(p for p in parts if p not in taken)


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
