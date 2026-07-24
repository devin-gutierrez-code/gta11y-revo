#!/usr/bin/env python3
"""Pin the unpinned settings rows from a calibration menulog.

The Menu Reader emits `EVENT pane-chain: pane=<screen> chain=<uid,uid,...>` when
the user leaves a settings pane (v1.3, GTA11Y.cs FlushPaneChain). This tool
monotone-aligns each observed runtime-uid chain against that pane's row order in
pausemenu.xml, so we can read off each row's TRUE runtime uniqueId on Devin's
game build without guessing the eMenuPref shift.

It also recovers the shift function s(header) = runtime_uid - header_value, whose
step points are the 4 preferences the build inserted after 2944.

v1.4 fixes (the 2026-07-20 calibration failed to align on ALL SIX panes and every
one of the three causes was a bug in here, not in the data):
  1. WRAP/ROTATION. A full pane walk wraps past the last row back to the first,
     and the walk rarely starts on row 0. `visible_order` treated the wrapped
     repeat as extra rows. `derotate()` now cuts each chain at its first repeat
     and rotates it so index 0 is the pane's true first row.
  2. "an unconditional row can never be skipped" was a HARD constraint and it is
     factually wrong on this build: pane 137 hides unconditional headers 63/64/87
     and pane 139 shows only 6 of 11 unconditional rows. It is now a soft
     preference used to rank otherwise-tied alignments.
  3. The offset window was a flat [0,4]. It is now the piecewise s(h) settled by
     that same calibration: h<=96 -> exactly 0, h>=108 -> exactly 4, and only the
     PREF_VOICE_* band (97..107) is free. That collapses the search enormously
     and is what makes panes 137/138/140 come out forced.
Panes are also matched against the GLOBAL pref pool rather than only their own
XML screen, because this build moved five rows between Graphics (137) and
Advanced Graphics (138) relative to the mirrored pausemenu.xml.

Usage:
    python tools/align-panes.py [menulog ...]        # newest log if omitted
    python tools/align-panes.py --write LOG          # also update overrides

Output (dry-run default): a suggested overrides `rows` block + the recovered
offset function, written to tools/_align_suggestions.json for human review.
Only forced (unambiguous) alignments are proposed as status=verified.
"""
import glob
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "_cache")
OVERRIDES = os.path.join(HERE, "menulabels-overrides.json")
SUGGEST = os.path.join(HERE, "_align_suggestions.json")
MODSETTINGS = os.path.join(
    os.path.expanduser("~"),
    "Documents", "Rockstar Games", "GTA V", "ModSettings")

# Piecewise shift s(header) settled by the 2026-07-20 calibration. Must stay in
# lockstep with build-menulabels.py's SHIFT / SHIFT_BOUNDARY / UNPINNED_*.
SHIFT = 4
SETTLED_LO_MAX = 96     # header <= 96  -> offset exactly 0
SETTLED_HI_MIN = 108    # header >= 108 -> offset exactly 4
# Runtime uid ranges implied by the above: [0,96] settled low, [101,111] the free
# PREF_VOICE_* band, [112,180] settled high.
FREE_UID_LO, FREE_UID_HI = 97, 111


def offsets_for_header(hv):
    """Allowed offsets for a build-2944 header value under the settled model."""
    if hv is None:
        return ()
    if hv <= SETTLED_LO_MAX:
        return (0,)
    if hv >= SETTLED_HI_MIN:
        return (SHIFT,)
    return (0, 1, 2, 3, 4)  # 97..107: the four insertions live in here


def deshift_settled(uid):
    """Runtime uid -> header value where s() is settled; None in the free band."""
    if uid <= SETTLED_LO_MAX:
        return uid
    if uid >= SETTLED_HI_MIN + SHIFT:
        return uid - SHIFT
    return None


def parse_pref_enum(hpp):
    prefs, val = {}, 0
    for m in re.finditer(r"^\s*(PREF_\w+)(?:\s*=\s*(-?\d+))?\s*,", hpp, re.M):
        if m.group(2) is not None:
            val = int(m.group(2))
        prefs[m.group(1)] = val
        val += 1
    return prefs


def text_of(elem, tag):
    c = elem.find(tag)
    return c.text.strip() if c is not None and c.text else None


def joaat(s):
    h = 0
    for c in s.lower():
        h = (h + ord(c)) & 0xFFFFFFFF
        h = (h + (h << 10)) & 0xFFFFFFFF
        h ^= h >> 6
    h = (h + (h << 3)) & 0xFFFFFFFF
    h ^= h >> 11
    h = (h + (h << 15)) & 0xFFFFFFFF
    return h - 0x100000000 if h >= 0x80000000 else h


def load_pane_rows():
    """screen_id -> ordered [(pref_name, header_val, conditional, gxt)]."""
    enum = {}
    with open(os.path.join(CACHE, "commands_hud.lua"), encoding="utf-8", errors="replace") as fh:
        for name, val in re.findall(r"(MENU_UNIQUE_ID_\w+)\s*=\s*(-?\d+)", fh.read()):
            enum[name] = int(val)
    with open(os.path.join(CACHE, "eMenuPref.hpp"), encoding="utf-8", errors="replace") as fh:
        prefs = parse_pref_enum(fh.read())
    root = ET.parse(os.path.join(CACHE, "pausemenu.xml")).getroot()
    panes = {}
    ms = root.find("MenuScreens")
    for wrap in (ms if ms is not None else []):
        sname = text_of(wrap, "MenuScreen")
        snum = enum.get(sname, joaat(sname) if sname else None)
        items = wrap.find("MenuItems")
        if items is None or snum is None:
            continue
        for item in items.findall("Item"):
            if text_of(item, "MenuUniqueId") != "MENU_UNIQUE_ID_SETTINGS_LIST":
                continue
            action = text_of(item, "MenuAction") or ""
            pref = text_of(item, "MenuPref")
            if "PREF_CHANGE" not in action or not pref:
                continue
            hv = prefs.get(pref)
            cond = (text_of(item, "Contexts") is not None) or (item.get("platform") is not None)
            panes.setdefault(snum, []).append((pref, hv, cond, text_of(item, "cTextId")))
    return panes, prefs


def parse_chains(paths):
    """pane -> list of observed uid sequences (one per pane-chain event)."""
    chains = {}
    rx = re.compile(r"EVENT pane-chain: pane=(-?\d+) chain=([\d,]+)")
    for p in paths:
        with open(p, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                m = rx.search(line)
                if not m:
                    continue
                pane = int(m.group(1))
                uids = [int(x) for x in m.group(2).split(",") if x != ""]
                if uids:
                    chains.setdefault(pane, []).append(uids)
    return chains


def first_appearance(sequences):
    order, seen = [], set()
    for seq in sequences:
        for u in seq:
            if u not in seen:
                seen.add(u)
                order.append(u)
    return order


def reconstruct_track(sequences):
    """Rebuild the pane's row track from every observed adjacency.

    A walk is a path on the pane's row cycle, so consecutive uids in the chains
    are cycle edges. Collecting them as an undirected graph and linearising it
    survives the oscillating walks (Audio was arrowed 9,8,9,8,9,8,... before the
    real sweep) that a naive cut-at-first-repeat mangles into two rows.

    Returns (track, closed) or (None, False) when the adjacency is not a simple
    path/cycle — e.g. because the pane-chain omits hashed rows and so fabricates
    an edge across them."""
    adj = {}
    for seq in sequences:
        for a, b in zip(seq, seq[1:]):
            if a == b:
                continue
            adj.setdefault(a, set()).add(b)
            adj.setdefault(b, set()).add(a)
    if not adj or any(len(v) > 2 for v in adj.values()):
        return None, False
    ends = [n for n in adj if len(adj[n]) == 1]
    if len(ends) == 1 or len(ends) > 2:
        return None, False
    closed = not ends
    start = ends[0] if ends else next(iter(sorted(adj)))
    track, prev, cur = [start], None, start
    while True:
        nxt = [x for x in adj[cur] if x != prev]
        if not nxt:
            break
        prev, cur = cur, nxt[0]
        if cur == start:
            break
        track.append(cur)
    if len(track) != len(adj):
        return None, False
    return track, closed


def is_subsequence(small, big):
    it = iter(big)
    return all(any(x == y for y in it) for x in small)


def root_track(track, closed, xml_uids):
    """Choose the rotation + direction of `track` that starts at the pane's row 0.

    The chain never records the row the highlight lands on at activation (no
    selection event fires for it), so `track` is an arbitrary rotation of the
    truth, in an arbitrary direction. pausemenu.xml is the arbiter: exactly one
    candidate should make the observed rows a subsequence of the XML row order.
    Ambiguity or disagreement means we must NOT claim an ord."""
    cands, seen = [], set()
    for seq in ([track, track[::-1]] if closed else [track, track[::-1]]):
        span = range(len(seq)) if closed else [0]
        for r in span:
            rot = tuple(seq[r:] + seq[:r])
            if rot in seen:
                continue
            seen.add(rot)
            filt = [u for u in rot if u in xml_uids]
            if filt and is_subsequence(filt, xml_uids):
                cands.append(list(rot))
    if len(cands) == 1:
        return cands[0], "rooted (unique fit against pausemenu.xml row order)"
    if not cands:
        return None, "no rotation matches the XML row order — ord not claimed"
    return None, f"{len(cands)} rotations fit the XML order — ord not claimed"


def visible_order(sequences, xml_uids):
    """(order, ord_trustworthy, note) for one pane's observed traversals."""
    track, closed = reconstruct_track(sequences)
    if track is None:
        return first_appearance(sequences), False, \
            "adjacency is not a simple track (hashed rows split it?) — ord not claimed"
    rooted, note = root_track(track, closed, xml_uids)
    if rooted is None:
        return first_appearance(sequences), False, note
    return rooted, True, note + (" [closed cycle]" if closed else " [open path]")


def resolve_settled(observed, val_to_name):
    """Deterministic uid -> pref for the bands where s(header) is settled.

    With s() pinned outside 97..107 no search is needed: the uid IS the header
    (<=96) or the header plus 4 (>=112). Returns (bindings, unresolved_uids)
    where each binding is (uid, pref, header, offset)."""
    bindings, free = [], []
    for u in observed:
        hv = deshift_settled(u)
        if hv is None:
            free.append(u)                  # 97..111: the PREF_VOICE_* band
            continue
        pref = val_to_name.get(hv)
        if pref is None:
            free.append(u)                  # uid outside the known enum entirely
            continue
        bindings.append((u, pref, hv, u - hv))
    return bindings, free


def align_pane(pane, rows, observed):
    """Subsequence alignment of observed runtime uids against a pane's XML rows.

    Used only for uids in the free 97..107 band now that s() is settled elsewhere.
    Constraints: observed uids map to distinct rows in visible order, and each
    offset = uid - header must lie in offsets_for_header(header).

    The v1.3 hard rule "an unconditional row can never be skipped" is GONE — it
    is false on this build (pane 137 hides unconditional headers 63/64/87; pane
    139 shows 6 of 11) and it was the sole reason all six panes failed to align.
    Skipping unconditional rows is now merely penalised, so a fit that respects
    the XML conditional flags still wins any tie."""
    n, m = len(observed), len(rows)
    results = []

    def rec(oi, ri, path, penalty):
        if len(results) > 4:
            return                          # plenty ambiguous already; stop early
        if oi == n:
            trailing = sum(1 for j in range(ri, m) if not rows[j][2])
            results.append((penalty + trailing, list(path)))
            return
        u = observed[oi]
        for j in range(ri, m):
            skipped = sum(1 for k in range(ri, j) if not rows[k][2])
            pref, hv, cond, gxt = rows[j]
            if hv is None:
                continue
            if (u - hv) not in offsets_for_header(hv):
                continue
            path.append(j)
            rec(oi + 1, j + 1, path, penalty + skipped)
            path.pop()

    rec(0, 0, [], 0)
    if not results:
        return [], "no valid alignment (offset constraints)"
    results.sort(key=lambda r: r[0])
    if len(results) > 1 and results[0][0] == results[1][0]:
        return [], "ambiguous (tied alignments — walk the pane fully, top to bottom)"
    bindings = []
    for oi, j in enumerate(results[0][1]):
        pref, hv, cond, gxt = rows[j]
        bindings.append((observed[oi], pref, hv, observed[oi] - hv))
    note = "forced" if len(results) == 1 else f"best fit (penalty {results[0][0]})"
    return bindings, note


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    write = "--write" in sys.argv
    if args:
        logs = args
    else:
        cand = sorted(glob.glob(os.path.join(MODSETTINGS, "menulog-*.log")))
        logs = cand[-1:] if cand else []
    if not logs:
        sys.exit("no menulog found; pass a path")
    print("reading:", ", ".join(os.path.basename(x) for x in logs))

    panes, prefs = load_pane_rows()
    chains = parse_chains(logs)
    if not chains:
        sys.exit("no EVENT pane-chain lines found — is this a v1.3 calibration log?")

    val_to_name = {v: k for k, v in prefs.items()}
    stamp = ", ".join(os.path.basename(x) for x in logs)
    offset_points, suggest_rows = {}, []
    for pane in sorted(chains):
        rows = panes.get(pane) or []
        # The XML row order expressed in RUNTIME uids, so the rooting check can
        # compare like with like. Free-band headers get their provisional +4.
        xml_uids = [r[1] + (0 if r[1] <= SETTLED_LO_MAX else SHIFT)
                    for r in rows if r[1] is not None]
        observed, ord_ok, note = visible_order(chains[pane], xml_uids)
        settled, free = resolve_settled(observed, val_to_name)
        print(f"\npane {pane}: {len(observed)} visible rows, "
              f"{len(settled)} settled, {len(free)} in the free 97..107 band")
        print(f"    order: {note}")

        # The free band still needs the constrained search, and only against
        # this pane's own XML rows.
        free_bindings = []
        if free:
            if rows:
                free_bindings, note = align_pane(pane, rows, free)
                print(f"    free band: {note}")
                if not free_bindings:
                    print(f"      observed: {free}")
                    print(f"      xml rows: {[(r[0], r[1], 'cond' if r[2] else '') for r in rows]}")
            else:
                print(f"    free band: no XML rows for this screen — {free} left unpinned")

        for u, pref, hv, off in settled + free_bindings:
            is_free = any(u == b[0] for b in free_bindings)
            offset_points[hv] = off
            # ord is the OBSERVED visible index, not the XML index — that is what
            # the reader's ordinal-monotonicity gate needs, and this build hides
            # and reorders rows relative to the mirrored pausemenu.xml.
            ordv = observed.index(u)
            print(f"    uid {u:>4} {('ord %2d' % ordv) if ord_ok else 'ord --'}"
                  f"  <- {pref} (header {hv}, offset +{off})"
                  f"{'  [free-band fit]' if is_free else ''}")
            row = {
                "menuId": 51, "uniqueId": u,
                "pref": pref, "screenMenuId": pane, "ord": ordv,
                "evidence": f"align-panes.py {'free-band fit' if is_free else 'settled s(h)'}"
                            f", pane {pane}, {stamp}",
            }
            # ord is only claimed when the track rooted unambiguously against the
            # XML order; otherwise the pref binding still stands but the reader's
            # ordinal gate must not be fed a guess.
            if not ord_ok:
                row.pop("ord")
            # The free band stays honest: it keeps the spoken "unverified" cue
            # until a targeted session settles s() inside 97..107.
            row["status"] = "assumed" if is_free else "verified"
            row["keyConfidence"] = "unpinned" if is_free else "confirmed"
            suggest_rows.append(row)

    # ---- recover + validate the shift step function s(header) ----
    if offset_points:
        print("\n=== recovered offset s(header) (step points = inserted prefs) ===")
        last, steps, bad = None, [], False
        for hv in sorted(offset_points):
            off = offset_points[hv]
            if last is not None and off < last:
                print(f"  !! NON-MONOTONIC: header {hv} offset {off} < previous {last} "
                      f"— alignment or data suspect")
                bad = True
            if last is not None and off != last:
                steps.append((hv, last, off))
                print(f"  step near header {hv}: offset {last} -> {off}")
            last = off
        if not steps:
            print(f"  no step observed in sampled headers (all offset {last})")
        print(f"  sampled {len(offset_points)} headers; anchors s(<={SETTLED_LO_MAX})=0, "
              f"s(>={SETTLED_HI_MIN})=4 expected; "
              f"{'MONOTONIC OK' if not bad else 'CHECK FAILED'}")
        lo = [h for h in offset_points if h <= SETTLED_LO_MAX]
        hi = [h for h in offset_points if h >= SETTLED_HI_MIN]
        if lo and any(offset_points[h] != 0 for h in lo):
            print(f"  !! anchor s(<={SETTLED_LO_MAX})=0 violated")
        if hi and any(offset_points[h] != SHIFT for h in hi):
            print(f"  !! anchor s(>={SETTLED_HI_MIN})=4 violated")

    out = {"suggestedRows": suggest_rows,
           "offsetByHeader": {str(k): v for k, v in sorted(offset_points.items())}}
    with open(SUGGEST, "w", encoding="utf-8") as fh:
        json.dump(out, fh, indent=1)
    print(f"\nwrote {len(suggest_rows)} suggested verified rows -> "
          f"{os.path.normpath(SUGGEST)}")

    if write and suggest_rows:
        with open(OVERRIDES, encoding="utf-8") as fh:
            ov = json.load(fh)
        by_uid = {r["uniqueId"]: r for r in ov.get("rows", [])}
        added = updated = 0
        for r in suggest_rows:
            cur = by_uid.get(r["uniqueId"])
            if cur is None:
                ov.setdefault("rows", []).append(r)
                by_uid[r["uniqueId"]] = r
                added += 1
                continue
            # Existing hand-pinned row: fill in what the alignment learned
            # (screenMenuId / ord) without clobbering its human evidence note or
            # downgrading a verified row to assumed.
            for k, v in r.items():
                if k in ("menuId", "uniqueId", "evidence"):
                    continue
                if k == "status" and cur.get("status") == "verified":
                    continue
                if k == "keyConfidence" and cur.get("keyConfidence") == "confirmed":
                    continue
                if cur.get(k) != v:
                    cur[k] = v
                    updated += 1
        with open(OVERRIDES, "w", encoding="utf-8") as fh:
            json.dump(ov, fh, indent=2)
        print(f"--write: {added} new rows, {updated} field updates into "
              f"{os.path.basename(OVERRIDES)} (rerun build-menulabels.py)")
    elif suggest_rows:
        print("review _align_suggestions.json, then rerun with --write to merge")


if __name__ == "__main__":
    main()
