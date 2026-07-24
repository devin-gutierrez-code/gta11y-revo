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

import argparse
import gzip
import json
import math
import os
import sys
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime
from pathlib import Path

TOOLS = Path(__file__).parent.resolve()
CACHE = TOOLS / "_cache"
OUT   = TOOLS.parent / "GTA" / "scripts" / "gta11y-map.json"
OUT_NODES = TOOLS.parent / "GTA" / "scripts" / "gta11y-nodes.json.gz"
OUT_JUNCTIONS = TOOLS.parent / "GTA" / "scripts" / "gta11y-junctions.json.gz"
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

def fetch(name, url, refresh=False):
    """Download `url` into CACHE/name if not already present (or `refresh` is
    set). Prints the cache file's age so a stale vendored snapshot is visible
    on every run instead of silently pinning the pipeline. Returns the bytes."""
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / name
    if path.exists() and not refresh:
        age_days = (datetime.now().timestamp() - path.stat().st_mtime) / 86400
        print(f"  cached {name} (age {age_days:.0f} days; --refresh to re-download)")
    else:
        print(f"  downloading {name} from {url}")
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "gta11y-build"})
            with urllib.request.urlopen(req, timeout=60) as r:
                data = r.read()
            path.write_bytes(data)
        except Exception as exc:
            print(f"  ERROR downloading {name}: {exc}")
            if path.exists():
                print(f"  keeping existing cached copy of {name}")
            else:
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
# Enrichment — CodeWalker-extracted paths.xml / junctions.xml (local files)
# ---------------------------------------------------------------------------
#
# paths.xml is the 3dsmax *source* scene the game compiled into YND; the
# DurtyFree dump above is the *compiled runtime* graph. Topology and lane
# counts stay DurtyFree-authoritative; paths.xml contributes only attributes
# the runtime dump lacks (street names, turn restrictions, road width, speed
# category, disabled/no-nav flags). Source nodes are joined onto graph nodes
# by position: paths.xml floats sit at most ~0.19 m from their YND-quantized
# runtime positions, so TOL1=0.35 m is generous without cross-matching
# neighbours (min node spacing is meters).
#
# IMPORTANT: the scene XML stores only NON-default attribute values. An
# absent attribute means "unknown/default", never an explicit zero.

# Extended node flags — must match ExtFlags in GTA/MapDb.cs.
EXT_FLAG_BITS = [
    ("Disabled",                 1 << 0),
    ("Dont Use For Navigation",  1 << 1),
    ("NoGps",                    1 << 2),
    ("Highway",                  1 << 3),
    ("Off Road",                 1 << 4),
    ("Water",                    1 << 5),
    ("Cannot Go Left",           1 << 6),
    ("Cannot Go Right",          1 << 7),
    ("Left Turns Only",          1 << 8),
    ("Slip Lane",                1 << 9),
    ("Indicate Keep Left",       1 << 10),
    ("Indicate Keep Right",      1 << 11),
    ("Special",                  1 << 12),
    ("No Big Vehicles",          1 << 13),
    ("Tunnel",                   1 << 14),
    ("GpsBothWays",              1 << 15),
    ("Block If No Lanes",        1 << 16),
]

# Link flag bits — must match link-flag consumers in GTA/MapDb.cs.
LINK_FLAG_BITS = [
    ("Narrowroad", 1 << 0),
    ("Shortcut",   1 << 1),
]


def _attr_bool(attrs, name):
    v = attrs.get(name)
    return v is not None and str(v).lower() in ("true", "1", "yes")


def parse_paths_xml(path):
    """Stream-parse the 138 MB scene XML. Returns
    (nodes: {guid: (x, y, z, attrs)}, links: [(guidA, guidB, attrs)])."""
    nodes = {}
    links = []
    skipped_links = 0
    for _event, elem in ET.iterparse(str(path), events=("end",)):
        if elem.tag != "object":
            continue
        cls = elem.get("class")
        if cls == "vehiclenode":
            pos = elem.find("./transform/node/position")
            if pos is not None:
                attrs = {a.get("name"): a.get("value")
                         for a in elem.iter("attribute")}
                nodes[elem.get("guid")] = (
                    float(pos.get("x")), float(pos.get("y")), float(pos.get("z")),
                    attrs)
        elif cls == "vehiclelink":
            refs = [r.get("guid") for r in elem.findall("./references/ref")]
            if len(refs) == 2:
                attrs = {a.get("name"): a.get("value")
                         for a in elem.iter("attribute")}
                links.append((refs[0], refs[1], attrs))
            else:
                skipped_links += 1
        # Clearing each finished <object> keeps iterparse memory flat; the
        # parent <objects> retains only empty husk elements.
        elem.clear()
    print(f"  paths.xml: {len(nodes)} vehiclenodes, {len(links)} vehiclelinks"
          f" ({skipped_links} links without 2 refs skipped)")
    return nodes, links


def build_spatial_hash(graph_nodes):
    """1 m-bin spatial hash over graph node positions: {(ix,iy): [idx]}."""
    h = {}
    for i, rec in enumerate(graph_nodes):
        p = rec["p"]
        key = (int(math.floor(p[0])), int(math.floor(p[1])))
        h.setdefault(key, []).append(i)
    return h


def nearest_graph_node(spatial, graph_nodes, x, y, z, tol):
    """Nearest graph node within 3D distance `tol` (tol <= 1.0 assumed:
    the 3x3 1 m-bin neighbourhood covers it). Returns (idx, dist) or (-1, None)."""
    bx, by = int(math.floor(x)), int(math.floor(y))
    best, best_dsq = -1, tol * tol
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for idx in spatial.get((bx + dx, by + dy), ()):
                p = graph_nodes[idx]["p"]
                dsq = (p[0] - x) ** 2 + (p[1] - y) ** 2 + (p[2] - z) ** 2
                if dsq < best_dsq:
                    best_dsq, best = dsq, idx
    return (best, math.sqrt(best_dsq)) if best >= 0 else (-1, None)


def enrich_graph(graph, src_nodes, src_links):
    """Join paths.xml node/link attributes onto the DurtyFree graph in place.
    Emits v2 fields: per-node e/s/v, per-link width + flags, street table.
    Returns (metrics dict, {src_guid: graph_idx})."""
    gnodes = graph["nodes"]
    spatial = build_spatial_hash(gnodes)

    streets = [""]
    street_to_idx = {"": 0}
    # Per-graph-node scalar provenance: keep the CLOSEST source node's street
    # and speed when several source nodes collapse onto one graph node
    # (compile-time merges); flag bits are OR-ed from all of them.
    scalar_dist = {}

    m = {
        "matched_t1": 0, "matched_t2": 0,
        "unmatched": 0, "unmatched_disabled": 0,
        "collisions": 0,
        "street_applied": 0, "speed_applied": 0, "ext_nodes": set(),
    }
    guid_to_idx = {}

    for guid, (x, y, z, attrs) in src_nodes.items():
        idx, dist = nearest_graph_node(spatial, gnodes, x, y, z, 0.35)
        if idx >= 0:
            m["matched_t1"] += 1
        else:
            idx, dist = nearest_graph_node(spatial, gnodes, x, y, z, 1.0)
            if idx >= 0:
                m["matched_t2"] += 1
            elif _attr_bool(attrs, "Disabled"):
                m["unmatched_disabled"] += 1
                continue
            else:
                m["unmatched"] += 1
                continue
        guid_to_idx[guid] = idx
        rec = gnodes[idx]

        ext = 0
        for name, bit in EXT_FLAG_BITS:
            if _attr_bool(attrs, name):
                ext |= bit
        if ext:
            if rec.get("e", 0):
                m["collisions"] += 1
            rec["e"] = rec.get("e", 0) | ext
            m["ext_nodes"].add(idx)

        closest_so_far = scalar_dist.get(idx)
        if closest_so_far is None or dist < closest_so_far:
            scalar_dist[idx] = dist
            street = (attrs.get("Streetname") or "").strip()
            if street:
                si = street_to_idx.get(street)
                if si is None:
                    si = len(streets)
                    streets.append(street)
                    street_to_idx[street] = si
                rec["s"] = si
            speed = attrs.get("Speed")
            if speed is not None:
                try:
                    rec["v"] = int(speed)
                except ValueError:
                    pass

    m["street_applied"] = sum(1 for r in gnodes if r.get("s", 0))
    m["speed_applied"] = sum(1 for r in gnodes if "v" in r)
    m["ext_nodes"] = len(m["ext_nodes"])

    # ---- Link join: attach width/flags to both directions A->B and B->A ----
    lm = {"both_resolved": 0, "partial": 0, "unresolved": 0,
          "width_links": 0, "graph_link_missing": 0,
          "lane_compared": 0, "lane_agree": 0}

    def _find_link(rec, target_idx):
        for l in rec["l"]:
            if l[0] == target_idx:
                return l
        return None

    for ga, gb, attrs in src_links:
        ia, ib = guid_to_idx.get(ga), guid_to_idx.get(gb)
        if ia is None and ib is None:
            lm["unresolved"] += 1
            continue
        if ia is None or ib is None:
            lm["partial"] += 1
            continue
        lm["both_resolved"] += 1

        width = 0
        w = attrs.get("Width")
        if w is not None:
            try:
                width = max(0, int(float(w)))
            except ValueError:
                width = 0
        lflags = 0
        for name, bit in LINK_FLAG_BITS:
            if _attr_bool(attrs, name):
                lflags |= bit

        found_any = False
        for src, dst in ((ia, ib), (ib, ia)):
            l = _find_link(gnodes[src], dst)
            if l is None:
                continue
            found_any = True
            if width or lflags:
                while len(l) < 5:
                    l.append(0)
                if width and not l[3]:
                    l[3] = min(255, width)
                l[4] |= lflags
                if width:
                    lm["width_links"] += 1
        if not found_any:
            lm["graph_link_missing"] += 1

        # Lane cross-validation (multiset compare — Lanes In/Out orientation
        # relative to the ref order is undocumented). DurtyFree stays
        # authoritative; this is only a divergence canary.
        li, lo = attrs.get("Lanes In"), attrs.get("Lanes Out")
        if li is not None or lo is not None:
            l = _find_link(gnodes[ia], ib)
            if l is not None:
                lm["lane_compared"] += 1
                try:
                    src_set = sorted([int(li) if li is not None else 1,
                                      int(lo) if lo is not None else 1])
                except ValueError:
                    src_set = None
                if src_set is not None and src_set == sorted([l[1], l[2]]):
                    lm["lane_agree"] += 1

    graph["version"] = 2
    graph["streets"] = streets
    m.update(lm)
    return m, guid_to_idx


def parse_junctions(path, graph, spatial):
    """Parse junctions.xml (CJunctionTemplateArray) and snap entrances onto
    graph nodes. Returns (junctions list, metrics)."""
    gnodes = graph["nodes"]
    tree = ET.parse(str(path))
    root = tree.getroot()
    out = []
    m = {"junctions": 0, "entrances": 0, "snapped": 0}

    def _vec(el):
        return [float(el.get("x")), float(el.get("y")), float(el.get("z"))]

    for item in root.findall("./Entries/Item"):
        jmin = _vec(item.find("vJunctionMin"))
        jmax = _vec(item.find("vJunctionMax"))
        num_entr = int(item.find("iNumEntrances").get("value"))
        phases = int(item.find("iNumPhases").get("value"))
        lights_el = item.find("iNumTrafficLightLocations")
        lights = int(lights_el.get("value")) if lights_el is not None else 0
        flags_el = item.find("iFlags")
        jflags = int(flags_el.get("value")) if flags_el is not None else 0

        jp = []
        jp_el = item.find("vJunctionNodePositions")
        if jp_el is not None and jp_el.text:
            vals = jp_el.text.split()
            for i in range(0, len(vals) - 2, 3):
                v = [float(vals[i]), float(vals[i + 1]), float(vals[i + 2])]
                if v != [0.0, 0.0, 0.0]:
                    jp.append([round(c, 2) for c in v])

        entrances = []
        for e in item.findall("./Entrances/Item")[:max(0, num_entr)]:
            p = _vec(e.find("vNodePosition"))
            if p == [0.0, 0.0, 0.0]:
                continue  # padding rows in the fixed-size array
            # Exact snap first; 1.0 m fallback for entrances that sit slightly
            # off a node. Beyond that leave ni=-1 — the runtime arms on raw
            # entrance position + travel-orientation alignment, and a wrong
            # node (cross street) is worse than no node.
            ni, _d = nearest_graph_node(spatial, gnodes, p[0], p[1], p[2], 0.35)
            if ni < 0:
                ni, _d = nearest_graph_node(spatial, gnodes, p[0], p[1], p[2], 1.0)
            m["entrances"] += 1
            if ni >= 0:
                m["snapped"] += 1
            entrances.append({
                "p": [round(c, 2) for c in p],
                "ni": ni,
                "ph": int(e.find("iPhase").get("value")),
                "sd": round(float(e.find("fStoppingDistance").get("value")), 2),
                "o": round(float(e.find("fOrientation").get("value")), 4),
                "rr": 1 if e.find("bCanTurnRightOnRedLight").get("value") == "true" else 0,
                "la": 1 if e.find("bLeftLaneIsAheadOnly").get("value") == "true" else 0,
                "rl": 1 if e.find("bRightLaneIsRightOnly").get("value") == "true" else 0,
                "lf": int(e.find("iLeftFilterLanePhase").get("value")),
            })
        if not entrances:
            continue
        out.append({
            "min": [round(c, 2) for c in jmin],
            "max": [round(c, 2) for c in jmax],
            "jp": jp,
            "phases": phases,
            "lights": lights,
            "flags": jflags,
            "e": entrances,
        })
        m["junctions"] += 1
    return out, m


def verify_del_perro(graph):
    """--verify: reproduce the extraction report's ground truth — a continuous
    DelPFwy 4-lane run at X in [-910, 0], Y in [-545, -485]."""
    gnodes = graph["nodes"]
    streets = graph.get("streets", [""])
    hits = []
    for i, rec in enumerate(gnodes):
        x, y, _z = rec["p"]
        if -910 <= x <= 0 and -545 <= y <= -485:
            si = rec.get("s", 0)
            if si and streets[si] == "DelPFwy":
                max_lanes = max((max(l[1], l[2]) for l in rec["l"]), default=0)
                width = max((l[3] if len(l) > 3 else 0 for l in rec["l"]), default=0)
                hits.append((x, y, rec["p"][2], max_lanes, width))
    hits.sort()
    print(f"  verify: {len(hits)} DelPFwy nodes in the Del Perro box")
    for x, y, z, lanes, width in hits[:12]:
        print(f"    ({x:8.1f},{y:8.1f},{z:6.1f}) lanes={lanes} widthM={width}")
    four_lane = sum(1 for h in hits if h[3] >= 4)
    print(f"  verify: {four_lane}/{len(hits)} nodes carry a >=4-lane link "
          f"(report expects a continuous 4-lane run)")
    ok = len(hits) >= 10 and four_lane > 0
    print(f"  verify: {'PASS' if ok else 'FAIL'}")
    return ok


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description="gta11y map-data builder")
    ap.add_argument("--paths-xml", type=Path,
                    default=TOOLS.parent / "navmesh_exports" / "paths.xml",
                    help="CodeWalker-extracted vehicle path scene XML")
    ap.add_argument("--junctions-xml", type=Path,
                    default=TOOLS.parent / "navmesh_exports" / "junctions.xml",
                    help="CodeWalker-extracted CJunctionTemplateArray XML")
    ap.add_argument("--refresh", action="store_true",
                    help="re-download the upstream sources instead of using tools/_cache")
    ap.add_argument("--strict", action="store_true",
                    help="exit nonzero when the node join rate is below 90%%")
    ap.add_argument("--verify", action="store_true",
                    help="dump Del Perro Fwy ground-truth box after building")
    args = ap.parse_args()

    print("gta11y map-data builder")
    print(f"  cache:  {CACHE}")
    print(f"  output: {OUT}")

    classification = json.loads(CLASSIFICATION.read_text(encoding="utf-8"))

    street_bytes = fetch("street.geojson", SOURCES["street.geojson"], args.refresh)
    gas_bytes    = fetch("worldGasPumps.json", SOURCES["worldGasPumps.json"], args.refresh)
    gar_bytes    = fetch("garages.json", SOURCES["garages.json"], args.refresh)
    # nodes.zip is downloaded and consumed in-place; fetch() caches it.
    fetch("nodes.zip", SOURCES["nodes.zip"], args.refresh)

    highways = build_highways(street_bytes, classification)
    services = build_services(gas_bytes, gar_bytes)
    graph    = build_node_graph(CACHE / "nodes.zip")
    graph["version"] = 2  # schema v2 even when enrichment inputs are absent

    # ---- Enrichment from the local CodeWalker extraction -------------------
    join_ok = True
    junctions = []
    if args.paths_xml.exists():
        print(f"  enriching from {args.paths_xml}")
        src_nodes, src_links = parse_paths_xml(args.paths_xml)
        em, _guid_to_idx = enrich_graph(graph, src_nodes, src_links)

        # Headline metric = graph coverage: how many RUNTIME nodes received a
        # source match. Source nodes with no runtime counterpart were merged
        # or dropped by the YND compiler and are expected losses.
        covered = len({i for i in _guid_to_idx.values()})
        cov_rate = covered / graph["n"] * 100.0 if graph["n"] else 0.0
        print(f"  graph coverage: {covered}/{graph['n']} runtime nodes matched "
              f"({cov_rate:.1f}%)")
        print(f"  source join: {em['matched_t1']} @0.35m, {em['matched_t2']} @1.0m, "
              f"{em['unmatched']} unmatched (compiled out), "
              f"{em['unmatched_disabled']} unmatched-Disabled")
        print(f"  node attrs: street={em['street_applied']} speed={em['speed_applied']} "
              f"extFlagNodes={em['ext_nodes']} collisions={em['collisions']} "
              f"streetTable={len(graph['streets'])}")
        print(f"  link join: both={em['both_resolved']} partial={em['partial']} "
              f"unresolved={em['unresolved']} graphLinkMissing={em['graph_link_missing']} "
              f"widthApplied={em['width_links']}")
        agree = (em["lane_agree"] / em["lane_compared"] * 100.0) if em["lane_compared"] else 0.0
        print(f"  lane canary: {em['lane_agree']}/{em['lane_compared']} agree ({agree:.1f}%) "
              f"— DurtyFree stays authoritative")
        if cov_rate < 90.0:
            print(f"  WARNING: graph coverage {cov_rate:.1f}% is below the 90% target")
            join_ok = False
    else:
        print(f"  WARNING: {args.paths_xml} not found — emitting v2 with NO enrichment")

    if args.junctions_xml.exists():
        spatial = build_spatial_hash(graph["nodes"])
        junctions, jm = parse_junctions(args.junctions_xml, graph, spatial)
        snap = (jm["snapped"] / jm["entrances"] * 100.0) if jm["entrances"] else 0.0
        print(f"  junctions: {jm['junctions']} kept, {jm['entrances']} entrances, "
              f"{jm['snapped']} snapped to graph nodes ({snap:.1f}%)")
    else:
        print(f"  WARNING: {args.junctions_xml} not found — no junctions file emitted")

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

    if junctions:
        jdump = {"version": 1, "n": len(junctions), "junctions": junctions}
        jjson = json.dumps(jdump, separators=(",", ":")).encode("utf-8")
        with gzip.open(OUT_JUNCTIONS, "wb", compresslevel=9) as f:
            f.write(jjson)
        print(f"  wrote {OUT_JUNCTIONS.name}: "
              f"{OUT_JUNCTIONS.stat().st_size / 1024.0:.1f} KB gzipped, "
              f"{len(junctions)} junctions")

    size_kb       = OUT.stat().st_size / 1024.0
    nodes_size_kb = OUT_NODES.stat().st_size / 1024.0
    print(f"  wrote {OUT.name}: {size_kb:.1f} KB, {len(highways)} highways, {len(services)} services")
    print(f"  wrote {OUT_NODES.name}: {nodes_size_kb:.1f} KB gzipped, {graph['n']} nodes (schema v{graph['version']})")

    if args.verify:
        if not verify_del_perro(graph):
            return 1
    if args.strict and not join_ok:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
