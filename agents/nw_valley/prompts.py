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

Reply each turn with JSON:
- "thought": one short sentence (shown to spectators).
- "say": message to the other farmers, under 15 words, or "". Only speak to coordinate or answer a question.
- "emote": one of {emotes}, or "".
- "actions": up to 4, done in order, each {{"do": ..., "target": ..., "item": ..., "count": ...}}:
  go_to(target: place like Farm/home/Pierre's/Saloon/Beach/Town/Forest, or a person)
  clear_debris(count), till(count), plant(target: seed, count), water, harvest
  ship(target: item, empty = all crops; paid overnight), buy(target: item, count) at Pierre's
  eat(target: food), forage(target: place or empty for here, count), chop_trees(count 1-5)
  craft(target: recipe; scarecrows/chests get placed by the field)
  talk_to(target: villager, item: optional gift)
  store(target: item, empty = all but tools & seeds) / take(target: item, count): the shared chest
  sleep (go to bed, ends your day), stop (cancel your plan; first action only)

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
    return "\n".join(lines)
