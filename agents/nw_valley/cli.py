"""Send commands to one game instance by hand, for testing the mod without an LLM.

    uv run python -m nw_valley.cli observe
    uv run python -m nw_valley.cli till count=5
    uv run python -m nw_valley.cli --port 8766 go_to target=Saloon

Job commands wait for the job to finish and print the result.
"""

from __future__ import annotations

import argparse
import asyncio
import json

from .bridge import GameConnection, GameError


async def amain(port: int, cmd: str, args: list[str], timeout: float) -> None:
    conn = GameConnection(port)
    await conn.connect()
    kwargs: dict[str, object] = {}
    for a in args:
        k, _, v = a.partition("=")
        kwargs[k] = int(v) if v.isdigit() else v
    try:
        data = await conn.request(cmd, **kwargs)
    except GameError as e:
        print(f"error: {e}")
        return
    print(json.dumps(data, indent=2))
    if job_id := data.get("job_id"):
        while True:
            ev = await asyncio.wait_for(conn.events.get(), timeout)
            if ev["event"] == "job_done" and ev.get("job_id") == job_id:
                print(f"{'OK' if ev['ok'] else 'FAILED'}: {ev['summary']}")
                break
            print(f"(event: {ev})")
    await conn.close()


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--port", type=int, default=8765)
    p.add_argument("--timeout", type=float, default=300)
    p.add_argument("cmd")
    p.add_argument("args", nargs="*", help="key=value")
    a = p.parse_args()
    asyncio.run(amain(a.port, a.cmd, a.args, a.timeout))


if __name__ == "__main__":
    main()
