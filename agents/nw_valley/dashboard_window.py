"""The dashboard in a native window (the system web view, via pywebview).

    python -m nw_valley.dashboard_window URL [X,Y,W,H]

A separate process, because pywebview needs the main thread (the agents' main thread runs asyncio).
It closes itself when the process that started it goes away.
"""

from __future__ import annotations

import os
import sys
import threading
import time

import webview


def _close_with_parent(window: webview.Window, parent: int) -> None:
    while os.getppid() == parent:
        time.sleep(1)
    window.destroy()


def main() -> None:
    url = sys.argv[1]
    x = y = None
    width, height = 360, 900
    if len(sys.argv) > 2 and sys.argv[2]:
        x, y, width, height = (int(v) for v in sys.argv[2].split(","))
    # x,y are global (from the top-left of the primary display, like the games' windows); pywebview
    # positions relative to `screen`, which defaults to the display that has the focus.
    window = webview.create_window("NobodyValley", url, x=x, y=y, width=width, height=height,
                                   screen=webview.screens[0], background_color="#1d1b26", text_select=True)
    threading.Thread(target=_close_with_parent, args=(window, os.getppid()), daemon=True).start()
    webview.start()


if __name__ == "__main__":
    main()
