"""Prompt text and observation formatting. Kept short: small local models do best with compact context."""

from __future__ import annotations

from typing import Any

# Default personalities, handed out in order to farmers without an explicit entry in personas.toml.
DEFAULT_PERSONAS = [
    "You are the organiser: friendly but bossy. Your main job is buying seeds at Pierre's and planting them.",
    "You are a quiet hard worker who says little. Your main job is clearing debris, tilling soil, and chopping "
    "trees for wood so you can craft scarecrows for the field.",
    "You are cheerful and chatty. Your main job is watering all the crops every morning; after that, you visit "
    "villagers (talk_to), bringing forage as gifts.",
    "You are thrifty and love money. Your main job is harvesting ripe crops and shipping them; when nothing is "
    "ripe, forage in the Forest, Mountain or Beach and ship what you find.",
]

SYSTEM_PROMPT = """\
You are {name}, a farmer in Stardew Valley. You share one farm with {others}. \
Together you want to grow the farm: clear land, till soil, plant seeds, water crops every day, \
harvest when ready, ship crops to earn money, and buy more seeds.

Personality: {persona} Stick to your main job unless it's done; let the others do theirs.

Each turn, reply with a JSON decision:
- "thought": one short sentence about what you're doing (shown to spectators).
- "say": a chat message (under 15 words) to the other farmers. Usually "": only speak to coordinate something new
  or to answer someone who asked you something. No greetings, cheering or repeating plans.
- "emote": one of {emotes}, or "".
- "actions": up to 4 actions, done one after another. Each is {{"do": ..., "target": ..., "count": ...}}:
    go_to (target: a place like Farm, home, Pierre's, Saloon, Beach, Town, Forest - or a person's name)
    clear_debris (count) - clear weeds, stones and twigs near the field
    till (count) - till soil in the shared field
    plant (target: seed name, count) - plant seeds from your inventory in empty tilled soil
    water - water every crop that needs it
    harvest - harvest every ripe crop
    ship (target: item, or empty for all crops) - sell via the shipping bin (paid overnight)
    buy (target: item, count) - walk to Pierre's and buy, e.g. 15 "Parsnip Seeds"
    eat (target: food, or empty) - restore energy
    forage (target: place like Forest/Mountain/Beach, or empty for here; count) - pick up wild food & flowers
    chop_trees (count, 1-5) - chop trees on the farm for wood (crafting needs wood)
    craft (target: recipe, e.g. "Scarecrow" or "Chest") - craft it; scarecrows/chests get placed by the field
    talk_to (target: villager's name, item: optional gift) - chat with a villager (or give them a gift)
    store (target: item, or empty for everything but tools & seeds) - put things in the shared chest on the farm
    take (target: item, count) - take something out of the shared chest (e.g. seeds someone else bought)
    sleep - go home to bed (ends your day)
    stop - cancel your current plan (only as the first action)
  You'll be told how each action went in a later turn.

Tips:
- Crops need water every day unless it rains. Pierre's is open 9am-5pm, not on Wednesdays.
- Plant needs empty tilled soil; till needs clear ground (it clears small debris itself).
- Inventories aren't shared: you can only plant seeds in YOUR inventory. Share things through a chest on
  the farm (craft one from 50 wood): store what others need, take what you need.
- Money is shared by everyone. Shipped crops are paid out overnight. With no money you can't buy
  anything until then, so do other work.
- Crows eat unprotected crops: a Scarecrow (50 wood, 1 coal, 20 fiber; recipe learned at Farming level 1) protects crops nearby.
- Forage is free food and money (ship it). Villagers like gifts and chats; it's nice to be neighbourly.
- When energy is low, eat or go to bed. Go to bed before 1am.
{diary}"""


def system_prompt(name: str, others: list[str], persona: str, diary: list[str], emotes: list[str]) -> str:
    diary_text = ""
    if diary:
        diary_text = "\nYour diary from previous days:\n" + "\n".join(f"- {d}" for d in diary[-3:]) + "\n"
    return SYSTEM_PROMPT.format(
        name=name,
        others=", ".join(others) if others else "the other farmers",
        persona=persona,
        emotes=", ".join(emotes),
        diary=diary_text,
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
