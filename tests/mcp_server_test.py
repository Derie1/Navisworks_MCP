"""
MCP-level test of the .NET server (navis_mcp_server.dll), no Navisworks needed
==============================================================================
Starts a fake bridge (a small HTTP server that answers like the plugin) on a free
port, runs the server against it over stdio and checks the MCP side:

    python tests/mcp_server_test.py [path\\to\\navis_mcp_server.dll]

Default DLL: bin\\Release\\ServerBuild\\net8.0\\navis_mcp_server.dll (dotnet build of
server\\NavisMcpServer). Checks initialize, tools/list against tools.json, routing and
argument renames, images, plugin errors, product check, unreachable bridge, and that
a slow call does not block other requests.
"""

import base64
import json
import os
import socket
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DLL = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "bin", "Release", "ServerBuild", "net8.0", "navis_mcp_server.dll")
TOOLS = json.load(open(os.path.join(ROOT, "server", "NavisMcpServer", "tools.json"), encoding="utf-8"))
PNG = base64.b64encode(b"\x89PNG\r\n\x1a\nfake").decode()

requests = []          # (method, path, body) the fake bridge received
product = {"value": "Navisworks"}
results = []


class FakeBridge(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _reply(self, code, obj):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        requests.append(("GET", self.path, None))
        if self.path == "/health":
            body = {"ok": True, "version": "9.9.9", "port": self.server.server_port, "routes": 59}
            if product["value"]:
                body["product"] = product["value"]
            return self._reply(200, body)
        self._reply(404, {"error": "not found"})

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(n) or b"{}")
        requests.append(("POST", self.path, body))
        if self.path == "/view/capture":
            return self._reply(200, {"width": 10, "height": 5, "image_png_base64": PNG})
        if self.path == "/model/open":
            return self._reply(500, {"error": "Exception", "detail": "file not found: C:/nope.nwf", "path": "/model/open"})
        if self.path == "/clash/run":
            time.sleep(2)
        self._reply(200, {"route": self.path, "body": body})


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


class Client:
    def __init__(self, port):
        env = dict(os.environ, NAVIS_MCP_PORT=str(port), NAVIS_MCP_TIMEOUT="5")
        self.p = subprocess.Popen(["dotnet", DLL], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, env=env)
        self.next_id = 0
        self.responses = {}
        self.order = []
        self.lock = threading.Condition()
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        for line in self.p.stdout:
            msg = json.loads(line.decode("utf-8"))
            with self.lock:
                self.responses[msg.get("id")] = msg
                self.order.append(msg.get("id"))
                self.lock.notify_all()

    def send(self, method, params=None):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            msg["params"] = params
        self.p.stdin.write((json.dumps(msg) + "\n").encode())
        self.p.stdin.flush()
        return self.next_id

    def wait(self, rid, timeout=20):
        with self.lock:
            self.lock.wait_for(lambda: rid in self.responses, timeout)
            return self.responses.get(rid)

    def call(self, method, params=None):
        return self.wait(self.send(method, params))

    def tool(self, name, args=None):
        return self.call("tools/call", {"name": name, "arguments": args or {}})["result"]

    def close(self):
        self.p.stdin.close()
        self.p.wait(10)


def check(name, ok, note=""):
    results.append((name, bool(ok)))
    print(f"[{'PASS' if ok else 'FAIL'}] {name:<48} {str(note)[:120]}")


def text(res):
    return next(c["text"] for c in res["content"] if c["type"] == "text")


def main():
    port = free_port()
    httpd = ThreadingHTTPServer(("127.0.0.1", port), FakeBridge)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    c = Client(port)

    init = c.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                 "clientInfo": {"name": "test", "version": "0"}})["result"]
    check("initialize: name navisworks, protocol echoed",
          init["serverInfo"]["name"] == "navisworks" and init["protocolVersion"] == "2025-06-18",
          init["serverInfo"])
    check("initialize: instructions mention AI companion", "AI companion" in init.get("instructions", ""))

    tools = c.call("tools/list")["result"]["tools"]
    names = [t["name"] for t in tools]
    check("tools/list: same tools as tools.json", names == [t["name"] for t in TOOLS["tools"]], f"{len(names)} tools")
    check("tools/list: no bridge table leaks", all(set(t) <= {"name", "description", "inputSchema"} for t in tools))

    r = c.tool("health_check")
    h = json.loads(text(r))
    check("health_check: plugin + server version", not r["isError"] and h["version"] == "9.9.9" and "server_version" in h, h)

    requests.clear()
    r = c.tool("run_clash_test", {"test_name": "T1"})
    sent = [q for q in requests if q[0] == "POST"]
    check("run_clash_test -> POST /clash/run {name}", sent and sent[-1][1] == "/clash/run" and sent[-1][2] == {"name": "T1"}, sent)

    r = c.tool("get_clash_results", {"test_name": "T1", "max_results": 5})
    check("get_clash_results renames test_name/max_results", requests[-1][2] == {"name": "T1", "limit": 5}, requests[-1][2])

    r = c.tool("add_clash_comment", {"test_name": "T1", "clash_names": ["Clash1"], "comment": "Прошу проверить"})
    check("non-ASCII arguments pass through", requests[-1][2]["comment"] == "Прошу проверить"
          and "Прошу проверить" in text(r), requests[-1][2])

    r = c.tool("capture_view", {"width": 10})
    kinds = [x["type"] for x in r["content"]]
    img = next((x for x in r["content"] if x["type"] == "image"), {})
    check("capture_view -> image content", kinds == ["text", "image"] and img.get("data") == PNG
          and img.get("mimeType") == "image/png" and "image_png_base64" not in text(r), kinds)

    r = c.tool("open_document", {"path": "C:/nope.nwf"})
    check("plugin error -> isError with detail", r["isError"] and "file not found" in text(r), text(r))

    err = c.call("tools/call", {"name": "no_such_tool", "arguments": {}})
    check("unknown tool -> JSON-RPC error", "error" in err and err["error"]["code"] == -32602, err.get("error"))

    # A slow call must not block a ping sent after it.
    slow = c.send("tools/call", {"name": "run_clash_test", "arguments": {}})
    time.sleep(0.2)
    ping = c.send("ping")
    c.wait(ping)
    c.wait(slow)
    check("slow call does not block ping", c.order.index(ping) < c.order.index(slow))
    c.close()

    # A new server process against a port served by something else.
    product["value"] = "AutoCAD"
    c = Client(port)
    c.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "0"}})
    r = c.tool("list_model_files")
    check("other product on the port -> isError", r["isError"] and "not by the Navisworks bridge" in text(r), text(r))
    c.close()

    # Plugins before 2.3 send no product: still accepted.
    product["value"] = None
    c = Client(port)
    c.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "0"}})
    r = c.tool("list_model_files")
    check("old plugin without product accepted", not r["isError"], text(r)[:80])
    c.close()

    httpd.shutdown()
    c = Client(free_port())
    c.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "0"}})
    r = c.tool("list_model_files")
    check("bridge not running -> isError with hint", r["isError"] and "AI companion" in text(r), text(r))
    c.close()

    passed = sum(ok for _, ok in results)
    print(f"\n{passed}/{len(results)} checks passed")
    sys.exit(0 if passed == len(results) else 1)


if __name__ == "__main__":
    main()
