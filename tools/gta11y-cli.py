#!/usr/bin/env python3
"""Plain-text client for GTA11Y's loopback diagnostic bridge."""
from __future__ import annotations

import argparse
import json
import socket
import sys

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 27738
DEFAULT_TIMEOUT = 10.0


class BridgeError(RuntimeError):
    pass


class Bridge:
    def __init__(self, host=DEFAULT_HOST, port=DEFAULT_PORT, timeout=DEFAULT_TIMEOUT):
        self.host, self.port, self.timeout = host, port, timeout
        self.sock = None
        self.buffer = b""
        self.next_id = 1

    def __enter__(self):
        try:
            self.sock = socket.create_connection((self.host, self.port), self.timeout)
            self.sock.settimeout(self.timeout)
        except OSError as error:
            raise BridgeError(
                f"could not reach GTA11Y at {self.host}:{self.port} ({error}). "
                "Confirm GTA V is running and scripts\\gta11y-bridge.json has enabled=true."
            ) from error
        return self

    def __exit__(self, *_):
        if self.sock:
            self.sock.close()

    def call(self, command, args=None):
        request = {"cmd": command, "args": args or {}, "id": self.next_id}
        self.next_id += 1
        self.sock.sendall((json.dumps(request) + "\n").encode("utf-8"))
        while b"\n" not in self.buffer:
            try:
                chunk = self.sock.recv(65536)
            except socket.timeout as error:
                raise BridgeError(f"timed out waiting for {command}") from error
            if not chunk:
                raise BridgeError("bridge closed the connection")
            self.buffer += chunk
        line, self.buffer = self.buffer.split(b"\n", 1)
        response = json.loads(line.decode("utf-8", errors="replace"))
        if not response.get("ok"):
            raise BridgeError(response.get("error", "command failed"))
        return response.get("result")


def parse_pairs(pairs):
    result = {}
    for pair in pairs:
        if "=" not in pair:
            raise SystemExit(f"argument {pair!r} must use key=value")
        key, value = pair.split("=", 1)
        try:
            result[key] = json.loads(value)
        except json.JSONDecodeError:
            result[key] = value
    return result


def main():
    parser = argparse.ArgumentParser(description="Talk to GTA11Y inside a running GTA V process.")
    parser.add_argument("command")
    parser.add_argument("args", nargs="*")
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT)
    options = parser.parse_args()
    try:
        with Bridge(options.host, options.port, options.timeout) as bridge:
            result = bridge.call(options.command, parse_pairs(options.args))
    except BridgeError as error:
        print(f"gta11y: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
