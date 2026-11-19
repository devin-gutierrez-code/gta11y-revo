#!/usr/bin/env python3
"""Dependency-free MCP stdio server for GTA11Y's diagnostic bridge."""
from __future__ import annotations

import base64
import importlib.util
import json
import os
import sys

TOOLS_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI_PATH = os.path.join(TOOLS_DIR, "gta11y-cli.py")
SPEC = importlib.util.spec_from_file_location("gta11y_cli", CLI_PATH)
CLI = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CLI)
HOST = os.environ.get("GTA11Y_BRIDGE_HOST", CLI.DEFAULT_HOST)
PORT = int(os.environ.get("GTA11Y_BRIDGE_PORT", CLI.DEFAULT_PORT))


def schema(properties=None, required=None):
    return {"type": "object", "properties": properties or {}, "required": required or [], "additionalProperties": False}


TOOLS = {
    "gta11y_ping": ("ping", "Confirm the loaded bridge process and whether the GTA V script tick is alive.", schema()),
    "gta11y_state": ("state", "Read combined mod, player, vehicle, drive-assist, and shapecast state.", schema()),
    "gta11y_mod_state": ("mod.state", "Read the loaded iteration, logger, screenshot, loading, and pause status.", schema()),
    "gta11y_player": ("player", "Read live player position, motion, health, wanted level, street, and zone.", schema()),
    "gta11y_vehicle": ("vehicle", "Read live current-vehicle pose, motion, health, and physical state.", schema()),
    "gta11y_drive_state": ("drive.state", "Read live drive-assist mode, decisions, controller outputs, recovery, and achieved controls.", schema()),
    "gta11y_shapecasts": ("shapecasts", "Read the live left, center, right, and rear drive-assist shapecast cache.", schema()),
    "gta11y_scan": ("scanner", "Read nearby vehicles, people, or objects from the game world.", schema({
        "kind": {"type": "string", "enum": ["vehicles", "people", "objects"]},
        "radius": {"type": "number", "minimum": 1, "maximum": 500},
        "max_results": {"type": "integer", "minimum": 1, "maximum": 50},
    })),
    "gta11y_input_controls": ("input.controls", "List controls accepted by the diagnostic input writer.", schema()),
    "gta11y_input_hold": ("input.hold", "Hold a game control for a bounded duration. Maximum 30 seconds; verify achieved readback.", schema({
        "control": {"type": "string"}, "value": {"type": "number", "minimum": -1, "maximum": 1},
        "ms": {"type": "integer", "minimum": 1, "maximum": 30000},
    }, ["control"])),
    "gta11y_input_release": ("input.release", "Release one bridge-held control, or all controls when omitted.", schema({"control": {"type": "string"}})),
    "gta11y_input_state": ("input.state", "Read bridge-held controls and the mod's achieved throttle, brake, and steering samples.", schema()),
    "gta11y_screenshot": ("screenshot", "Capture the current GTA V window and return the JPEG as an MCP image.", schema({"name": {"type": "string"}})),
    "gta11y_log_tail": ("log.tail", "Read the tail of the active drive-assist log.", schema({"count": {"type": "integer", "minimum": 1, "maximum": 400}})),
    "gta11y_log_write": ("log.write", "Write an attributed bridge-marker EVENT into the active drive-assist log. Pass 'tag' to also fire a failure snapshot, so the moment gets pre-roll frames and a contact sheet like a player F1 flag.", schema({
        "text": {"type": "string", "maxLength": 1000}, "source": {"type": "string"}, "tag": {"type": "string"},
    }, ["text"])),
    "gta11y_sapi_speak": ("speech.sapi", "Speak to the tester through a dedicated SAPI voice, separate from Tolk/NVDA output.", schema({"text": {"type": "string", "maxLength": 1000}}, ["text"])),
    "gta11y_speech_status": ("speech.status", "Read the dedicated bridge SAPI worker status.", schema()),
    # ---- iter-71 test harness ----
    # These change the game world and additionally need "allow_world_writes": true in
    # scripts/gta11y-bridge.json. Every one returns what it ACHIEVED, not what it was
    # asked for: the readback is how a tester who cannot see the screen verifies setup.
    "gta11y_settings_get": ("settings.get", "Read mod settings with their shipped default, whether they are a drive-assist gate, and whether that gate is instrumented. Omit 'id' and 'ids' to list all.", schema({
        "id": {"type": "string"}, "ids": {"type": "array", "items": {"type": "string"}},
    })),
    "gta11y_settings_set": ("settings.set", "Set one or many mod settings in a single batch. For a drive-assist gate this creates a clean mid-session epoch boundary (EVENT gate-toggle src=bridge plus a fresh gate-manifest) that triage-log.py splits on - use this instead of the in-game menu, which corrupts the experiment.", schema({
        "values": {"type": "object"}, "id": {"type": "string"}, "value": {"type": "integer"},
    })),
    "gta11y_world_locations": ("world.locations", "List the named test locations the drive-assist protocol keeps returning to.", schema()),
    "gta11y_world_teleport": ("world.teleport", "Place the player and their vehicle at a named location or explicit coordinates, optionally snapped to a road node and at a given speed. Returns the achieved pose - verify placement from that, not from a screenshot.", schema({
        "location": {"type": "string"}, "x": {"type": "number"}, "y": {"type": "number"}, "z": {"type": "number"},
        "heading": {"type": "number"}, "snap_to_road": {"type": "boolean"},
        "speed_mps": {"type": "number", "minimum": 0, "maximum": 80},
    })),
    "gta11y_world_conditions": ("world.conditions", "Set clock, weather and traffic/ped density so two runs of one scenario are comparable.", schema({
        "hour": {"type": "integer", "minimum": 0, "maximum": 23}, "minute": {"type": "integer", "minimum": 0, "maximum": 59},
        "weather": {"type": "string"}, "vehicle_density": {"type": "number", "minimum": 0, "maximum": 1},
        "ped_density": {"type": "number", "minimum": 0, "maximum": 1}, "clear_density": {"type": "boolean"},
    })),
    "gta11y_vehicle_spawn": ("vehicle.spawn", "Spawn a vehicle by model name at a location and warp the player into it. A cold model asks you to repeat the call rather than stalling the game thread.", schema({
        "model": {"type": "string"}, "location": {"type": "string"},
        "x": {"type": "number"}, "y": {"type": "number"}, "z": {"type": "number"}, "heading": {"type": "number"},
        "warp": {"type": "boolean"}, "delete_previous": {"type": "boolean"},
    }, ["model"])),
    "gta11y_vehicle_set": ("vehicle.set", "Set the current vehicle's body/engine/petrol-tank health, fire, dirt or speed directly - so damage telemetry can be exercised without repeatedly crashing a car.", schema({
        "repair": {"type": "boolean"}, "body_health": {"type": "number"}, "engine_health": {"type": "number"},
        "petrol_tank_health": {"type": "number"}, "dirt": {"type": "number"}, "on_fire": {"type": "boolean"},
        "speed_mps": {"type": "number", "minimum": 0, "maximum": 80},
    })),
    "gta11y_scenario_start": ("scenario.start", "Run a declarative test scenario. Steps: gates, teleport, vehicle, conditions, marker, hold, release, wait, wait_until, capture. One step per tick, non-blocking - poll gta11y_scenario_status.", schema({
        "name": {"type": "string"}, "steps": {"type": "array", "items": {"type": "object"}},
    }, ["steps"])),
    "gta11y_scenario_status": ("scenario.status", "Poll the running scenario: state, current step, elapsed time and the trace of completed steps.", schema()),
    "gta11y_scenario_abort": ("scenario.abort", "Stop the running scenario and release every bridge-held control.", schema()),
    "gta11y_commands": ("help", "List commands supported by the running build.", schema()),
    "gta11y_command": (None, "Send any diagnostic command by name.", {"type": "object", "properties": {"cmd": {"type": "string"}, "args": {"type": "object"}}, "required": ["cmd"], "additionalProperties": False}),
}


def bridge_call(command, arguments):
    with CLI.Bridge(HOST, PORT) as bridge:
        return bridge.call(command, arguments)


def call(command, arguments):
    try:
        result = bridge_call(command, arguments)
        content = [{"type": "text", "text": json.dumps(result, indent=2, ensure_ascii=False)}]
        if command == "screenshot" and isinstance(result, dict):
            path = result.get("path", "")
            if path and os.path.isfile(path):
                with open(path, "rb") as image:
                    content.append({"type": "image", "data": base64.b64encode(image.read()).decode("ascii"), "mimeType": "image/jpeg"})
        return content, False
    except CLI.BridgeError as error:
        return [{"type": "text", "text": f"GTA11Y bridge unavailable.\n\n{error}"}], True


def handle(method, params):
    if method == "initialize":
        return {"protocolVersion": params.get("protocolVersion", "2024-11-05"), "capabilities": {"tools": {}}, "serverInfo": {"name": "gta11y", "version": "1"}}, None
    if method in ("notifications/initialized", "initialized"):
        return None, None
    if method == "ping":
        return {}, None
    if method == "tools/list":
        return {"tools": [{"name": name, "description": desc, "inputSchema": input_schema} for name, (_, desc, input_schema) in TOOLS.items()]}, None
    if method == "tools/call":
        name = params.get("name", "")
        if name not in TOOLS:
            return None, {"code": -32602, "message": f"unknown tool {name!r}"}
        command = TOOLS[name][0]
        arguments = params.get("arguments") or {}
        if command is None:
            command = arguments.get("cmd", "")
            arguments = arguments.get("args") or {}
        content, is_error = call(command, arguments)
        return {"content": content, "isError": is_error}, None
    return None, {"code": -32601, "message": f"method not found: {method}"}


def main():
    for line in sys.stdin:
        try:
            request = json.loads(line)
            result, error = handle(request.get("method", ""), request.get("params") or {})
            if "id" not in request or (result is None and error is None):
                continue
            response = {"jsonrpc": "2.0", "id": request["id"]}
            response["error" if error else "result"] = error or result
        except Exception as exc:
            response = {"jsonrpc": "2.0", "id": None, "error": {"code": -32603, "message": str(exc)}}
        print(json.dumps(response, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
