#!/usr/bin/env python3
"""Offline tests for GTA11Y bridge client and MCP framing."""
from __future__ import annotations

import importlib.util
import json
import socket
import threading


def load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


CLI = load("tools/gta11y-cli.py", "gta11y_cli_test")
MCP = load("tools/mcp/gta11y_mcp.py", "gta11y_mcp_test")


def serve_once(listener):
    client, _ = listener.accept()
    with client, client.makefile("rwb") as stream:
        request = json.loads(stream.readline())
        assert request["cmd"] == "state"
        stream.write((json.dumps({"id": request["id"], "ok": True, "result": {"iteration": "test"}}) + "\n").encode())
        stream.flush()


def main():
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    thread = threading.Thread(target=serve_once, args=(listener,))
    thread.start()
    with CLI.Bridge(port=listener.getsockname()[1]) as bridge:
        assert bridge.call("state") == {"iteration": "test"}
    thread.join()
    listener.close()

    tools, error = MCP.handle("tools/list", {})
    assert error is None
    names = {tool["name"] for tool in tools["tools"]}
    assert {"gta11y_drive_state", "gta11y_shapecasts", "gta11y_log_write", "gta11y_sapi_speak"} <= names
    print("bridge offline tests passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
