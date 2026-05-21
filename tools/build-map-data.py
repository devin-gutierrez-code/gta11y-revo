#!/usr/bin/env python3
"""
build-map-data.py — produces GTA/scripts/gta11y-map.json for the gta11y mod.

Downloads two community datasets (cached on disk under tools/_cache/), applies the
hand-curated road classification in tools/road-classification.json, simplifies the
geometry, and emits a single compact JSON consumed at runtime by the MapDb class.

Re-run whenever the upstream community data changes or when you edit the
classification table.

Sources (each redrawn into our own schema so the mod ships under its own license):
- Foxxite/GTAV-Geo-Json      street.geojson — named road polygons, game coords.
- DurtyFree/gta-v-data-dumps worldGasPumps.json + garages.json — POI coords.

Output schema (see also: MapDb class in GTA/GTA11Y.cs):
{
  "version": 1,
  "highways": [{"name": str, "type": "freeway|highway|surface|alley", "polygon": [[x, y], ...]}],
  "services": [{"kind": "gas|garage|lot", "x": f, "y": f, "z": f, "name": str}]
}
"""

import gzip
import json
import os
import sys
import urllib.request
import zipfile
from pathlib import Path

TOOLS = Path(__file__).parent.resolve()
CACHE = TOOLS / "_cache"
OUT   = TOOLS.parent / "GTA" / "scripts" / "gta11y-map.json"
OUT_NODES = TOOLS.parent / "GTA" / "scripts" / "gta11y-nodes.json.gz"
CLASSIFICATION = TOOLS / "road-classification.json"

SOURCES = {
    "street.geojson":
        "https://raw.githubusercontent.com/Foxxite/GTAV-Geo-Json/master/street.geojson",
    "worldGasPumps.json":
        "https://raw.githubusercontent.com/DurtyFree/gta-v-data-dumps/master/objectslocations/worldGasPumps.json",
    "garages.json":
        "https://raw.githubusercontent.com/DurtyFree/gta-v-data-dumps/master/garages.json",
    "nodes.zip":
        "https://github.com/DurtyFree/gta-v-data-dumps/raw/master/nodes.zip",
}


# ---------------------------------------------------------------------------
# Source fetching (cached)
# ---------------------------------------------------------------------------

def fetch(name, url):
    """Download `url` into CACHE/name if not already present. Returns the bytes."""
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / name
    if not path.exists():
        print(f"  downloading {name} from {url}")
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "gta11y-build"})
            with urllib.request.urlopen(req, timeout=60) as r:
                path.write_bytes(r.read())
        except Exception as exc:
            print(f"  ERROR downloading {name}: {exc}")
            raise
    return path.read_bytes()


# ---------------------------------------------------------------------------
# Geometry: Douglas-Peucker simplification
# ---------------------------------------------------------------------------

def _perp_distance(p, a, b):
    """Perpendicular distance from p to the line segment (a, b)."""
    ax, ay = a
    bx, by = b
    px, py = p
    dx, dy = bx - ax, by - ay
    if dx == 0 and dy == 0:
        return ((px - ax) ** 2 + (py - ay) ** 2) ** 0.5
    # Perpendicular distance using 2D cross product / line length.
    return abs(dx * (ay - py) - (ax - px) * dy) / ((dx * dx + dy * dy) ** 0.5)


def simplify(points, tolerance):
    """Iterative Douglas-Peucker. Input: [[x,y], ...]. Returns simplified copy."""
    if len(points) < 3:
        return list(points)
    # Find the point furthest from the chord between endpoints.
    dmax, idx = 0.0, 0
    for i in range(1, len(points) - 1):
        d = _perp_distance(points[i], points[0], points[-1])
        if d > dmax:
            dmax, idx = d, i
    if dmax > tolerance:
        left = simplify(points[: idx + 1], tolerance)
        right = simplify(points[idx:], tolerance)
        return left[:-1] + right
    return [points[0], points[-1]]


# ---------------------------------------------------------------------------
# Classification
# ---------------------------------------------------------------------------

def classify(name, overrides, rules, default_type):
    """Return the road-type string for a given road name."""
    if name in overrides:
        return overrides[name]
    nlow = name.lower().rstrip()
    for rule in rules:
        if nlow.endswith(" " + rule["suffix"].lower()) or nlow == rule["suffix"].lower():
            return rule["type"]
    return default_type


# ---------------------------------------------------------------------------
# Builders
# ---------------------------------------------------------------------------

def build_highways(street_geojson_bytes, classification):
    """Extract & classify road polygons from Foxxite's street.geojson."""
    data = json.loads(street_geojson_bytes)
    overrides = classification["overrides"]
    rules     = classification["rules"]
    default   = classification["default_type"]
    tolerance = classification.get("polygon_simplify_tolerance", 6.0)
    min_keep  = classification.get("min_polygon_vertices", 4)

    out = []
    skipped_short = 0
    for feat in data.get("features", []):
        props = feat.get("properties") or {}
        name  = (props.get("name") or "").strip()
        if not name:
            continue
        geom = feat.get("geometry") or {}
        gtype = geom.get("type")
        # Tolerate Polygon (one ring), MultiPolygon, or LineString.
        rings = []
        if gtype == "Polygon":
            rings = geom.get("coordinates") or []
        elif gtype == "MultiPolygon":
            for poly in (geom.get("coordinates") or []):
                rings.extend(poly)
        elif gtype == "LineString":
            rings = [geom.get("coordinates") or []]
        else:
            continue

        rtype = classify(name, overrides, rules, default)

        for ring in rings:
            if not ring:
                continue
            # Round to 1 decimal — 0.1 m precision is plenty for point-in-polygon
            # over a city-sized map and shrinks the JSON significantly.
            ring_2d = [[round(p[0], 1), round(p[1], 1)] for p in ring if len(p) >= 2]
            simp = simplify(ring_2d, tolerance)
            if len(simp) < min_keep:
                skipped_short += 1
                continue
            out.append({"name": name, "type": rtype, "polygon": simp})

    print(f"  highways: {len(out)} polygons emitted ({skipped_short} too-short polygons dropped)")
    return out


def build_services(gaspumps_bytes, garages_bytes):
    """Flatten POI dumps into a single services list."""
    out = []

    # Gas pumps
    try:
        gas = json.loads(gaspumps_bytes)
        # DurtyFree's worldGasPumps.json is a list of objects, each with a
        # "position" (or similar) field. Schema varies — accept either {x,y,z}
        # or {position: {x,y,z}}, plus optional {name} or {hash}/{model}.
        for entry in gas:
            pos = entry.get("Position") or entry.get("position") or entry
            if not isinstance(pos, dict):
                continue
            x = pos.get("X", pos.get("x"))
            y = pos.get("Y", pos.get("y"))
            z = pos.get("Z", pos.get("z"))
            if x is None or y is None or z is None:
                continue
            # Most DurtyFree gas-pump records carry a prop hash like
            # "prop_gas_pump_old2" — useless to a screen reader. The actual GTA V
            # station chain (Globe Oil / Xero / Ron / LTD) isn't tracked here, so
            # we collapse to a generic readable name.
            raw_name = entry.get("Name") or entry.get("name") or ""
            if not raw_name or raw_name.startswith("prop_") or raw_name.startswith("hash"):
                raw_name = "Gas station"
            out.append({
                "kind": "gas",
                "x": round(float(x), 1),
                "y": round(float(y), 1),
                "z": round(float(z), 1),
                "name": raw_name,
            })
    except Exception as exc:
        print(f"  WARN: failed to parse worldGasPumps.json: {exc}")

    # Garages
    try:
        garages = json.loads(garages_bytes)
        # Some DurtyFree dumps are dict-of-objects keyed by id; tolerate either.
        if isinstance(garages, dict):
            garages = list(garages.values())
        for entry in garages:
            if not isinstance(entry, dict):
                continue
            pos = entry.get("Position") or entry.get("position") or {}
            x = pos.get("X", pos.get("x"))
            y = pos.get("Y", pos.get("y"))
            z = pos.get("Z", pos.get("z"))
            if x is None or y is None or z is None:
                # Some garages give entry/exit door coords instead — try common names.
                door = entry.get("EntryPoint") or entry.get("Entry") or entry.get("Door1")
                if isinstance(door, dict):
                    x = door.get("X", door.get("x"))
                    y = door.get("Y", door.get("y"))
                    z = door.get("Z", door.get("z"))
            if x is None or y is None or z is None:
                continue
            out.append({
                "kind": "garage",
                "x": round(float(x), 1),
                "y": round(float(y), 1),
                "z": round(float(z), 1),
                "name": entry.get("Name") or entry.get("name") or "Garage",
            })
    except Exception as exc:
        print(f"  WARN: failed to parse garages.json: {exc}")

    gas_count = sum(1 for s in out if s["kind"] == "gas")
    gar_count = sum(1 for s in out if s["kind"] == "garage")
    print(f"  services: {gas_count} gas + {gar_count} garage = {len(out)} total")
    return out


# ---------------------------------------------------------------------------
# Node-graph builder — DurtyFree nodes.zip -> compact gzipped JSON
# ---------------------------------------------------------------------------
#
# Upstream schema (DurtyFree/gta-v-data-dumps, nodes.zip):
#   [{"AreaId": int, "Nodes": [{
#       "Id": int (per-cell, NOT globally unique),
#       "Position": {"X": f, "Y": f, "Z": f},
#       "IsValidForGps", "IsJunction", "IsFreeway", "IsGravelRoad",
#       "IsBackroad", "IsOnWater", "IsPedCrossway", "TrafficlightExists",
#       "LeftTurnNoReturn", "RightTurnNoReturn": bool,
#       "ConnectedNodes": [{"Node": {"Id": int, "Position": {...},
#                                    "ConnectedNodes": null, ...},
#                           "LaneCountForward": int, "LaneCountBackward": int}]
#     }]}, ...]
#
# Output schema (consumed by GTA.MapDb.NodeGraph at runtime):
#   {"version": 1, "n": <count>,
#    "nodes": [{"p":[x,y,z], "f":<flag bits>,
#               "l":[[<targetIdx>, <fwdLanes>, <bwdLanes>], ...]}, ...]}
#
# Flag bits (matches NodeGraph.NodeFlags in MapDb.cs):
#   bit0 ValidForGps   bit1 Junction      bit2 Freeway     bit3 GravelRoad
#   bit4 Backroad      bit5 OnWater       bit6 PedCrossway bit7 TrafficLight
#   bit8 LeftTurnNoReturn   bit9 RightTurnNoReturn
#
# Connection resolution: connected nodes carry only the target's per-cell Id,
# not its AreaId, so the per-cell Id is ambiguous. We resolve by POSITION
# instead (rounded to 0.01 m, which is far finer than the YND quantisation
# of 0.25 m for X/Y and 0.125 m for Z) — every node has its full XYZ inline.

NODE_FLAG_BITS = [
    ("IsValidForGps",      1 << 0),
    ("IsJunction",         1 << 1),
    ("IsFreeway",          1 << 2),
    ("IsGravelRoad",       1 << 3),
    ("IsBackroad",         1 << 4),
    ("IsOnWater",          1 << 5),
    ("IsPedCrossway",      1 << 6),
    ("TrafficlightExists", 1 << 7),
    ("LeftTurnNoReturn",   1 << 8),
    ("RightTurnNoReturn",  1 << 9),
]


def _pos_key(x, y, z):
    """Quantise to 0.01 m for exact-match position lookup across the dump."""
    return (round(x * 100), round(y * 100), round(z * 100))


def build_node_graph(nodes_zip_path):
    """Convert DurtyFree nodes.zip into the compact runtime format."""
    with zipfile.ZipFile(nodes_zip_path) as z:
        with z.open("nodes.json") as f:
            cells = json.load(f)

    # Pass 1: flatten all nodes, assign a stable index, build position lookup.
    flat = []
    pos_to_idx = {}
    for cell in cells:
        for node in cell.get("Nodes", []):
            pos = node["Position"]
            x, y, z = float(pos["X"]), float(pos["Y"]), float(pos["Z"])
            key = _pos_key(x, y, z)
            # Duplicate positions across cells are rare but possible at cell
            # boundaries. Keep the first; later ones become unreachable nodes,
            # which is fine — the graph stays connected through whichever copy
            # was discovered first.
            if key in pos_to_idx:
                continue
            pos_to_idx[key] = len(flat)
            flags = 0
            for name, bit in NODE_FLAG_BITS:
                if node.get(name):
                    flags |= bit
            flat.append({
                "x": round(x, 2), "y": round(y, 2), "z": round(z, 2),
                "f": flags,
                "raw_links": node.get("ConnectedNodes") or [],
            })

    # Pass 2: resolve link targets to indices.
    resolved_links = 0
    dropped_links = 0
    for entry in flat:
        out_links = []
        for raw in entry["raw_links"]:
            tn = raw.get("Node")
            if not tn:
                dropped_links += 1
                continue
            tp = tn.get("Position") or {}
            tx, ty, tz = float(tp.get("X", 0)), float(tp.get("Y", 0)), float(tp.get("Z", 0))
            tkey = _pos_key(tx, ty, tz)
            tidx = pos_to_idx.get(tkey)
            if tidx is None:
                dropped_links += 1
                continue
            fwd = int(raw.get("LaneCountForward", 1))
            bwd = int(raw.get("LaneCountBackward", 1))
            out_links.append([tidx, fwd, bwd])
            resolved_links += 1
        entry["l"] = out_links
        del entry["raw_links"]

    # Pass 3: shape final records as {p, f, l}.
    out_nodes = [
        {"p": [e["x"], e["y"], e["z"]], "f": e["f"], "l": e["l"]}
        for e in flat
    ]
    print(f"  nodes: {len(out_nodes)} flat (from {sum(len(c['Nodes']) for c in cells)} raw)")
    print(f"  links: {resolved_links} resolved, {dropped_links} dropped (orphan targets)")
    return {"version": 1, "n": len(out_nodes), "nodes": out_nodes}


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    print("gta11y map-data builder")
    print(f"  cache:  {CACHE}")
    print(f"  output: {OUT}")

    classification = json.loads(CLASSIFICATION.read_text(encoding="utf-8"))

    street_bytes = fetch("street.geojson", SOURCES["street.geojson"])
    gas_bytes    = fetch("worldGasPumps.json", SOURCES["worldGasPumps.json"])
    gar_bytes    = fetch("garages.json", SOURCES["garages.json"])
    # nodes.zip is downloaded and consumed in-place; fetch() caches it.
    fetch("nodes.zip", SOURCES["nodes.zip"])

    highways = build_highways(street_bytes, classification)
    services = build_services(gas_bytes, gar_bytes)
    graph    = build_node_graph(CACHE / "nodes.zip")

    OUT.parent.mkdir(parents=True, exist_ok=True)
    output = {
        "version": 1,
        "highways": highways,
        "services": services,
    }
    OUT.write_text(json.dumps(output, separators=(",", ":")), encoding="utf-8")

    # The node graph is shipped separately and gzip-compressed — even a
    # compact JSON of 67k nodes with links is ~30 MB raw; gzip brings it to
    # ~5-6 MB which is fine to bundle with the mod.
    OUT_NODES.parent.mkdir(parents=True, exist_ok=True)
    nodes_json = json.dumps(graph, separators=(",", ":")).encode("utf-8")
    with gzip.open(OUT_NODES, "wb", compresslevel=9) as f:
        f.write(nodes_json)

    size_kb       = OUT.stat().st_size / 1024.0
    nodes_size_kb = OUT_NODES.stat().st_size / 1024.0
    print(f"  wrote {OUT.name}: {size_kb:.1f} KB, {len(highways)} highways, {len(services)} services")
    print(f"  wrote {OUT_NODES.name}: {nodes_size_kb:.1f} KB gzipped, {graph['n']} nodes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
