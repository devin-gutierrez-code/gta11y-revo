#!/usr/bin/env python3
"""triage-log.py — zero-token pre-digestion of gta11y drive-assist debug logs.

Streams a driveassist-*.log (tens of MB) and emits small analysis artifacts so
that no agent ever has to read the raw log:

  triage/<log-stem>/digest.md       session overview + per-incident summaries
  triage/<log-stem>/incident-NN.log full-rate excerpt around each failure cluster
  triage/<log-stem>/events.log      every EVENT line of the session
  tools/log-index.json              one trend row per session (appended/updated)

Windows are computed from FRAME t= timestamps, never frame counts (fps is
variable and can exceed 188 with the uncapper). Tolerant of older log formats:
every field is optional.

Usage:
  python tools\\triage-log.py                 # newest log in ModSettings/repo root
  python tools\\triage-log.py <log> [...]     # specific log(s)
  python tools\\triage-log.py --index-only <logs...>   # backfill trend index only
"""

import argparse
import json
import math
import re
import statistics
import sys
from datetime import datetime
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
MODSETTINGS = Path.home() / "Documents" / "Rockstar Games" / "GTA V" / "ModSettings"

FRAME_RE = re.compile(r"^FRAME (\d+)(?: t=(\d{2}):(\d{2}):(\d{2})\.(\d{3}))?")
EVENT_RE = re.compile(r"^(?:\[F(\d+)\]\s+)?EVENT ([\w-]+):?\s*(.*)$")
MARKER_RE = re.compile(
    r"^(?:\[F(\d+)\]\s+)?(AUTO-COLLISION|PLAYER-INDICATED-FAILURE) #(\d+)\s*(.*)$")
TIME_RE = re.compile(r"t=(\d{2}):(\d{2}):(\d{2})\.(\d{3})")

NOTABLE_EVENT_KINDS = (
    "vehicle-death", "vehicle-swap", "vehicle-repair", "teleport", "autonav",
    "wedge-damage", "reverse-abort", "abort-deadlock", "stuck-locus",
    "map-data", "map-data-mismatch", "iter-version", "v2-selftest",
    # iter-36 additions
    "stuck-locus-clear", "damage-tally", "brake-standoff",
    "assist-dead-vehicle", "wrongway-junction",
)


def kv(line, key):
    """Value of key= in line, or None. Empty string is a valid value."""
    m = re.search(re.escape(key) + r"=(\S*)", line)
    return m.group(1) if m else None


def fnum_of(line, key):
    v = kv(line, key)
    if v is None:
        return None
    try:
        return float(v.rstrip(",)"))
    except ValueError:
        return None


def hms_to_s(h, m, s, ms):
    return int(h) * 3600 + int(m) * 60 + int(s) + int(ms) / 1000.0


def fmt_t(t):
    if t is None:
        return "--:--:--"
    t = t % 86400
    return "%02d:%02d:%06.3f" % (t // 3600, (t % 3600) // 60, t % 60)


class Frame:
    __slots__ = ("f", "t", "line0", "line1", "spd", "mode", "lat", "skew",
                 "steer", "brake", "owner", "decel", "thrA", "thrP", "navC",
                 "street", "bh", "eh", "pos", "onRoad", "nativeOnRoad")

    def __init__(self, f, t, line0):
        self.f, self.t, self.line0 = f, t, line0
        self.line1 = line0 + 1
        self.spd = self.lat = self.skew = self.steer = self.brake = None
        self.decel = self.thrA = self.thrP = self.navC = None
        self.bh = self.eh = None
        self.mode = self.owner = self.street = self.pos = None
        self.onRoad = self.nativeOnRoad = None

    def trace(self):
        def n(v, fmt="%.2f"):
            return (fmt % v) if v is not None else "-"
        return (f"F{self.f} {fmt_t(self.t)} spd={n(self.spd,'%.1f')} "
                f"mode={self.mode or '-'} lat={n(self.lat)} skew={n(self.skew,'%.1f')} "
                f"steer={n(self.steer)} brake={n(self.brake)} own={self.owner or '-'} "
                f"decel={n(self.decel,'%.1f')} thr={n(self.thrA)}/{n(self.thrP)} "
                f"navC={n(self.navC,'%.1f')} bh={n(self.bh,'%.0f')} "
                f"street={self.street or '-'} pos={self.pos or '-'}")


class Marker:
    __slots__ = ("num", "kind", "typ", "f", "t", "delta", "impactSpeed",
                 "line_no", "raw", "snap0", "snap1", "frame",
                 "failClass", "street", "latAgeMs", "mode", "layer")

    def __init__(self):
        self.num = self.f = self.t = self.delta = self.impactSpeed = None
        self.kind = self.typ = self.raw = None
        self.line_no = self.snap0 = self.snap1 = None
        self.frame = None
        # iter-36 P0-1: self-classifying header kv (present from iter36 logs on)
        self.failClass = self.street = self.mode = None
        self.latAgeMs = self.layer = None


def parse_log(path):
    """One in-memory pass. Returns (lines, frames, events, markers, header)."""
    text = path.read_text(encoding="utf-8", errors="replace")
    lines = text.splitlines()
    del text

    frames, events, markers = [], [], {}
    header = {"banner": None, "iter": None}
    cur = None
    t_off = 0.0
    last_t = None
    section = None

    def abs_t(raw_t):
        nonlocal t_off, last_t
        t = raw_t + t_off
        if last_t is not None and t < last_t - 43200:  # crossed midnight
            t_off += 86400
            t = raw_t + t_off
        last_t = t
        return t

    i = 0
    n = len(lines)
    while i < n:
        line = lines[i]
        if line.startswith("FRAME "):
            m = FRAME_RE.match(line)
            if m:
                if cur:
                    cur.line1 = i
                t = abs_t(hms_to_s(*m.groups()[1:])) if m.group(2) else None
                cur = Frame(int(m.group(1)), t, i)
                frames.append(cur)
                section = "frame"
        elif line.startswith("  ") and section == "frame" and cur:
            s = line.strip()
            if s.startswith("veh:"):
                sp = re.search(r"speed=([\d.]+)m/s", s)
                cur.spd = float(sp.group(1)) if sp else None
                pm = re.search(r"pos=\(([-\d.,]+)\)", s)
                cur.pos = "(" + pm.group(1) + ")" if pm else None
                cur.bh = fnum_of(s, "bh")
                cur.eh = fnum_of(s, "eh")
            elif s.startswith("road:"):
                cur.lat = fnum_of(s, "lat")
                cur.skew = fnum_of(s, "skewAngle")
                cur.street = kv(s, "street")
                cur.onRoad = kv(s, "onValidRoad")
                cur.nativeOnRoad = kv(s, "nativeOnRoad")
            elif s.startswith("mode:"):
                cur.mode = kv(s, "drive")
            elif s.startswith("decision:"):
                cur.steer = fnum_of(s, "smoothedSteer")
            elif s.startswith("brake:"):
                cur.brake = fnum_of(s, "rampedBrake")
                cur.owner = kv(s, "owner")
                cur.decel = fnum_of(s, "decelMs2")
            elif s.startswith("throttle:"):
                cur.thrA = fnum_of(s, "applied")
                cur.thrP = fnum_of(s, "playerRaw")
            elif s.startswith("navDist:"):
                cur.navC = fnum_of(s, "C")
        elif line.startswith("#"):
            if line.startswith("# Build:"):
                header["banner"] = line
                bm = re.search(r"# Build: (\S+)", line)
                header["iter"] = bm.group(1) if bm else None
            section = None
        else:
            em = EVENT_RE.match(line)
            if em:
                f = int(em.group(1)) if em.group(1) else (cur.f if cur else None)
                tm = TIME_RE.search(line)
                t = abs_t(hms_to_s(*tm.groups())) if tm else (cur.t if cur else None)
                events.append({"f": f, "t": t, "kind": em.group(2),
                               "rest": em.group(3), "line": line, "i": i})
                if em.group(2) == "iter-version":
                    # authoritative banner; the "# Build:" header can be stale
                    header["iter"] = em.group(3).strip() or header["iter"]
                section = None
                i += 1
                continue
            mm = MARKER_RE.match(line)
            if mm:
                mk = Marker()
                mk.f = int(mm.group(1)) if mm.group(1) else (cur.f if cur else None)
                mk.typ = "P" if mm.group(2).startswith("PLAYER") else "A"
                mk.num = int(mm.group(3))
                rest = mm.group(4)
                mk.kind = kv(rest, "kind")
                mk.delta = fnum_of(rest, "healthDelta")
                mk.impactSpeed = fnum_of(rest, "impactSpeed")
                # iter-36 P0-1: lift the self-classifying header kv
                mk.failClass = kv(rest, "failClass")
                mk.street = kv(rest, "street")
                mk.mode = kv(rest, "mode")
                mk.layer = kv(rest, "layer")
                mk.latAgeMs = fnum_of(rest, "latAgeMs")
                tm = TIME_RE.search(rest)
                mk.t = abs_t(hms_to_s(*tm.groups())) if tm else (cur.t if cur else None)
                mk.line_no = i
                mk.raw = line
                mk.frame = cur
                # snapshot block: preceding ==== line through the indented dump
                snap0 = i - 1 if i > 0 and lines[i - 1].startswith("====") else i
                j = i + 1
                if j < n and lines[j].startswith("===="):
                    j += 1
                    while j < n and (lines[j].startswith(" ") or lines[j].startswith("====")):
                        j += 1
                mk.snap0, mk.snap1 = snap0, j
                markers[(mk.typ, mk.num)] = mk
                section = None
                i = i + 1
                continue
            if not line.startswith("===="):
                section = None
        i += 1
    if cur:
        cur.line1 = n
    return lines, frames, events, list(markers.values()), header


def cluster_markers(markers, gap):
    ms = sorted(markers, key=lambda m: (m.t if m.t is not None else 0, m.num))
    clusters = []
    for m in ms:
        if (clusters and m.t is not None and clusters[-1][-1].t is not None
                and m.t - clusters[-1][-1].t <= gap):
            clusters[-1].append(m)
        else:
            clusters.append([m])
    return clusters


def frame_at(frames, t):
    """Last frame with frame.t <= t (linear-free bisect on t)."""
    lo, hi = 0, len(frames) - 1
    best = None
    while lo <= hi:
        mid = (lo + hi) // 2
        ft = frames[mid].t
        if ft is None or ft <= t:
            best = frames[mid]
            lo = mid + 1
        else:
            hi = mid - 1
    return best


def frames_in(frames, t0, t1):
    return [fr for fr in frames if fr.t is not None and t0 <= fr.t <= t1]


def decimate(frs, hz=10.0):
    out, last = [], None
    step = 1.0 / hz
    for fr in frs:
        if last is None or fr.t - last >= step:
            out.append(fr)
            last = fr.t
    return out


def driving_stats(frames):
    """(driving_seconds, gaps>5s list, per-second fps counts)."""
    driving, gaps, fps = 0.0, [], {}
    prev = None
    for fr in frames:
        if fr.t is None:
            continue
        if prev is not None:
            d = fr.t - prev
            if d > 5.0:
                gaps.append((prev, d))
            else:
                driving += max(d, 0)
        fps[int(fr.t)] = fps.get(int(fr.t), 0) + 1
        prev = fr.t
    return driving, gaps, sorted(fps.values())


def unmarked_damage(frames, markers, events, thresh=5.0, window=3.0):
    """Returns (hits, gap_skips). Frame pairs spanning a >5s wall gap
    (pause/alt-tab/load — same threshold driving_stats uses) are skipped:
    health deltas across a gap are teleports/reloads, not driving damage."""
    hits = []
    gap_skips = 0
    mtimes = [m.t for m in markers if m.t is not None]
    for a, b in zip(frames, frames[1:]):
        if a.bh is None or b.bh is None:
            continue
        drop = (a.bh - b.bh) + ((a.eh - b.eh) if a.eh is not None and b.eh is not None else 0)
        if drop >= thresh and a.t is not None and b.t is not None and (b.t - a.t) > 5.0:
            gap_skips += 1
            continue
        if drop >= thresh and b.t is not None:
            if any(abs(b.t - mt) <= window for mt in mtimes):
                continue
            note = ""
            for ev in events:
                if ev["t"] is not None and abs(ev["t"] - b.t) <= window and \
                        ev["kind"] in ("vehicle-swap", "vehicle-repair", "teleport", "vehicle-death",
                                       "grind-start", "grind-end", "collision-suppressed",
                                       "damage-tally"):  # iter-36 P1-5
                    note = f" [{ev['kind']} nearby]"
                    break
            hits.append(f"F{b.f} {fmt_t(b.t)} bh {a.bh:.0f}->{b.bh:.0f} "
                        f"eh {a.eh or 0:.0f}->{b.eh or 0:.0f} spd={b.spd or 0:.1f} "
                        f"pos={b.pos or '-'} street={b.street or '-'}{note}")
    return hits, gap_skips


def events_in(events, t0, t1):
    return [ev for ev in events if ev["t"] is not None and t0 <= ev["t"] <= t1]


def event_lines_compact(evs, verbatim_max=3, cap=25):
    """Verbatim for rare kinds, aggregated counts for per-tick spam kinds."""
    by_kind = {}
    for ev in evs:
        by_kind.setdefault(ev["kind"], []).append(ev)
    out = []
    for kind, group in sorted(by_kind.items(), key=lambda x: x[1][0]["i"]):
        if kind == "speech":
            continue
        if len(group) <= verbatim_max:
            out.extend(g["line"].strip() for g in group)
        else:
            out.append(f"[x{len(group)}] EVENT {kind}: first {group[0]['line'].strip()[:120]}"
                       f" ... last F{group[-1]['f']}")
    return out[:cap] + ([f"... {len(out)-cap} more kinds/lines"] if len(out) > cap else [])


def write_excerpt(out_dir, ci, lines, frames, events, cluster, pre, post,
                  tr_pre, tr_post):
    """Two files per incident cluster:
    incident-NN.log        compact: snapshots + full-rate trace + events
    incident-NN-frames.log raw full-rate FRAME slice (grep-free archive)
    """
    first, last = cluster[0], cluster[-1]
    t0 = (first.t or 0) - pre
    t1 = (last.t or 0) + post
    core = frames_in(frames, t0, t1)
    head = [f"# Incident {ci:02d}: markers "
            f"{', '.join('#%d(%s)' % (m.num, m.typ) for m in cluster)}"]
    for m in cluster:
        head.append(f"#   {m.raw.strip()}")

    out = list(head)
    out.append(f"# Core {fmt_t(t0)}..{fmt_t(t1)} full-rate trace; outer ~10Hz. "
               f"Full FRAME blocks for the core: incident-{ci:02d}-frames.log")
    out.append("")
    lead = decimate(frames_in(frames, (first.t or 0) - tr_pre, t0 - 0.001))
    if lead:
        out.append("## Lead-in trace (~10Hz)")
        out.extend(fr.trace() for fr in lead)
        out.append("")
    for m in cluster:
        out.append(f"## Snapshot dump (marker #{m.num})")
        out.extend(lines[m.snap0:m.snap1])
        out.append("")
    if core:
        out.append("## Core trace (~20Hz; every frame in the -frames.log archive)")
        out.extend(fr.trace() for fr in decimate(core, hz=20.0))
        out.append("")
    tail = decimate(frames_in(frames, t1 + 0.001, (last.t or 0) + tr_post))
    if tail:
        out.append("## Tail trace (~10Hz)")
        out.extend(fr.trace() for fr in tail)
        out.append("")
    evs = events_in(events, (first.t or 0) - tr_pre, (last.t or 0) + tr_post)
    if evs:
        out.append("## Events in window (spam kinds aggregated)")
        out.extend(event_lines_compact(evs, cap=60))
        texts = []
        for e in evs:
            if e["kind"] == "speech":
                tm = re.search(r'text="([^"]*)"', e["rest"])
                if tm:
                    texts.append(f"F{e['f']} \"{tm.group(1)}\"")
        if texts:
            out.append("## Speech in window")
            out.extend(texts)
    p = out_dir / f"incident-{ci:02d}.log"
    p.write_text("\n".join(out) + "\n", encoding="utf-8")

    raw = list(head)
    raw.append(f"# Raw full-rate slice {fmt_t(t0)}..{fmt_t(t1)} — read selectively "
               f"(offset/limit), the compact incident-{ci:02d}.log usually suffices.")
    if core:
        lo = core[0].line0
        hi = max(core[-1].line1, max(m.snap1 for m in cluster))
        raw.extend(lines[lo:hi])
    pf = out_dir / f"incident-{ci:02d}-frames.log"
    pf.write_text("\n".join(raw) + "\n", encoding="utf-8")
    return p, pf


def cluster_summary(frames, events, cluster, idx):
    first = cluster[0]
    out = [f"### Incident {idx:02d}  ({', '.join('#%d %s%s' % (m.num, m.typ, ' ' + m.kind if m.kind else '') for m in cluster)})"]
    deltas = [m.delta for m in cluster if m.delta is not None]
    fr0 = first.frame
    out.append(f"- at {fmt_t(first.t)} F{first.f}  pos={fr0.pos if fr0 else '-'} "
               f"street={fr0.street if fr0 else '-'}"
               + (f"  healthDelta={sum(deltas):.1f}" if deltas else "")
               + (f"  impactSpeed={first.impactSpeed}" if first.impactSpeed is not None else ""))
    if first.t is not None:
        out.append("- approach trace:")
        for off in (-10, -5, -3, -2, -1, -0.5, 0, 1, 3):
            fr = frame_at(frames, first.t + off)
            if fr and fr.t is not None and abs(fr.t - (first.t + off)) < 2.5:
                out.append(f"    [{off:+.1f}s] {fr.trace()}")
        evs = [ev for ev in events_in(events, first.t - 10, (cluster[-1].t or first.t) + 3)
               if ev["kind"] != "speech"]
        if evs:
            by_kind = {}
            for ev in evs:
                by_kind.setdefault(ev["kind"], []).append(ev)
            parts = []
            for kind, group in sorted(by_kind.items(), key=lambda x: x[1][0]["i"]):
                if len(group) == 1:
                    parts.append(f"F{group[0]['f']} {kind}: {group[0]['rest'][:80]}")
                else:
                    parts.append(f"{kind} x{len(group)}")
            out.append("- events in window: " + "; ".join(parts[:14]) +
                       (f" (+{len(parts)-14} kinds)" if len(parts) > 14 else ""))
        speech = [ev for ev in events if ev["t"] is not None and ev["kind"] == "speech"
                  and first.t - 10 <= ev["t"] <= (cluster[-1].t or first.t) + 3]
        if speech:
            texts = []
            for e in speech:
                tm = re.search(r'text="([^"]*)"', e["rest"])
                if tm:
                    texts.append(tm.group(1))
            if texts:
                out.append("- speech in window: " + " | ".join(texts[:12]))
    return out


def build_digest(log_path, lines, frames, events, markers, header, clusters,
                 unmarked, index_rows, args, gap_skips=0):
    out = [f"# Triage digest — {log_path.name}", ""]
    out.append(f"Generated {datetime.now():%Y-%m-%d %H:%M} by tools/triage-log.py; "
               f"raw log {log_path.stat().st_size/1048576:.1f} MB / {len(lines)} lines. "
               f"Incident excerpts + events.log live next to this file.")
    out.append("")

    out.append("## Session")
    it = header.get("iter") or "?"
    out.append(f"- build banner: {it}")
    for ev in events:
        if ev["kind"] in ("iter-version", "map-data", "v2-selftest", "map-data-mismatch"):
            out.append(f"- {ev['line'].strip()}")
    tf = [fr.t for fr in frames if fr.t is not None]
    driving, gaps, fps_counts = driving_stats(frames)
    if tf:
        out.append(f"- frames: {len(frames)}  span {fmt_t(tf[0])} -> {fmt_t(tf[-1])} "
                   f"({(tf[-1]-tf[0])/60:.1f} min wall, {driving/60:.1f} min driving)")
    else:
        out.append(f"- frames: {len(frames)} (no timestamps in this log format)")
    if fps_counts:
        # Clamped p95 index: naive int(n*0.95)-1 underflows to -1 (= max,
        # silently mislabeled as p95) for very short sessions.
        p95_idx = max(0, min(len(fps_counts) - 1, math.ceil(len(fps_counts) * 0.95) - 1))
        out.append(f"- fps profile: median={statistics.median(fps_counts):.0f} "
                   f"p95={fps_counts[p95_idx]} max={fps_counts[-1]} "
                   f"secs>100fps={sum(1 for c in fps_counts if c > 100)}")
    last_fr = frames[-1] if frames else None
    pcount = sum(1 for m in markers if m.typ == "P")
    acount = len(markers) - pcount
    deltas = [m.delta for m in markers if m.delta is not None]
    rate1k = len(markers) / len(frames) * 1000 if frames else 0
    ratemin = len(markers) / (driving / 60) if driving > 0 else 0
    out.append(f"- markers: {len(markers)} ({pcount}P + {acount}A) in {len(clusters)} "
               f"distinct incidents (<= {args.cluster_gap:.0f}s clustering)")
    out.append(f"- rate: {rate1k:.2f}/1k frames  |  {ratemin:.2f}/driving-minute (fps-independent)")
    if deltas:
        out.append(f"- marked health loss: total {sum(deltas):.1f}, worst {min(deltas):.1f}")
    if last_fr and last_fr.bh is not None:
        out.append(f"- end health: bh={last_fr.bh:.0f} eh={last_fr.eh:.0f}")
    out.append("")

    out.append("## Failure table")
    out.append("| # | typ | frame | time | kind | failClass | delta | impactSpd | street | incident |")
    out.append("|---|-----|-------|------|------|-----------|-------|-----------|--------|----------|")
    for ci, cl in enumerate(clusters, 1):
        for m in cl:
            fr = m.frame
            # iter-36 P0-1: header kv beats the last-FRAME fallback; !stale flags
            # a marker whose lane model was frozen (latAgeMs > 2s) at press time.
            street = m.street or (fr.street if fr else None) or "-"
            fc = m.failClass or "-"
            if m.latAgeMs is not None and m.latAgeMs > 2000:
                fc += "!stale"
            out.append(f"| {m.num} | {m.typ} | F{m.f} | {fmt_t(m.t)} | {m.kind or '-'} | "
                       f"{fc} | "
                       f"{m.delta if m.delta is not None else '-'} | "
                       f"{m.impactSpeed if m.impactSpeed is not None else '-'} | "
                       f"{street} | {ci:02d} |")
    out.append("")

    out.append("## Incidents (one line each — detail in incidents.md, excerpts in incident-NN*.log)")
    for ci, cl in enumerate(clusters, 1):
        first = cl[0]
        fr = first.frame
        deltas = [m.delta for m in cl if m.delta is not None]
        marks = "+".join(f"#{m.num}{m.typ}" + (f"({m.kind})" if m.kind else "")
                         for m in cl)
        out.append(f"- {ci:02d} {fmt_t(first.t)} F{first.f} {marks}"
                   + (f" delta={sum(deltas):.1f}" if deltas else "")
                   + (f" failClass={first.failClass}" if first.failClass else "")
                   + f" street={first.street or (fr.street if fr else None) or '-'}"
                   + f" pos={fr.pos if fr else '-'}"
                   + (f" spd={fr.spd:.1f}" if fr and fr.spd is not None else ""))
    out.append("")

    out.append("## Unmarked damage sweep (bh+eh drop >= 5 with no marker within 3s)")
    if gap_skips:
        out.append(f"- [pause-gap skipped: {gap_skips} drop(s) spanning a >5s wall gap "
                   f"— reload/teleport artifacts, not driving damage]")
    if unmarked:
        out.extend(f"- {h}" for h in unmarked[:40])
        if len(unmarked) > 40:
            out.append(f"- ... {len(unmarked)-40} more")
    else:
        out.append("- none — every damage episode has a marker")
    out.append("")

    out.append("## Notable events (verbatim)")
    notable = [ev for ev in events if ev["kind"] in NOTABLE_EVENT_KINDS
               and ev["kind"] not in ("map-data", "iter-version", "v2-selftest")]
    if notable:
        out.extend(f"- {ev['line'].strip()}" for ev in notable[:40])
        if len(notable) > 40:
            out.append(f"- ... {len(notable)-40} more (see events.log)")
    else:
        out.append("- none")
    out.append("")

    out.append("## EVENT histogram")
    hist = {}
    for ev in events:
        hist[ev["kind"]] = hist.get(ev["kind"], 0) + 1
    for k, v in sorted(hist.items(), key=lambda x: -x[1]):
        out.append(f"- {v:6d}  {k}")
    out.append("")

    speech = {}
    for ev in events:
        if ev["kind"] == "speech":
            tm = re.search(r'text="([^"]*)"', ev["rest"])
            if tm:
                speech[tm.group(1)] = speech.get(tm.group(1), 0) + 1
    if speech:
        out.append("## Speech histogram (top 20)")
        for k, v in sorted(speech.items(), key=lambda x: -x[1])[:20]:
            out.append(f"- {v:5d}  {k}")
        out.append("")

    if gaps:
        out.append("## Wall-clock gaps > 5s (idle/pause — rate noise)")
        for t, d in gaps[:20]:
            out.append(f"- at {fmt_t(t)}: {d:.1f}s gap")
        out.append("")

    if index_rows:
        out.append("## Trend (from tools/log-index.json)")
        out.append("| log | iter | frames | drv-min | markers | incidents | /1k | /min | worst | total |")
        out.append("|-----|------|--------|---------|---------|-----------|-----|------|-------|-------|")
        for r in index_rows[-6:]:
            out.append(f"| {r['log']} | {r.get('iter','?')} | {r.get('frames','-')} | "
                       f"{r.get('minutes','-')} | {r.get('markers','-')} | {r.get('incidents','-')} | "
                       f"{r.get('ratePer1k','-')} | {r.get('ratePerMin','-')} | "
                       f"{r.get('worstDelta','-')} | {r.get('totalDelta','-')} |")
        out.append("")
    return "\n".join(out) + "\n"


def index_row(log_path, frames, markers, clusters, header):
    driving, _, _ = driving_stats(frames)
    deltas = [m.delta for m in markers if m.delta is not None]
    return {
        "log": log_path.name,
        "iter": header.get("iter"),
        "frames": len(frames),
        "minutes": round(driving / 60, 1),
        "markers": len(markers),
        "incidents": len(clusters),
        "ratePer1k": round(len(markers) / len(frames) * 1000, 2) if frames else None,
        "ratePerMin": round(len(markers) / (driving / 60), 2) if driving > 0 else None,
        "worstDelta": min(deltas) if deltas else None,
        "totalDelta": round(sum(deltas), 1) if deltas else None,
        "endBh": frames[-1].bh if frames and frames[-1].bh is not None else None,
        "endEh": frames[-1].eh if frames and frames[-1].eh is not None else None,
        "generated": datetime.now().strftime("%Y-%m-%d %H:%M"),
    }


def load_index(path):
    if path.exists():
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            print(f"WARN: could not parse {path}, starting fresh", file=sys.stderr)
    return []


def newest_log():
    cands = []
    for d in (MODSETTINGS, REPO_ROOT):
        if d.is_dir():
            cands.extend(d.glob("driveassist-*.log"))
    if not cands:
        sys.exit("No driveassist-*.log found in ModSettings or repo root.")
    return max(cands, key=lambda p: p.stat().st_mtime)


def process(log_path, args, index):
    print(f"== {log_path} ({log_path.stat().st_size/1048576:.1f} MB)")
    lines, frames, events, markers, header = parse_log(log_path)
    # iter-36 P0-3: treat vehicle-death as an A-class marker so a death can
    # never fall outside every incident cluster window. Synthetic num >= 900
    # avoids colliding with the real marker sequence.
    for k, ev in enumerate(e for e in events if e["kind"] == "vehicle-death"):
        mk = Marker()
        mk.typ = "A"
        mk.kind = "death"
        mk.num = 900 + k
        mk.f = ev["f"]
        mk.t = ev["t"]
        bd = fnum_of(ev["rest"], "bodyDrop")
        ed = fnum_of(ev["rest"], "engineDrop")
        mk.delta = -((bd or 0) + (ed or 0)) if (bd or ed) else None
        mk.street = kv(ev["rest"], "street")
        mk.line_no = ev["i"]
        mk.snap0, mk.snap1 = ev["i"], ev["i"] + 1
        mk.frame = frame_at(frames, ev["t"]) if ev["t"] is not None else None
        markers.append(mk)
    clusters = cluster_markers(markers, args.cluster_gap) if markers else []
    print(f"   frames={len(frames)} events={len(events)} markers={len(markers)} "
          f"incidents={len(clusters)} iter={header.get('iter')}")

    row = index_row(log_path, frames, markers, clusters, header)
    index[:] = [r for r in index if r.get("log") != row["log"]]
    index.append(row)
    index.sort(key=lambda r: r.get("log", ""))

    if args.index_only:
        return

    out_dir = args.out_dir / log_path.stem
    out_dir.mkdir(parents=True, exist_ok=True)

    ev_path = out_dir / "events.log"
    ev_path.write_text("\n".join(ev["line"] for ev in events) + "\n", encoding="utf-8")

    for ci, cl in enumerate(clusters, 1):
        p, pf = write_excerpt(out_dir, ci, lines, frames, events, cl, args.pre,
                              args.post, args.trace_pre, args.trace_post)
        print(f"   wrote {p.name} ({p.stat().st_size/1024:.0f} KB) + "
              f"{pf.name} ({pf.stat().st_size/1024:.0f} KB)")

    if clusters:
        inc = [f"# Per-incident summaries — {log_path.name}",
               f"(approach traces around each cluster; compact excerpts: "
               f"incident-NN.log, raw full-rate: incident-NN-frames.log)", ""]
        for ci, cl in enumerate(clusters, 1):
            inc.extend(cluster_summary(frames, events, cl, ci))
            inc.append("")
        ip = out_dir / "incidents.md"
        ip.write_text("\n".join(inc) + "\n", encoding="utf-8")
        print(f"   wrote {ip.name} ({ip.stat().st_size/1024:.1f} KB)")

    unmarked, gap_skips = unmarked_damage(frames, markers, events)
    digest = build_digest(log_path, lines, frames, events, markers, header,
                          clusters, unmarked, index, args, gap_skips)
    dp = out_dir / "digest.md"
    dp.write_text(digest, encoding="utf-8")
    print(f"   wrote {dp} ({dp.stat().st_size/1024:.1f} KB), events.log "
          f"({ev_path.stat().st_size/1024:.0f} KB)")


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("logs", nargs="*", help="log file(s); default = newest")
    ap.add_argument("--out-dir", type=Path, default=REPO_ROOT / "triage")
    ap.add_argument("--index", type=Path, default=REPO_ROOT / "tools" / "log-index.json")
    ap.add_argument("--index-only", action="store_true",
                    help="only append trend row(s); skip digest/excerpts")
    ap.add_argument("--pre", type=float, default=3.0,
                    help="full-rate seconds before a cluster's first marker")
    ap.add_argument("--post", type=float, default=3.0,
                    help="full-rate seconds after a cluster's last marker")
    ap.add_argument("--trace-pre", type=float, default=15.0)
    ap.add_argument("--trace-post", type=float, default=5.0)
    ap.add_argument("--cluster-gap", type=float, default=3.0,
                    help="markers closer than this (s) are one incident")
    args = ap.parse_args()

    paths = [Path(p) for p in args.logs] if args.logs else [newest_log()]
    for p in paths:
        if not p.exists():
            sys.exit(f"Not found: {p}")

    index = load_index(args.index)
    for p in paths:
        process(p, args, index)
    args.index.write_text(json.dumps(index, indent=1), encoding="utf-8")
    print(f"== index updated: {args.index} ({len(index)} sessions)")


if __name__ == "__main__":
    main()
