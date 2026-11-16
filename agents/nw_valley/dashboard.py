"""A small live web page showing what every agent is thinking and doing (one panel each, plus the chat).

Served from the agent process at http://127.0.0.1:8780 (stdlib only). The page polls /state every second.
"""

from __future__ import annotations

import json
import shutil
import socketserver
import subprocess
import threading
import time
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

PAGE = """<!doctype html>
<html><head><meta charset="utf-8"><title>NobodyValley</title>
<style>
  body { margin: 0; padding: 10px; background: #1d1b26; color: #eee; font: 13px/1.35 -apple-system, sans-serif; }
  h1 { font-size: 14px; margin: 0 0 8px; color: #f6c86b; }
  .agent { background: #2b2838; border-radius: 6px; padding: 8px 10px; margin-bottom: 8px; border-left: 4px solid var(--c); }
  .name { font-weight: 600; color: var(--c); }
  .meta { color: #999; font-size: 11px; float: right; }
  .doing { margin-top: 3px; }
  .thought { margin-top: 3px; color: #cfcfe8; font-style: italic; }
  .plan { margin-top: 3px; color: #9fd3a8; font-size: 12px; }
  .result { margin-top: 3px; color: #aaa; font-size: 12px; }
  .result.failed { color: #e99; }
  #chat { background: #2b2838; border-radius: 6px; padding: 8px 10px; }
  #chat div { margin: 2px 0; }
  #chat .who { font-weight: 600; }
  #chat .t { color: #777; font-size: 11px; margin-right: 4px; }
</style></head>
<body>
<h1 id="title">NobodyValley</h1>
<div id="agents"></div>
<h1>Chat</h1>
<div id="chat"></div>
<script>
const colors = ["#5fd0e0", "#d08ae8", "#e8c75f", "#7fd67f", "#7f9fe8", "#e87f7f"];
const esc = s => (s || "").replace(/[&<>]/g, c => ({"&": "&amp;", "<": "&lt;", ">": "&gt;"}[c]));
async function refresh() {
  let s;
  try { s = await (await fetch("/state")).json(); } catch (e) { return; }
  document.getElementById("title").textContent = s.world ? "NobodyValley - " + s.world : "NobodyValley";
  const names = Object.keys(s.agents);
  const color = n => colors[names.indexOf(n) % colors.length];
  document.getElementById("agents").innerHTML = names.map(n => {
    const a = s.agents[n];
    return `<div class="agent" style="--c:${color(n)}">
      <span class="name">${esc(n)}</span><span class="meta">${esc(a.where)}</span>
      <div class="doing">${esc(a.doing || "idle")}</div>
      ${a.thought ? `<div class="thought">"${esc(a.thought)}"</div>` : ""}
      ${a.plan ? `<div class="plan">Plan: ${esc(a.plan)}</div>` : ""}
      ${a.result ? `<div class="result ${a.result_ok ? "" : "failed"}">${esc(a.result)}</div>` : ""}
    </div>`;
  }).join("");
  document.getElementById("chat").innerHTML = s.chat.slice().reverse().map(m =>
    `<div><span class="t">${esc(m.time)}</span><span class="who" style="color:${color(m.from)}">${esc(m.from)}:</span> ${esc(m.text)}</div>`
  ).join("") || "<div style='color:#777'>(quiet)</div>";
}
refresh(); setInterval(refresh, 1000);
</script>
</body></html>
"""


class _Server(ThreadingHTTPServer):
    daemon_threads = True

    def server_bind(self) -> None:
        # HTTPServer.server_bind() calls socket.getfqdn(), which can take ~30s on macOS.
        socketserver.TCPServer.server_bind(self)
        self.server_name, self.server_port = "127.0.0.1", self.server_address[1]


class Dashboard:
    def __init__(self, port: int = 8780):
        self.port = port
        self.lock = threading.Lock()
        self.agents: dict[str, dict[str, Any]] = {}
        self.chat: deque[dict[str, str]] = deque(maxlen=40)
        self.world = ""

    @property
    def url(self) -> str:
        return f"http://127.0.0.1:{self.port}/"

    # ---------------------------------------------------------------- updates (called from the agents)

    def update(self, name: str, **fields: Any) -> None:
        with self.lock:
            self.agents.setdefault(name, {}).update(fields)

    def said(self, name: str, text: str) -> None:
        with self.lock:
            self.chat.append({"from": name, "text": text, "time": time.strftime("%H:%M")})

    def set_world(self, text: str) -> None:
        with self.lock:
            self.world = text

    def state(self) -> dict[str, Any]:
        with self.lock:
            return {"agents": dict(self.agents), "chat": list(self.chat), "world": self.world}

    # ---------------------------------------------------------------- serving

    def start(self) -> None:
        dashboard = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self) -> None:  # noqa: N802
                if self.path.startswith("/state"):
                    body, kind = json.dumps(dashboard.state()).encode(), "application/json"
                else:
                    body, kind = PAGE.encode(), "text/html; charset=utf-8"
                self.send_response(200)
                self.send_header("Content-Type", kind)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args: Any) -> None:
                pass

        server = _Server(("127.0.0.1", self.port), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()

    def open_window(self, geometry: str | None) -> None:
        """Open the dashboard in its own browser window; geometry is "x,y,w,h" in screen points."""
        chrome = Path("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome")
        profile = Path.home() / ".cache" / "nw-valley-dashboard"
        if chrome.exists():
            # Close a dashboard window left over from a previous run (it uses its own Chrome profile).
            subprocess.run(["pkill", "-f", "--", f"user-data-dir={profile}"], check=False)
            time.sleep(0.5)
            args = [str(chrome), f"--app={self.url}", f"--user-data-dir={profile}",
                    "--no-first-run", "--no-default-browser-check"]
            if geometry:
                x, y, w, h = geometry.split(",")
                args += [f"--window-position={x},{y}", f"--window-size={w},{h}"]
            subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        elif shutil.which("open"):
            subprocess.Popen(["open", self.url])
