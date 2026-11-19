"""Client for the NobodyValley SMAPI mod (newline-delimited JSON over TCP, one connection per game instance)."""

from __future__ import annotations

import asyncio
import itertools
import json
import logging
import time
from typing import Any

log = logging.getLogger(__name__)


class GameError(Exception):
    """The mod rejected a command (e.g. unknown place). The message is meant for the agent."""


class GameConnection:
    def __init__(self, port: int, host: str = "127.0.0.1"):
        self.host = host
        self.port = port
        self.events: asyncio.Queue[dict[str, Any]] = asyncio.Queue()
        self._ids = itertools.count(1)
        self._pending: dict[int, asyncio.Future[dict[str, Any]]] = {}
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._read_task: asyncio.Task[None] | None = None
        self.closed = asyncio.Event()
        # Monotonic time of the last message (event or reply) this connection received. Anything at
        # all proves the game is alive, so the watchdog uses it to spot hung instances.
        self.last_live = time.monotonic()

    async def connect(self, timeout: float = 1.0) -> None:
        self._reader, self._writer = await asyncio.wait_for(
            asyncio.open_connection(self.host, self.port, limit=1 << 22), timeout
        )
        self._read_task = asyncio.create_task(self._read_loop())

    async def close(self) -> None:
        if self._writer:
            self._writer.close()
        if self._read_task:
            self._read_task.cancel()

    async def request(self, cmd: str, timeout: float = 30.0, **args: Any) -> dict[str, Any]:
        if self._writer is None:
            raise RuntimeError("not connected")
        msg_id = next(self._ids)
        fut: asyncio.Future[dict[str, Any]] = asyncio.get_running_loop().create_future()
        self._pending[msg_id] = fut
        payload = {"id": msg_id, "cmd": cmd, **{k: v for k, v in args.items() if v is not None}}
        self._writer.write((json.dumps(payload) + "\n").encode())
        await self._writer.drain()
        try:
            reply = await asyncio.wait_for(fut, timeout)
        finally:
            self._pending.pop(msg_id, None)
        if not reply.get("ok"):
            raise GameError(reply.get("error", "unknown error"))
        return reply.get("data") or {}

    async def _read_loop(self) -> None:
        assert self._reader is not None
        try:
            while line := await self._reader.readline():
                try:
                    msg = json.loads(line)
                except json.JSONDecodeError:
                    log.warning("port %s: bad json from game: %r", self.port, line[:200])
                    continue
                self.last_live = time.monotonic()
                if "event" in msg:
                    await self.events.put(msg)
                elif (fut := self._pending.get(msg.get("id"))) and not fut.done():
                    fut.set_result(msg)
        except (ConnectionError, asyncio.IncompleteReadError):
            pass
        finally:
            self.closed.set()
            for fut in self._pending.values():
                if not fut.done():
                    fut.set_exception(ConnectionError("game connection closed"))


async def discover(base_port: int = 8765, count: int = 8, skip: set[int] | None = None) -> list[GameConnection]:
    """Connect to every running game instance (each binds the first free port from base_port)."""

    async def try_port(port: int) -> GameConnection | None:
        conn = GameConnection(port)
        try:
            await conn.connect(timeout=0.5)
            return conn
        except (OSError, asyncio.TimeoutError):
            return None

    ports = [p for p in range(base_port, base_port + count) if p not in (skip or set())]
    results = await asyncio.gather(*(try_port(p) for p in ports))
    return [c for c in results if c is not None]
