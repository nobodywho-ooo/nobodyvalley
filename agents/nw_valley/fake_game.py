"""A tiny stand-in for the NobodyValley mod, for testing the agent loop without running Stardew Valley.

    uv run python -m nw_valley.fake_game --players Alice Bob Carol Dave
    uv run nw-valley --base-port 9765     # in another terminal

Each fake farmer listens on its own port (9765, 9766, ...; away from the real mod). Jobs just take a few seconds and
update a shared, very simplified farm.
"""

from __future__ import annotations

import argparse
import asyncio
import itertools
import json
import random
from typing import Any

JOB_SECONDS = {"go_to": 4, "clear_debris": 6, "till": 6, "plant": 5, "water": 5, "harvest": 4, "ship": 3, "buy": 5, "eat": 2, "sleep": 3}


class World:
    def __init__(self, players: list[str]):
        self.time = 600
        self.money = 500
        self.debris = 120
        self.tilled = 0
        self.crops: list[dict[str, Any]] = []
        self.players = {p: {"location": "Farm", "energy": 270, "inventory": ["Axe", "Hoe", "Watering Can", "Pickaxe", "Scythe", "Parsnip Seeds x15"], "doing": None} for p in players}
        self.servers: dict[str, "FakeInstance"] = {}

    def broadcast(self, event: dict[str, Any]) -> None:
        for s in self.servers.values():
            s.send(event)


class FakeInstance:
    ids = itertools.count(1)

    def __init__(self, world: World, name: str, port: int):
        self.world, self.name, self.port = world, name, port
        self.writers: list[asyncio.StreamWriter] = []
        self.job: asyncio.Task[None] | None = None
        self.job_id = 0

    def send(self, msg: dict[str, Any]) -> None:
        for w in self.writers:
            w.write((json.dumps(msg) + "\n").encode())

    async def handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        self.writers.append(writer)
        while line := await reader.readline():
            msg = json.loads(line)
            try:
                data = self.command(msg)
                writer.write((json.dumps({"id": msg.get("id"), "ok": True, "data": data}) + "\n").encode())
            except ValueError as e:
                writer.write((json.dumps({"id": msg.get("id"), "ok": False, "error": str(e)}) + "\n").encode())
        self.writers.remove(writer)

    def command(self, msg: dict[str, Any]) -> Any:
        w, me = self.world, self.world.players[self.name]
        cmd = msg["cmd"]
        if cmd == "hello":
            return {"port": self.port, "world_ready": True, "farmer": self.name, "is_host": self.port == 8765}
        if cmd == "observe":
            return {
                "me": {"name": self.name, "location": me["location"], "energy": me["energy"], "max_energy": 270, "money": w.money,
                       "busy": me["doing"], "inventory": me["inventory"]},
                "world": {"time": f"{w.time // 100}:{w.time % 100:02}", "date": "Mon Spring 1, year 1", "weather": "sunny"},
                "farm": {"crops": [f"{len(w.crops)} Parsnip"] if w.crops else [], "crops_needing_water": sum(not c["watered"] for c in w.crops),
                         "empty_tilled_soil": w.tilled - len(w.crops), "dead_crops": 0, "debris_near_field": w.debris, "raining": False},
                "others": [{"name": n, "location": p["location"], "doing": p["doing"] or "nothing in particular"} for n, p in w.players.items() if n != self.name],
                "nearby_villagers": [],
                "seeds_for_sale": ["Parsnip Seeds (20g)", "Potato Seeds (50g)", "Cauliflower Seeds (80g)"],
            }
        if cmd == "say":
            for s in w.servers.values():
                if s is not self:
                    s.send({"event": "chat", "from": self.name, "text": msg["text"], "private": False})
            return {"summary": "Said it."}
        if cmd == "emote":
            return {"summary": "ok"}
        if cmd == "stop":
            if self.job:
                self.job.cancel()
            return {"summary": "Stopped."}
        if cmd in JOB_SECONDS:
            if self.job:
                self.job.cancel()
                self.send({"event": "job_done", "job_id": self.job_id, "action": "?", "ok": False, "summary": "interrupted"})
            self.job_id = next(self.ids)
            me["doing"] = cmd
            self.job = asyncio.create_task(self.run_job(self.job_id, cmd, msg))
            return {"job_id": self.job_id, "started": cmd}
        raise ValueError(f"unknown command '{cmd}'")

    async def run_job(self, job_id: int, cmd: str, msg: dict[str, Any]) -> None:
        w, me = self.world, self.world.players[self.name]
        await asyncio.sleep(JOB_SECONDS[cmd] * random.uniform(0.7, 1.3))
        n = int(msg.get("count", 10))
        ok, summary = True, "done"
        if cmd == "go_to":
            me["location"] = msg["target"]
            summary = f"You arrived at {msg['target']}."
        elif cmd in ("till", "clear_debris", "plant", "water", "harvest") and me["location"] != "Farm":
            me["location"] = "Farm"
        if cmd == "clear_debris":
            n = min(n, w.debris); w.debris -= n; summary = f"Cleared {n} debris."
        elif cmd == "till":
            w.tilled += n; summary = f"Tilled {n} tiles."
        elif cmd == "plant":
            n = min(n, w.tilled - len(w.crops))
            if n <= 0:
                ok, summary = False, "no empty tilled soil to plant in (till some first)"
            else:
                w.crops += [{"watered": False}] * n; summary = f"Planted {n} Parsnip Seeds."
        elif cmd == "water":
            for c in w.crops:
                c["watered"] = True
            summary = "All crops are now watered."
        elif cmd == "buy":
            w.money -= 20 * n; summary = f"Bought {n} {msg['item']}. Money left: {w.money}g."
        me["energy"] = max(0, me["energy"] - 10)
        me["doing"] = None
        self.send({"event": "job_done", "job_id": job_id, "action": cmd, "ok": ok, "summary": summary})


async def clock(world: World) -> None:
    while True:
        await asyncio.sleep(7)
        world.time += 10
        if world.time % 100 == 60:
            world.time += 40
        if world.time % 100 == 0:
            world.broadcast({"event": "time", "time": f"{world.time // 100}:00", "time_raw": world.time})


async def amain(players: list[str], base_port: int) -> None:
    world = World(players)
    for i, name in enumerate(players):
        inst = FakeInstance(world, name, base_port + i)
        world.servers[name] = inst
        await asyncio.start_server(inst.handle, "127.0.0.1", inst.port)
        print(f"fake {name} on port {inst.port}")
    await clock(world)


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--players", nargs="+", default=["Alice", "Bob", "Carol", "Dave"])
    p.add_argument("--base-port", type=int, default=9765)
    a = p.parse_args()
    asyncio.run(amain(a.players, a.base_port))
