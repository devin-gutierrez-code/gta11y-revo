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
# iter-52: LogCollisionSnapshot used to have exactly two call sites, so this regex
# listed both by name. It now fires at every failure emitter (engine-stuck, stuck-layer,
# wedge-damage, damage-tally, ...), and a tag this pattern does not match is a snapshot
# the whole pipeline silently ignores — which is the same blind spot the broadening was
# meant to close. Match the SHAPE of the header instead of an allow-list, so a tag added
# in C# tomorrow is picked up without a matching edit here.
MARKER_RE = re.compile(
    r"^(?:\[F(\d+)\]\s+)?([A-Z][A-Z0-9-]{3,31}) #(\d+)\s*(.*)$")

# The two the player or the collision detector raises directly. Everything else is an
# auto-detected failure class; the distinction matters for the failure table, where a
# player-indicated failure is ground truth and an auto tag is an inference.
PLAYER_MARKER_TYPES = ("AUTO-COLLISION", "PLAYER-INDICATED-FAILURE")
TIME_RE = re.compile(r"t=(\d{2}):(\d{2}):(\d{2})\.(\d{3})")

NOTABLE_EVENT_KINDS = (
    "vehicle-death", "vehicle-swap", "vehicle-repair", "teleport", "autonav",
    "wedge-damage", "reverse-abort", "abort-deadlock", "stuck-locus",
    "map-data", "map-data-mismatch", "iter-version", "v2-selftest",
    # iter-36 additions
    "stuck-locus-clear", "damage-tally", "brake-standoff",
    "assist-dead-vehicle", "wrongway-junction",
    # overhaul "pin fix" batch (iter38): prove the fixes fire
    "pinfix-gates", "acc-release", "stuck-layer",
    # iter-39 L7: the events carrying the TRUE damage and the TRUE authority
    # handoffs. Their absence is why the 07-23 digest reported -201.1 total
    # against a real ~501 body + ~740 engine, and why three assist suspensions
    # that stranded the car next to a wall never appeared in the summary.
    "marker-settle", "vehicle-session", "grind-start", "grind-end",
    "collision-suppressed", "recovery-giveup", "assist-resume", "teleport-jump",
    # iter-39 Stage 1/3
    "watchdog-govern-end",
    # iter-49: the vanilla-script batch. road-facts / node-props / kerb /
    # engine-stuck are ~1 Hz samplers and stay OUT of this list on purpose —
    # at one line a second they would eat the verbatim budget the way
    # brake-episode did on 07-28. Only the transitions belong here.
    "under-fire", "gunfire-arm-veto", "engine-stuck-arm", "temp-action",
    "pullover", "pullover-fail", "drive-sequence", "drive-sequence-fail",
    "offroad-tunnel-veto", "recovery-deadend-skip", "node-hazard",
    # iter-40: brake-episode was REMOVED from this list. It has its own dedicated
    # "Brake authority" digest section, and at 103 of 162 notable events on 07-28
    # it consumed the verbatim budget and pushed four whole incidents out of
    # digest.md. Keep it out; the section below still reports it in aggregate.
    #
    # iter-40 additions — the events that name a change of authority, a real
    # damage figure, or one of the new group behaviours firing.
    "assist-suspend", "suspend-skip", "assist-stalled", "scan-stale-release",
    "iter40-gates", "grace-arbitration", "giveup-hold-end", "poly-hold-drift",
    "stuck-layer", "pivot-done", "pivot-begin",
    # error kinds: an unhandled subsystem exception was previously invisible in
    # the digest entirely.
    "subsystem-error", "probe-error", "shapetest-overflow", "reader-internal",
    # iter-42: the metadata batch. nodes-not-loaded names a path-node streaming
    # gap that used to be silently misread as "off-road"; actuation-divergence
    # names a command that never reached the vehicle; junction-disagree names a
    # shipped-junction-data mismatch against the engine's own routing.
    "iter42-gates", "nodes-not-loaded", "actuation-divergence", "junction-disagree",
    # iter-43: the queue-veto escape hatch, the per-vehicle geometry line that
    # makes size comparisons possible, and the reason a gen-dir sample was
    # skipped.
    "iter43-gates", "queue-veto-expired", "vehicle-geometry", "gen-dir-skip",
    # iter-46: combat-state names the wanted-level transitions that segregate
    # gunfire from driving cost; gate-manifest is the single source of truth for
    # which gates exist and how they were set; gate-quiet is a gate saying WHY it
    # produced no evidence, which is the difference between "unreachable" and
    # "uninstrumented" that the old census could not express.
    "combat-state", "gate-manifest", "gate-quiet",
    # iter-51. Each of these names a moment the 08-11 session could not explain:
    # lock-override = the reported steering lock was falsified by observation;
    # gate-degraded = a gate is ON but its data source is OFF, so the epoch is
    # wasted; intox-exit / intox-clip-missing = the outro played, or the clip name
    # was wrong; unrest-instigate* = the riot's combat seeds; unrest-scan = why the
    # recruit roster is the size it is, which is THE line the riot work turns on.
    "lock-override", "gate-degraded", "speed-governor",
    "intox-exit", "intox-clip-missing",
    "unrest-instigate", "unrest-instigate-end", "unrest-scan",
    "unrest-scenario-exit",
)


# iter-41: the settings introduced this iteration, all default OFF. Used to
# decide which epoch is the all-off BASELINE and to render the gate-evidence
# table. Keep in sync with EVENT iter41-gates in GTA11Y.cs.
ITER41_SETTINGS = (
    "creepClearV2", "accStandstillV2", "assistVoiceV2", "throttleAuthorityV2",
    "damageDrawdownV2", "stuckArmHysteresisV2", "offroadBrakeV2", "skewGateV2",
    "handbrakeBandV2",
)

# iter-42: same contract, one iteration on. Keep in sync with EVENT iter42-gates.
ITER42_SETTINGS = (
    "nodeStreamGuardV2", "actuationFeedbackV2", "wheelStateV2",
)

# iter-43 A4: the vehicle-geometry gate (per-vehicle wheelbase / steering lock /
# rear overhang / brake distances).
ITER43_SETTINGS = ("vehicleGeometryV2",)

# Every gate the one-gate-per-session protocol can flip, in the order a reader
# wants them. The epoch table's "gates on" column and its BASELINE detection both
# key off this — a gate missing here is invisible in the only table that
# attributes behaviour to a configuration.
TRACKED_GATE_SETTINGS = ITER41_SETTINGS + ITER42_SETTINGS + ITER43_SETTINGS + (
    "roadBoundaryV2", "groundNormalV2", "junctionDirectionsV2",
    "graceArbitrationV2",
    # iter-51. steerBiasV2 is listed because its sign was inverted for the whole
    # of iter-49/50 and the epoch table is where a session that flips it back on
    # has to be readable against one that did not.
    "steerBiasV2", "roadSpeedGovernorV2", "polyDistrustV2",
)

# Gates whose only proof of life is an event kind. A gate that is ON but silent
# is indistinguishable from one that is broken, so the digest reports the
# expected kinds explicitly as an ABSENCE row rather than omitting them.
GATE_EXPECTED_EVENTS = {
    "reverseV2": ("reverse-abort", "recovery-reverse-out"),
    "brakeDeadzoneV2": ("brake-episode",),
    "graceArbitrationV2": ("grace-arbitration", "watchdog-grace"),
    "recoverySuspendV2": ("recovery-giveup", "assist-suspend", "giveup-hold-end"),
    "recoveryTargetV2": ("recovery-search",),
    "stuckLadderV2": ("stuck-arm", "stuck-locus", "pivot-begin", "stuck-layer"),
    "roadModelV2": ("poly-hold-drift", "poly-distrust"),
    "creepClearV2": ("auto-creep", "announce-creep-resume"),
    "accStandstillV2": ("acc-release",),
    "assistVoiceV2": ("cue-suppressed", "cue-budget-drop"),
    "throttleAuthorityV2": ("throttle-arbitration", "throttle-hold-end"),
    "damageDrawdownV2": ("damage-drawdown",),
    "stuckArmHysteresisV2": ("stuck-eval", "stuck-arm", "stuck-arm-budget-hit"),
    "offroadBrakeV2": ("offroad-floor",),
    "skewGateV2": ("lookahead-suppress", "lateral-eject"),
    "handbrakeBandV2": ("brake-episode",),
    # iter-42. nodeStreamGuardV2 is the one gate whose SILENCE is the good
    # outcome — no event means no streaming gap was hit. The other two are pure
    # telemetry and prove themselves through FRAME fields, not events, so the
    # digest's Actuation/Wheel-state sections are their evidence.
    "nodeStreamGuardV2": ("nodes-not-loaded",),
    "actuationFeedbackV2": ("actuation-divergence",),
    # iter-43. junctionDirectionsV2 got its own skip-reason event because
    # "eval 4069 / fire 0" with four silent early returns was undiagnosable.
    "junctionDirectionsV2": ("junction-disagree", "gen-dir-skip"),
    "vehicleGeometryV2": ("vehicle-geometry",),
    # iter-51. steerBiasV2 emits at 0.5 Hz while it is steering, so its absence in
    # a session that ran it is a real finding. The other two are the new
    # drive-assist gates; both also emit gate-degraded when roadFactsV2 (their data
    # source) is off, which is the difference between "tested and did nothing" and
    # "could not have done anything".
    "steerBiasV2": ("steer-bias",),
    "roadSpeedGovernorV2": ("speed-governor", "gate-degraded"),
    "polyDistrustV2": ("poly-diverge", "gate-degraded"),
    "intoxDriveImpair": ("intox-state",),
    "intoxBlackoutDrive": ("intox-blackout",),
}


# iter-43 C2: below this speed Vehicle.BrakePower is not a usable actuation
# signal — see Frame.brake_not_actuated(). iter-46 0a-6: Vehicle.SteeringAngle
# has the same property, so steer_not_actuated() uses this constant too.
BRAKE_ACTUATION_MIN_SPEED = 3.0

# iter-46 0a-7: a speed band below this many frames is noise and is flagged
# [low-n] rather than quoted. 08-01's ">10 m/s" steer band held 25 frames and
# produced a 40% "dead" rate that meant nothing.
MIN_BAND_N = 200

# iter-46 0a-10: a gate whose last fire lands before this fraction of the session
# — while it kept being evaluated — is reported DEAD-MID-SESSION. Frames, not
# evals, because `reach` units are not comparable across gates until Stage 1.
DEAD_GATE_FRAC = 0.6


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
    # iter-41 T1: the ctrl* fields are the ACTUATION ground truth — what the
    # enabled control channel (which physics reads) actually held versus the
    # disabled channel (which carries the player's raw pedal). Without them no
    # generated trace can show a throttle confiscation: on 07-28 the pedal was
    # taken on 10,711 of 29,916 frames and `trace()` rendered none of it.
    __slots__ = ("f", "t", "line0", "line1", "spd", "mode", "lat", "skew",
                 "steer", "brake", "owner", "decel", "thrA", "thrP", "navC",
                 "street", "bh", "eh", "pos", "onRoad", "nativeOnRoad",
                 "ctrlB", "ctrlBd", "ctrlT", "ctrlTd", "ctrlHb", "thrSites",
                 "accBrk", "leadGap", "scanAgeMs", "grace", "suspendedMs",
                 # iter-42 A1/A3: ACHIEVED state. Everything above this line is
                 # what the mod commanded; these are what the vehicle did with it.
                 "steerAch", "steerAchDeg", "brakeAch", "thrAch",
                 "whlContact", "whlSlip", "whlMode", "whlPitch", "whlCamber",
                 # iter-42 B1/B2: MEASURED road geometry (dark — consumed by
                 # nothing in the mod; these fields are the validation evidence).
                 "roadW", "edgeL", "edgeR", "gradeDeg", "camberDeg", "gradeAhead",
                 # iter-42 C2: the engine's own routing answer vs the shipped data
                 "genDir", "genDist", "jSrc", "jDist")

    def __init__(self, f, t, line0):
        self.f, self.t, self.line0 = f, t, line0
        self.line1 = line0 + 1
        self.spd = self.lat = self.skew = self.steer = self.brake = None
        self.decel = self.thrA = self.thrP = self.navC = None
        self.bh = self.eh = None
        self.mode = self.owner = self.street = self.pos = None
        self.onRoad = self.nativeOnRoad = None
        self.ctrlB = self.ctrlBd = self.ctrlT = self.ctrlTd = self.ctrlHb = None
        self.thrSites = None
        self.accBrk = self.leadGap = self.scanAgeMs = None
        self.grace = self.suspendedMs = None
        # iter-42 A1/A3
        self.steerAch = self.steerAchDeg = self.brakeAch = self.thrAch = None
        self.whlContact = self.whlMode = None
        self.whlSlip = self.whlPitch = self.whlCamber = None
        # iter-42 B1/B2
        self.roadW = self.edgeL = self.edgeR = None
        self.gradeDeg = self.camberDeg = self.gradeAhead = None
        # iter-42 C2
        self.genDir = self.genDist = self.jSrc = self.jDist = None

    def brake_not_actuated(self):
        """Brake commanded into the control channel but the vehicle's physics-side
        brake never moved. This is the actuation-nullified signature the iter-39
        root cause wore, made directly observable instead of inferred.

        iter-43 C2: SPEED-GATED. Vehicle.BrakePower reads ~0 whenever the car is
        already stopped or creeping, so without this gate the detector reports a
        control fault for ordinary standstill braking. On 07-31 it fired on 84.1%
        of frames below 0.5 m/s and 76.7% below 2 m/s, but on only 0.8% above
        5 m/s and 0.6% above 10 m/s — i.e. the whole "52.9% of brakes are dead"
        headline was a readback artifact, and the brake channel is healthy
        (aPeak median 7.83 m/s2). Only a command ignored while the car is still
        MOVING is evidence of anything.
        """
        return (self.ctrlB is not None and self.brakeAch is not None
                and self.spd is not None and self.spd > BRAKE_ACTUATION_MIN_SPEED
                and self.ctrlB > 0.3 and self.brakeAch < 0.05)

    def steer_not_actuated(self):
        """Steer commanded but the road wheels did not follow.

        iter-46 0a-6: SPEED-GATED, mirroring brake_not_actuated above. This is
        the same readback artifact the brake detector was fixed for in iter-43,
        left in place on the steer side for one more iteration: Vehicle.
        SteeringAngle reads exactly 0.0 on a stationary car with nothing driving
        the throttle axis. Measured on 08-01: 0.0% "dead" on frames where ctrlT
        was live, 75-99% where it was not; 82.7% below 2 m/s but 7.2% in the
        5-10 m/s band, which is statistically identical to 07-31's 8.6%. The
        "43.1% of steer commands are not actuated" headline was produced
        entirely by a session that spent nearly all its time below 2 m/s.
        GTA11Y.cs:15340 carries the matching gate on the mod side.
        """
        return (self.steer is not None and self.steerAch is not None
                and self.spd is not None and self.spd > BRAKE_ACTUATION_MIN_SPEED
                and abs(self.steer) > 0.3 and abs(self.steerAch) < 0.05)

    # iter-46 0a-7: UNGATED variants, for the speed-banded table only. Banding by
    # speed IS the gate, so applying the gate inside the predicate as well makes
    # every sub-threshold band read 0.0% by construction — which hides the very
    # artifact the bands exist to expose. Never use these for a headline.
    def brake_dead_raw(self):
        return (self.ctrlB is not None and self.brakeAch is not None
                and self.ctrlB > 0.3 and self.brakeAch < 0.05)

    def steer_dead_raw(self):
        return (self.steer is not None and self.steerAch is not None
                and abs(self.steer) > 0.3 and abs(self.steerAch) < 0.05)

    def pedal_held(self):
        """True when the mod zeroed the enabled throttle channel while the
        player's raw pedal (disabled channel) was down. This is the signature of
        GTA11Y.cs:16905 and the six sibling disable-71 sites."""
        return (self.ctrlT is not None and self.ctrlTd is not None
                and self.ctrlT == 0.0 and self.ctrlTd > 0.5)

    def trace(self):
        def n(v, fmt="%.2f"):
            return (fmt % v) if v is not None else "-"
        ctl = ""
        if self.ctrlT is not None or self.ctrlB is not None:
            ctl = (f" ctl=B{n(self.ctrlB)}/{n(self.ctrlBd)}"
                   f" T{n(self.ctrlT)}/{n(self.ctrlTd)}"
                   + (f" hb={n(self.ctrlHb)}" if self.ctrlHb else "")
                   + (" HELD" if self.pedal_held() else "")
                   + (f" sites={self.thrSites}" if self.thrSites else ""))
        # iter-42 A1/A3: the achieved side, rendered right next to the commanded
        # side so an approach trace shows a nullified command in place instead of
        # requiring a second pass over the raw log.
        ach = ""
        if self.steerAch is not None or self.brakeAch is not None:
            ach = (f" ach=S{n(self.steerAch)}/B{n(self.brakeAch)}/T{n(self.thrAch)}"
                   + (" !BRAKE-DEAD" if self.brake_not_actuated() else "")
                   + (" !STEER-DEAD" if self.steer_not_actuated() else ""))
        if self.whlContact is not None:
            ach += (f" whl={self.whlContact}"
                    + (f"/{n(self.whlSlip,'%.1f')}" if self.whlSlip is not None else "")
                    + (f" {self.whlMode.upper()}" if self.whlMode else ""))
        return (f"F{self.f} {fmt_t(self.t)} spd={n(self.spd,'%.1f')} "
                f"mode={self.mode or '-'} lat={n(self.lat)} skew={n(self.skew,'%.1f')} "
                f"steer={n(self.steer)} brake={n(self.brake)} own={self.owner or '-'} "
                f"decel={n(self.decel,'%.1f')} thr={n(self.thrA)}/{n(self.thrP)} "
                f"navC={n(self.navC,'%.1f')} bh={n(self.bh,'%.0f')}{ctl}{ach} "
                f"street={self.street or '-'} pos={self.pos or '-'}")


class Marker:
    __slots__ = ("num", "kind", "typ", "f", "t", "delta", "impactSpeed",
                 "line_no", "raw", "snap0", "snap1", "frame",
                 "failClass", "street", "latAgeMs", "mode", "layer",
                 # iter-39 L2/L3/L4
                 "peakSpeed", "perceptAgeMs", "settleBh", "settleEh",
                 # iter-40 L2/L4: the real cost, and who was driving when it happened
                 "bodyDelta", "engineDelta",
                 "suspended", "suspendedMs", "grace", "wrongWay", "trueSpd",
                 # iter-46 0b-5/0b-6
                 "vehDead", "intoxSusp", "wanted",
                 # iter-49 D: gunfire attribution, straight off the header
                 "bullet", "proj",
                 # iter-52: the full snapshot tag (ENGINE-STUCK, STUCK-LAYER, ...).
                 # `typ` stays P/A for every existing consumer; `tag` is what actually
                 # names the failure now that snapshots fire at every emitter.
                 "tag", "images")

    def __init__(self):
        self.num = self.f = self.t = self.delta = self.impactSpeed = None
        self.kind = self.typ = self.raw = self.tag = None
        self.images = []
        self.line_no = self.snap0 = self.snap1 = None
        self.frame = None
        # iter-36 P0-1: self-classifying header kv (present from iter36 logs on)
        self.failClass = self.street = self.mode = None
        self.latAgeMs = self.layer = None
        # iter-39: pre-impact peak speed, perception age, and the settled damage
        # totals joined from EVENT marker-settle.
        self.peakSpeed = self.perceptAgeMs = None
        self.settleBh = self.settleEh = None
        # iter-40 L2/L4
        self.bodyDelta = self.engineDelta = None
        self.suspended = self.suspendedMs = self.grace = self.wrongWay = None
        self.trueSpd = None
        # iter-46 0b-5/0b-6
        self.vehDead = self.intoxSusp = self.wanted = None
        # iter-49 D
        self.bullet = self.proj = None

    def authority(self):
        """Who was actually driving when this marker fired.

        A failure taken while the assist was suspended or in watchdog grace is
        not the same kind of event as one it caused while driving, and treating
        them alike corrupts both the failure table and the rate trend.
        """
        # iter-46 0b-6: two authority states that reported ON. The dead-vehicle
        # latch makes the whole assist block early-return without ever setting
        # assistSuspended, and the intoxication stand-down is a deliberately
        # parallel latch — so 07-31 marker #18 read `assist=ON` fourteen seconds
        # after the vehicle was destroyed.
        if self.vehDead == "True":
            return "DEAD"
        if self.intoxSusp == "True":
            return "INTOX"
        if self.suspended == "True":
            ms = self.suspendedMs
            return "SUSP:" + ms + "ms" if ms and ms != "-" else "SUSP"
        if self.grace == "True":
            return "GRACE"
        if self.suspended is None and self.grace is None:
            return "-"      # pre-iter-40 log; the fields did not exist
        return "ON"

    def fire_tag(self):
        """Was this marker taken while under fire?

        iter-49 D: bullet=/proj= come straight off the header, so this is an
        exact per-marker fact rather than the wanted-level proxy. A marker
        tagged GUN is a firefight the driver was caught in; ranking drive-assist
        quality on it is what produced the 07-31 headline of 1552.0 units of
        "marked damage" when 932.1 of it was police gunfire.
        """
        if self.bullet == "True":
            return "GUN"
        if self.proj == "True":
            return "PRJ"
        if self.bullet is None and self.proj is None:
            return "-"      # pre-iter-49 log; the fields did not exist
        return ""

    def in_combat(self):
        """True when the marker itself recorded a non-zero wanted level.

        iter-46 0b-5: a per-marker fact, so combat damage no longer has to be
        segregated by reconstructing a timeline from speech. Falls back to None
        on pre-iter-46 logs, where the span reconstruction still applies.
        """
        if self.wanted is None:
            return None
        try:
            return int(self.wanted) > 0
        except ValueError:
            return None

    def cost(self):
        """Body+engine damage actually attributable to this marker.

        The header's healthDelta is ONE FRAME, captured before the impact has
        finished accruing, and the 1 s auto-collision cooldown then suppresses
        the follow-up frames — so it is the smallest number the event will ever
        produce. On 07-23 the two worst events of the session (163.5/349.9 and
        83.7/177.9) both reported healthDelta=-0.0. Prefer the settled totals.
        """
        if self.settleBh is not None or self.settleEh is not None:
            return (self.settleBh or 0.0) + (self.settleEh or 0.0)
        # iter-40 L2: fall back to the header's own body+engine deltas before
        # healthDelta. Those are exact; healthDelta is a saturating overall-health
        # scalar that reported -0.0 for a 248.2-point event on 07-28.
        if self.bodyDelta is not None or self.engineDelta is not None:
            return abs(self.bodyDelta or 0.0) + abs(self.engineDelta or 0.0)
        return abs(self.delta) if self.delta is not None else None


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
                # iter-42 B1/B2. Absent whenever the native refused, so None here
                # means "no reading", never zero.
                cur.roadW = fnum_of(s, "roadW")
                cur.edgeL = fnum_of(s, "edgeL")
                cur.edgeR = fnum_of(s, "edgeR")
                cur.gradeDeg = fnum_of(s, "gradeDeg")
                cur.camberDeg = fnum_of(s, "camberDeg")
                cur.gradeAhead = fnum_of(s, "gradeAheadDeg")
            elif s.startswith("mode:"):
                cur.mode = kv(s, "drive")
            elif s.startswith("decision:"):
                # iter-39 L6/L7: "steer" must mean the command that reached the
                # wheel. This used to read smoothedSteer — the obstacle-AVOIDANCE
                # term only — so the excerpts showed steer=0.01 while the wheel
                # was getting ~0.81, and any "the assist wasn't steering" reading
                # off an excerpt was unsafe. Prefer liveSteer (what was actually
                # written to control 59), fall back to steerCmd, then the old field
                # for pre-iter-39 logs.
                cur.steer = (fnum_of(s, "liveSteer"))
                if cur.steer is None:
                    cur.steer = fnum_of(s, "steerCmd")
                if cur.steer is None:
                    cur.steer = fnum_of(s, "smoothedSteer")
                # iter-42 A1: achieved road-wheel angle. steerAch is normalised
                # against the vehicle's own steering lock so it is directly
                # comparable to liveSteer above; steerAchDeg is the raw reading.
                cur.steerAch = fnum_of(s, "steerAch")
                cur.steerAchDeg = fnum_of(s, "steerAchDeg")
            elif s.startswith("brake:"):
                cur.brake = fnum_of(s, "rampedBrake")
                cur.owner = kv(s, "owner")
                cur.decel = fnum_of(s, "decelMs2")
                # iter-41 T1. NOTE the kv() regex requires "=" immediately after
                # the key, so "ctrlB" cannot match "ctrlBd=" — the pairs are safe.
                cur.ctrlB = fnum_of(s, "ctrlB")
                cur.ctrlBd = fnum_of(s, "ctrlBd")
                cur.ctrlT = fnum_of(s, "ctrlT")
                cur.ctrlTd = fnum_of(s, "ctrlTd")
                cur.ctrlHb = fnum_of(s, "ctrlHb")
                cur.scanAgeMs = fnum_of(s, "scanAgeMs")
                cur.suspendedMs = fnum_of(s, "suspendedMs")
                cur.grace = kv(s, "grace")
                cur.thrSites = kv(s, "thrSites")   # iter-41 Stage 0.2
                # iter-42 A1: physics-side pedal state, next to the control-channel
                # readback it is meant to be compared against.
                cur.brakeAch = fnum_of(s, "brakeAch")
                cur.thrAch = fnum_of(s, "thrAch")
            elif s.startswith("throttle:"):
                cur.thrA = fnum_of(s, "applied")
                cur.thrP = fnum_of(s, "playerRaw")
                cur.accBrk = fnum_of(s, "accBrk")
                cur.leadGap = fnum_of(s, "leadGap")
            elif s.startswith("junction:"):
                # iter-42 C2. jSrc="gen" is the coverage-backstop line: neither
                # static source armed, so only the engine had an answer.
                cur.jSrc = kv(s, "src")
                cur.jDist = fnum_of(s, "dist")
                cur.genDir = fnum_of(s, "genDir")
                cur.genDist = fnum_of(s, "genDist")
            elif s.startswith("wheels:"):
                # iter-42 A3: emitted only on an abnormal frame or the 1 Hz
                # heartbeat, so most frames legitimately have no wheels: block.
                cur.whlContact = kv(s, "contact")
                cur.whlSlip = fnum_of(s, "slip")
                cur.whlMode = kv(s, "mode")
                cur.whlPitch = fnum_of(s, "pitch")
                cur.whlCamber = fnum_of(s, "camber")
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
            # iter-52: the widened MARKER_RE matches the SHAPE of a snapshot header, so
            # require the ==== rule that always precedes one. Without this guard any log
            # line of the form "WORD #123" would be promoted to a failure marker.
            mm = MARKER_RE.match(line) if (i > 0 and lines[i - 1].startswith("====")) else None
            if mm:
                mk = Marker()
                mk.tag = mm.group(2)
                mk.images = []
                mk.f = int(mm.group(1)) if mm.group(1) else (cur.f if cur else None)
                mk.typ = "P" if mm.group(2).startswith("PLAYER") else "A"
                mk.num = int(mm.group(3))
                rest = mm.group(4)
                mk.kind = kv(rest, "kind")
                mk.delta = fnum_of(rest, "healthDelta")
                # iter-40 L2: healthDelta is Vehicle.HealthFloat, a SATURATING overall
                # scalar — it read -0.0 for an event that cost 248.2 of real body+engine
                # on 07-28. bodyDelta/engineDelta are on the same header line and are
                # exact; the parser simply never read them. Keep healthDelta for the
                # historical column but carry the real cost alongside it.
                mk.bodyDelta = fnum_of(rest, "bodyDelta")
                mk.engineDelta = fnum_of(rest, "engineDelta")
                mk.impactSpeed = fnum_of(rest, "impactSpeed")
                # iter-40 L4: was the assist even driving? Half the 07-28 markers were
                # taken while suspended or in watchdog grace and no artifact showed it.
                mk.suspended = kv(rest, "suspended")
                mk.suspendedMs = kv(rest, "suspendedMs")
                mk.grace = kv(rest, "grace")
                mk.wrongWay = kv(rest, "wrongWay")
                # iter-46 0b-5/0b-6: authority states that were previously invisible,
                # and the wanted level as a per-marker fact.
                mk.vehDead = kv(rest, "vehDead")
                mk.intoxSusp = kv(rest, "intoxSusp")
                mk.wanted = kv(rest, "wanted")
                # iter-49 D: bullet=/proj= are the direct answer to the question
                # wanted= could only hint at. 932.1 of 07-31's 1552.0 marked
                # damage units were police gunfire counted as driving failures.
                mk.bullet = kv(rest, "bullet")
                mk.proj = kv(rest, "proj")
                mk.trueSpd = fnum_of(rest, "trueSpd")
                # iter-36 P0-1: lift the self-classifying header kv
                mk.failClass = kv(rest, "failClass")
                mk.street = kv(rest, "street")
                mk.mode = kv(rest, "mode")
                mk.layer = kv(rest, "layer")
                mk.latAgeMs = fnum_of(rest, "latAgeMs")
                mk.peakSpeed = fnum_of(rest, "peakSpd300")     # iter-39 L3
                mk.perceptAgeMs = fnum_of(rest, "perceptAgeMs")  # iter-39 L4
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


def _pos_xy(m):
    fr = m.frame
    if fr is None or not fr.pos:
        return None
    try:
        parts = fr.pos.strip("()").split(",")
        return float(parts[0]), float(parts[1])
    except (ValueError, IndexError):
        return None


def attach_snapshot_images(log_path, out_dir, markers):
    """Joins <log-stem>-snaps/*.jpg onto markers by sequence number and copies them
    into <out_dir>/snaps/. Returns the number copied.

    Silent when the directory does not exist — the images are a default-OFF feature,
    so their absence is the normal case and is not worth a warning."""
    import shutil
    src = log_path.parent / (log_path.stem + "-snaps")
    if not src.is_dir():
        return 0
    by_seq = {}
    for img in sorted(src.glob("snap-*.jpg")):
        m = re.match(r"snap-(\d+)-", img.name)
        if not m:
            continue
        by_seq.setdefault(int(m.group(1)), []).append(img)
    if not by_seq:
        return 0
    dst = out_dir / "snaps"
    dst.mkdir(parents=True, exist_ok=True)
    copied = 0
    for mk in markers:
        for img in by_seq.get(mk.num, []):
            target = dst / img.name
            try:
                if not target.exists() or target.stat().st_size != img.stat().st_size:
                    shutil.copy2(img, target)
                mk.images.append(img.name)
                copied += 1
            except Exception:
                pass
    return copied


def cluster_traps(clusters, radius=20.0, gap=90.0):
    """Second-pass SPATIAL clustering: incidents that are one physical trap.

    Time-only clustering split the 07-23 Calafia Rd trap into incidents 04/05/06
    — three rows within 20 m of each other inside 36 s, while a single
    stuck-locus at that spot accrued 23.4 s. Read as three separate modest
    incidents it looks like scattered bad luck; read as one trap it is the
    headline of the session.
    """
    traps = []
    for ci, cl in enumerate(clusters, 1):
        p = next((_pos_xy(m) for m in cl if _pos_xy(m) is not None), None)
        t = next((m.t for m in cl if m.t is not None), None)
        placed = False
        if p is not None and t is not None:
            for tr in traps:
                if tr["pos"] is None or tr["tlast"] is None:
                    continue
                dx, dy = p[0] - tr["pos"][0], p[1] - tr["pos"][1]
                if (dx * dx + dy * dy) ** 0.5 <= radius and (t - tr["tlast"]) <= gap:
                    tr["idx"].append(ci)
                    tr["clusters"].append(cl)
                    tr["tlast"] = t
                    placed = True
                    break
        if not placed:
            traps.append({"idx": [ci], "clusters": [cl], "pos": p,
                          "tfirst": t, "tlast": t})
    return traps


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


def frame_drops(frames):
    """[(t, drop, frame, bhDrop, ehDrop)] for every pair with a health DECREASE.

    Returns (drops, gap_skips, wreck_decay).

    Pairs spanning a >5 s wall gap (pause/alt-tab/load — the same threshold
    driving_stats uses) are skipped: health deltas across a gap are
    teleports/reloads, not driving damage. Increases (repairs) are ignored here
    and handled by the ledger.

    iter-46 0a-1: DEAD-VEHICLE GUARD. GTA11Y.cs:7290 already refuses to mark a
    drop once the hull is destroyed (`vehDeadNow`), and :7453 keeps wreck decay
    out of collision-suppressed — but this function had no equivalent, so a
    destroyed vehicle's EngineHealth bleeding from -1 to -4000 was counted as
    damage at ~1 point per frame. On 07-31 that inflated ehDrawdown to 5177 of
    a 6378 headline, roughly 4000 of which was a DUMP decaying after police
    gunfire destroyed it. EngineHealth only goes negative post-destruction, so
    the test is exact rather than heuristic. Decay is returned separately so it
    stays visible without polluting the drawdown the trend table compares on.

    iter-46 0a-2: ONE drawdown definition. The old body summed a NET
    (bh+eh) delta and dropped the pair when the net was <= 0, while the
    log-index row ran its own loop accumulating each channel's decreases
    independently — two implementations of one quantity, which is why
    digest.md said 6370.0 and log-index.json said 6378.0 for the same run.
    Both now come from here, per channel, clamped at zero.
    """
    out, skips, wreck = [], 0, 0.0
    for a, b in zip(frames, frames[1:]):
        if a.bh is None or b.bh is None or a.t is None or b.t is None:
            continue
        bh_drop = a.bh - b.bh
        if bh_drop < 0:
            bh_drop = 0.0
        eh_drop = 0.0
        if a.eh is not None and b.eh is not None:
            eh_drop = a.eh - b.eh
            if eh_drop < 0:
                eh_drop = 0.0
        drop = bh_drop + eh_drop
        if drop <= 0:
            continue
        if (b.t - a.t) > 5.0:
            skips += 1
            continue
        # Post-destruction decay, not driving damage. Mirrors GTA11Y.cs:7290.
        if (b.eh is not None and b.eh < 0) or (
                b.bh <= 0 and b.eh is not None and b.eh <= 0):
            wreck += drop
            continue
        out.append((b.t, drop, b, bh_drop, eh_drop))
    return out, skips, wreck


# iter-46 0a-5: COMBAT SEGREGATION.
#
# 07-31 lost 932 of 1552 marked points to police gunfire during a wanted-level
# chase (markers #9-#17, body 912 -> 0 at 0.5-7.6 m/s with navC=none, i.e.
# nothing within cast range to have hit). The collision classifier has no
# under-fire suppressor and IdentifyAttacker returns "none" both for "no
# attacker" and "attacker not on the scan list", so weapon damage is
# indistinguishable from grind damage and lands in the drive-assist bill.
#
# Until the mod emits a first-class combat-state event (0b-4), reconstruct the
# timeline from the speech the mod already produces. This is deliberately a
# STOPGAP: it segregates, never deletes, so a real collision during a chase
# stays visible and only stops being counted as a drive-assist failure.
WANTED_RE = re.compile(r"[Ww]anted level is now (\d+)")
HOSTILE_RE = re.compile(r"(\d+) hostiles? detected")
SPEECH_TEXT_RE = re.compile(r'text="([^"]*)"')


def combat_spans(events, end_t=None):
    """[(t0, t1, hostiles)] windows during which the player had a WANTED LEVEL.

    Only wanted-level transitions delimit a window: wanted>0 opens, wanted=0
    closes, and a window still open at the end of the log runs to `end_t`.

    iter-46, corrected: the first version also let "N hostiles detected" open and
    extend a window by a 12 s tail, and closed an unclosed window at
    `max(tail, end_t)` — which always resolved to the end of the session. On
    07-31 that manufactured a 391 s span covering markers #19-#28 and excluded
    the two highest-value real failures in the log (a 16.2 m/s OVERSHOOT and a
    GRACE lateral departure at 11.6 m/s) as "combat". The real wanted level ran
    F44506 -> F49059 and was explicitly CLEARED there; the later
    "1 hostile detected" lines are the nav-assist proximity scanner reporting a
    nearby hostile ped, which is not evidence the player is taking fire.
    Hostile counts are still collected, but only as corroboration INSIDE a
    wanted window — they can never open one.
    """
    marks, hostiles = [], []
    # iter-46 0b-4: prefer EVENT combat-state, which the mod now emits directly on
    # every wanted-level transition. The speech fallback below is fragile — it
    # regex-matches a user-facing sentence, and that sentence is suppressed
    # entirely when the neverWanted cheat is on, which would silently hide every
    # combat window in the session.
    for ev in events or []:
        if ev["kind"] == "combat-state" and ev["t"] is not None:
            w = fnum_of(ev["rest"], "wanted")
            if w is not None:
                marks.append((ev["t"], int(w)))
    if not marks:
        for ev in events or []:
            if ev["kind"] != "speech" or ev["t"] is None:
                continue
            tm = SPEECH_TEXT_RE.search(ev["rest"])
            if not tm:
                continue
            wm = WANTED_RE.search(tm.group(1))
            if wm:
                marks.append((ev["t"], int(wm.group(1))))
    for ev in events or []:
        if ev["kind"] == "speech" and ev["t"] is not None:
            tm = SPEECH_TEXT_RE.search(ev["rest"])
            if tm and HOSTILE_RE.search(tm.group(1)):
                hostiles.append(ev["t"])
    if not marks:
        return []
    marks.sort(key=lambda m: m[0])
    spans, open_t = [], None
    for t, lvl in marks:
        if lvl > 0 and open_t is None:
            open_t = t
        elif lvl == 0 and open_t is not None:
            spans.append((open_t, t))
            open_t = None
    if open_t is not None and end_t is not None and end_t > open_t:
        spans.append((open_t, end_t))
    return [(t0, t1, sum(1 for h in hostiles if t0 <= h <= t1))
            for t0, t1 in spans]


def in_spans(t, spans):
    """True when t falls inside any (t0, t1, ...) window."""
    if t is None:
        return False
    for sp in spans or []:
        if sp[0] <= t <= sp[1]:
            return True
    return False


def marker_in_combat(m, spans):
    """Was the player wanted when this marker fired?

    iter-46: prefers the per-marker `wanted=` field the mod now stamps on every
    marker header (0b-5) — an exact fact — and falls back to the reconstructed
    wanted window only for pre-iter-46 logs.
    """
    direct = m.in_combat()
    if direct is not None:
        return direct
    return in_spans(m.t, spans)


# iter-41 T2. Two tiers, because this build produces two physically different
# damage modes and a single threshold cannot see both:
#   impact — a real collision, most of the loss inside half a second
#   scrape — sustained contact bleeding ~1 point per frame for seconds
# The scrape window matches the mod's own damage-tally window (GTA11Y.cs:6766)
# so the two agree instead of contradicting each other.
DAMAGE_TIERS = (("impact", 0.5, 5.0), ("scrape", 5.0, 3.0))


def damage_episodes(frames, tiers=DAMAGE_TIERS):
    """Rolling-window damage episodes. Returns (episodes, gap_skips).

    Replaces an adjacent-frame-pair test that required a >=5.0 drop between two
    consecutive frames. Health falls in ~1.0 steps in this build, so that test
    was unreachable for EVERY episode — and the digest then printed
    "none — every damage episode has a marker" for a session with zero markers
    and 26 points of damage.
    """
    drops, gap_skips, _wreck = frame_drops(frames)
    if not drops:
        return [], gap_skips
    raw = []
    for name, win, thresh in tiers:
        j = 0
        for i in range(len(drops)):
            if j < i:
                j = i
            tot = sum(d[1] for d in drops[i:j])
            while j < len(drops) and drops[j][0] - drops[i][0] <= win:
                tot += drops[j][1]
                j += 1
            if tot >= thresh:
                raw.append({"tier": name, "i0": i, "i1": j - 1})
    if not raw:
        return [], gap_skips
    raw.sort(key=lambda r: (r["i0"], r["i1"]))
    merged = []
    for r in raw:
        if merged and r["i0"] <= merged[-1]["i1"]:
            m = merged[-1]
            m["i1"] = max(m["i1"], r["i1"])
            # An impact inside a scrape is still an impact — keep the sharper label.
            if r["tier"] == "impact":
                m["tier"] = "impact"
        else:
            merged.append(dict(r))
    eps = []
    for m in merged:
        seg = drops[m["i0"]:m["i1"] + 1]
        t0, t1 = seg[0][0], seg[-1][0]
        frs = [d[2] for d in seg]
        spds = [fr.spd for fr in frs if fr.spd is not None]
        lats = [fr.lat for fr in frs if fr.lat is not None]
        owners = {}
        for fr in frs:
            if fr.owner:
                owners[fr.owner] = owners.get(fr.owner, 0) + 1
        eps.append({
            "tier": m["tier"], "t0": t0, "t1": t1,
            "drop": sum(d[1] for d in seg),
            "frames": frs, "f0": frs[0].f, "f1": frs[-1].f,
            "durMs": (t1 - t0) * 1000.0,
            "spdMin": min(spds) if spds else None,
            "spdMax": max(spds) if spds else None,
            "latMin": min(lats) if lats else None,
            "latMax": max(lats) if lats else None,
            "offRoad": any(fr.nativeOnRoad == "False" for fr in frs),
            "owners": owners,
            "pos": frs[-1].pos, "street": frs[-1].street,
        })
    return eps, gap_skips


def unmarked_damage(frames, markers, events, window=3.0):
    """Damage episodes with NO marker within `window` seconds.

    Returns (hits, gap_skips, episodes) — `episodes` is every episode, marked or
    not, so the digest can report total drawdown independently of marker coverage.
    """
    episodes, gap_skips = damage_episodes(frames)
    hits = []
    mtimes = [m.t for m in markers if m.t is not None]
    for ep in episodes:
        b = ep["frames"][-1]
        a_bh = ep["frames"][0].bh
        ep["marked"] = any(ep["t0"] - window <= mt <= ep["t1"] + window for mt in mtimes)
        if not ep["marked"]:
            # iter-39 L7: split the note kinds by what they actually mean.
            # A repair/swap/teleport EXPLAINS a health delta that isn't driving
            # damage. collision-suppressed and damage-tally are the opposite:
            # they are the detector saying "I saw real damage and threw it away".
            # Lumping them together made "[collision-suppressed nearby]" read as
            # "known/benign" on 07-23, when it actually flagged a 5 s wall scrape
            # at 35 mph that produced no marker at all.
            # iter-49 D: being shot at explains a health delta just as
            # completely as a repair does, and unlike the others it used to be
            # invisible. An episode under fire is a firefight, not an
            # undiagnosed drive-assist failure, and must not be ranked as one.
            EXCULPATORY = ("vehicle-swap", "vehicle-session", "vehicle-repair",
                           "teleport", "vehicle-death", "under-fire")
            INCRIMINATING = ("collision-suppressed", "damage-tally",
                             "grind-start", "grind-end")
            note = ""
            for ev in events:
                if ev["t"] is None or not (ep["t0"] - window <= ev["t"] <= ep["t1"] + window):
                    continue
                if ev["kind"] in EXCULPATORY:
                    note = f" [{ev['kind']} nearby — not driving damage]"
                    break
                if ev["kind"] in INCRIMINATING:
                    reason = kv(ev["rest"], "reason")
                    note = (f" [!! {ev['kind']}"
                            + (f" reason={reason}" if reason else "")
                            + " — real damage the detector discarded]")
                    break
            ep["note"] = note
            spd = (f"{ep['spdMin']:.1f}-{ep['spdMax']:.1f}"
                   if ep["spdMax"] is not None else "-")
            lat = (f" lat={ep['latMin']:.1f}..{ep['latMax']:.1f}"
                   if ep["latMax"] is not None else "")
            own = ",".join(f"{k}:{v}" for k, v in
                           sorted(ep["owners"].items(), key=lambda x: -x[1])[:3])
            hits.append(
                f"[{ep['tier']}] F{ep['f0']}-F{ep['f1']} {fmt_t(ep['t0'])} "
                f"drop={ep['drop']:.1f} over {ep['durMs']:.0f}ms "
                f"bh {a_bh:.0f}->{b.bh:.0f} spd={spd}{lat}"
                + (" OFFROAD" if ep["offRoad"] else "")
                + (f" own={own}" if own else "")
                + f" pos={ep['pos'] or '-'} street={ep['street'] or '-'}{note}")
    return hits, gap_skips, episodes


def immobile_spans(frames, thr=0.9, spd_max=2.0, min_s=3.0, ratio=0.8, gap_s=1.0):
    """Spans where the driver was asking to move and the car was not moving.

    iter-41 T3. Computed from FRAME data alone, so it works on logs written
    before Stage 0's `immobile-under-throttle` event existed. This is the
    detector that finally makes a zero-marker session legible: on 07-28 the
    dominant behaviour of the drive produced no marker, no incident file and no
    digest row of any kind.
    """
    qual = [fr for fr in frames
            if fr.t is not None and fr.thrP is not None and fr.spd is not None
            and fr.thrP >= thr and fr.spd < spd_max]
    if not qual:
        return []
    groups = [[qual[0]]]
    for fr in qual[1:]:
        if fr.t - groups[-1][-1].t <= gap_s:
            groups[-1].append(fr)
        else:
            groups.append([fr])
    out = []
    for g in groups:
        t0, t1 = g[0].t, g[-1].t
        if (t1 - t0) < min_s:
            continue
        span = frames_in(frames, t0, t1)
        if not span or len(g) / len(span) < ratio:
            continue
        held = sum(1 for fr in span if fr.pedal_held())
        owners = {}
        for fr in span:
            if fr.owner:
                owners[fr.owner] = owners.get(fr.owner, 0) + 1
        out.append({"t0": t0, "t1": t1, "dur": t1 - t0, "frames": span,
                    "f0": g[0].f, "f1": g[-1].f, "held": held,
                    "heldFrac": held / len(span) if span else 0.0,
                    "owners": owners, "pos": g[0].pos, "street": g[0].street})
    return out


def synth_markers(frames, events, episodes, immobiles, cap=12):
    """Marker-shaped rows for non-marker anomalies, tagged typ='S'.

    These reuse the whole cluster/excerpt pipeline so a session with zero real
    markers still emits trace files. They are EXCLUDED from marker counts and
    rates everywhere — a synthetic incident is a reading aid, not a failure.
    """
    cands = []
    for sp in immobiles:
        cands.append((sp["dur"], "immobile", sp["t0"], sp["f0"],
                      f"immobile-under-throttle {sp['dur']:.1f}s "
                      f"F{sp['f0']}-F{sp['f1']} pedalHeld={sp['heldFrac']*100:.0f}% "
                      f"own={','.join(f'{k}:{v}' for k, v in sorted(sp['owners'].items(), key=lambda x: -x[1])[:3])} "
                      f"street={sp['street'] or '-'} pos={sp['pos'] or '-'}", None))
    for ep in episodes:
        if ep.get("marked"):
            continue
        cands.append((ep["drop"], "damage", ep["t0"], ep["f0"],
                      f"damage-episode[{ep['tier']}] drop={ep['drop']:.1f} "
                      f"over {ep['durMs']:.0f}ms F{ep['f0']}-F{ep['f1']}"
                      + (" OFFROAD" if ep["offRoad"] else "")
                      + f" street={ep['street'] or '-'} pos={ep['pos'] or '-'}", None))
    for ev in events:
        if ev["kind"] in ("standoff-end", "throttle-hold-end"):
            ms = fnum_of(ev["rest"], "ms") or 0.0
            if ms < 3000:
                continue
            cands.append((ms / 1000.0, ev["kind"], ev["t"], ev["f"],
                          ev["line"].strip(), ev["i"]))
    # Worst-first, so the cap drops the least interesting rather than the latest.
    cands.sort(key=lambda c: -(c[0] or 0))
    out = []
    for k, (score, kind, t, f, desc, line_i) in enumerate(cands[:cap]):
        mk = Marker()
        mk.typ = "S"
        mk.kind = kind
        mk.num = 800 + k
        mk.f = f
        mk.t = t
        mk.failClass = kind.upper()
        mk.raw = f"SYNTHETIC #{800+k} ({kind}) {desc}"
        mk.line_no = line_i if line_i is not None else 0
        mk.snap0 = line_i if line_i is not None else 0
        mk.snap1 = (line_i + 1) if line_i is not None else 0
        mk.frame = frame_at(frames, t) if t is not None else None
        if mk.frame is not None:
            mk.street = mk.frame.street
        out.append(mk)
    out.sort(key=lambda m: (m.t if m.t is not None else 0))
    return out


# iter-46 1-2: gate-manifest supersedes the five hand-written per-iteration
# emitters and names every entry of the mod's GATE_SETTINGS array, so the tool
# no longer has to keep its own copy of the list in sync. It is listed FIRST so
# gate_state_at_start prefers it; the legacy kinds stay for one iteration so an
# archived log still parses.
ITER41_GATE_EVENTS = ("gate-manifest", "iter40-gates", "iter41-gates",
                      "pinfix-gates", "iter42-gates", "iter43-gates")


def gate_state_at_start(events):
    """{setting: value} as of the FIRST emission of each gates event.

    iter-43 D2: the mod re-emits `EVENT iter41-gates` after every gate-toggle (16
    times on 07-31). This used to fold every one of them into `state`, so the
    LAST — all-gates-on — snapshot was applied retroactively as the session's
    starting state, and build_epochs then reported epoch 1 with all nine iter-41
    gates ON when the F0 line plainly says all nine were 0. That silently
    destroyed the entire point of the one-gate-per-session protocol: every epoch
    row looked identical and none could be read as a baseline. Take the first
    emission of each kind only; the chronological gate-toggle events are what
    move the state forward from there.
    """
    state = {}
    seen_kinds = set()
    for ev in events:
        if ev["kind"] not in ITER41_GATE_EVENTS or ev["kind"] in seen_kinds:
            continue
        seen_kinds.add(ev["kind"])
        for m in re.finditer(r"(\w+)=(\S+)", ev["rest"]):
            state.setdefault(m.group(1), m.group(2))
    return state


def session_model(rest):
    """Vehicle model from a vehicle-session/vehicle-swap line.

    iter-43 D3: `kv()` is case-sensitive and only the opening
    `kind=first ... model=X` line uses a lowercase `model=`. Every subsequent
    `kind=change` line carries `oldModel=`/`newModel=`, so the old lookup
    returned None and EVERY epoch after the first reported `model=?` — which is
    exactly what made the per-vehicle comparison (the truck question) impossible
    to answer from the digest.
    """
    return kv(rest, "newModel") or kv(rest, "model")


def build_epochs(frames, events, markers, episodes, immobiles):
    """iter-41 T8: split a session into (gate config x vehicle model) epochs.

    The testing protocol is one build, one deploy, gates flipped in-game one at a
    time with a different car per configuration. Without this split every metric
    is a whole-session average across several different experiments, which is
    exactly what made iter-40's results unreadable.
    """
    tf = [fr.t for fr in frames if fr.t is not None]
    if not tf:
        return []
    state = gate_state_at_start(events)
    model = next((session_model(ev["rest"]) for ev in events
                  if ev["kind"] == "vehicle-session"), None) or "?"
    bounds = []
    seen_model = model
    for ev in sorted((e for e in events if e["t"] is not None),
                     key=lambda e: e["t"]):
        if ev["kind"] == "gate-toggle":
            nm, to = kv(ev["rest"], "name"), kv(ev["rest"], "to")
            bounds.append((ev["t"], "gate", f"{nm}={to}", nm, to))
        elif ev["kind"] in ("vehicle-session", "vehicle-swap"):
            m = session_model(ev["rest"]) or "?"
            # Only a CHANGE of car starts a new experiment. The opening
            # vehicle-session names the car we already started in, and cutting on
            # it would strand a fraction-of-a-second epoch at the session start
            # and steal BASELINE from the real first epoch.
            if m != seen_model:
                bounds.append((ev["t"], "vehicle", m, None, None))
                seen_model = m
    # Boundary times, de-duplicated, always starting at the first frame. Anything
    # inside the first second is session start-up, not an experiment boundary.
    cuts, seen = [tf[0]], {tf[0]}
    for b in bounds:
        if b[0] > tf[0] + 1.0 and b[0] not in seen:
            cuts.append(b[0])
            seen.add(b[0])
    cuts.sort()
    cuts.append(tf[-1] + 1e-6)
    eps = []
    for k in range(len(cuts) - 1):
        t0, t1 = cuts[k], cuts[k + 1]
        for b in bounds:
            if b[0] <= t0:
                if b[1] == "gate" and b[3]:
                    state[b[3]] = b[4]
                elif b[1] == "vehicle":
                    model = b[2]
        eps.append({"n": k + 1, "t0": t0, "t1": t1, "model": model,
                    "gates": dict(state), "reason": (
                        "start" if k == 0 else
                        next((b[2] for b in bounds if b[0] == t0), "?"))})
    for ep in eps:
        frs = frames_in(frames, ep["t0"], ep["t1"])
        ep["frames"] = len(frs)
        drv, _, _ = driving_stats(frs)
        ep["drvMin"] = drv / 60.0
        held = sum(1 for fr in frs if fr.pedal_held())
        rated = sum(1 for fr in frs if fr.ctrlT is not None and fr.ctrlTd is not None)
        ep["holdFrac"] = (held / rated) if rated else None
        drops, _, ep_wreck = frame_drops(frs)
        ep["bhDrawdown"] = sum(d[1] for d in drops)
        ep["wreckDecay"] = ep_wreck
        ep["damageEpisodes"] = sum(
            1 for e in episodes if ep["t0"] <= e["t0"] < ep["t1"])
        ep["markers"] = sum(
            1 for m in markers if m.typ != "S" and m.t is not None
            and ep["t0"] <= m.t < ep["t1"])
        ep["immobileSeconds"] = sum(
            sp["dur"] for sp in immobiles if ep["t0"] <= sp["t0"] < ep["t1"])
        def _cnt(kind):
            return sum(1 for ev in events if ev["kind"] == kind
                       and ev["t"] is not None and ep["t0"] <= ev["t"] < ep["t1"])
        ep["stuckArms"] = _cnt("stuck-arm")
        ep["modeChanges"] = _cnt("mode-change")
        ep["cueDrops"] = _cnt("cue-budget-drop")
        peaks = sorted(
            a for ev in events if ev["kind"] == "brake-episode"
            and ev["t"] is not None and ep["t0"] <= ev["t"] < ep["t1"]
            and (a := fnum_of(ev["rest"], "aPeak")) is not None)
        ep["medAPeak"] = peaks[len(peaks) // 2] if peaks else None
        # An epoch too short to judge must say so rather than publish a ratio
        # built on a handful of frames.
        ep["tooShort"] = ep["drvMin"] * 60 < 60
    # The all-gates-off epoch is the baseline every other row is read against.
    # iter-43 D4: this used to list ITER41_SETTINGS only, so an iter-42 gate
    # session — which is exactly what 07-31 was — showed an empty "gates on"
    # column for six of the gates actually under test, and every epoch looked
    # like a baseline. Track every gate the protocol can toggle.
    def _iter41_on(ep):
        return [k for k, v in ep["gates"].items()
                if k in TRACKED_GATE_SETTINGS and v == "1"]
    for ep in eps:
        ep["on"] = _iter41_on(ep)
    base = next((ep for ep in eps if not ep["on"] and not ep["tooShort"]), None)
    for ep in eps:
        ep["baseline"] = (base is not None and ep is base)
    return eps


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
                  tr_pre, tr_post, prefix="incident-"):
    """Two files per incident cluster:
    incident-NN.log        compact: snapshots + full-rate trace + events
    incident-NN-frames.log raw full-rate FRAME slice (grep-free archive)

    iter-41 T3: `prefix` is "incident-S" for synthetic (marker-free) clusters, so
    a reader can never mistake an anomaly trace for a real failure marker.
    """
    stem = f"{prefix}{ci:02d}"
    first, last = cluster[0], cluster[-1]
    t0 = (first.t or 0) - pre
    t1 = (last.t or 0) + post
    core = frames_in(frames, t0, t1)
    head = [f"# Incident {stem}: markers "
            f"{', '.join('#%d(%s)' % (m.num, m.typ) for m in cluster)}"]
    for m in cluster:
        head.append(f"#   {m.raw.strip()}")

    out = list(head)
    out.append(f"# Core {fmt_t(t0)}..{fmt_t(t1)} full-rate trace; outer ~10Hz. "
               f"Full FRAME blocks for the core: {stem}-frames.log")
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
    p = out_dir / f"{stem}.log"
    p.write_text("\n".join(out) + "\n", encoding="utf-8")

    raw = list(head)
    raw.append(f"# Raw full-rate slice {fmt_t(t0)}..{fmt_t(t1)} — read selectively "
               f"(offset/limit), the compact {stem}.log usually suffices.")
    if core:
        lo = core[0].line0
        hi = max(core[-1].line1, max(m.snap1 for m in cluster))
        raw.extend(lines[lo:hi])
    pf = out_dir / f"{stem}-frames.log"
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
                 unmarked, index_rows, args, gap_skips=0, traps=None,
                 episodes=None, immobiles=None, epochs=None, sclusters=None):
    out = [f"# Triage digest — {log_path.name}", ""]
    out.append(f"Generated {datetime.now():%Y-%m-%d %H:%M} by tools/triage-log.py; "
               f"raw log {log_path.stat().st_size/1048576:.1f} MB / {len(lines)} lines. "
               f"Incident excerpts + events.log live next to this file.")
    out.append("")

    # iter-46 0a-5: reconstruct the under-fire timeline once, up front — the
    # failure table, the attribution arithmetic and the combat section all key
    # off it.
    _last_t = next((fr.t for fr in reversed(frames) if fr.t is not None), None)
    cspans = combat_spans(events, end_t=_last_t)

    out.append("## Session")
    it = header.get("iter") or "?"
    out.append(f"- build banner: {it}")
    for ev in events:
        if ev["kind"] in ("iter-version", "map-data", "v2-selftest", "map-data-mismatch"):
            out.append(f"- {ev['line'].strip()}")
    tf = [fr.t for fr in frames if fr.t is not None]
    driving, gaps, fps_counts = driving_stats(frames)
    # iter-41 T5: `len(frames)` counts FRAME *lines*, but the logger thins them
    # while parked, so it under-counts real engine frames (33,094 vs 34,340 on
    # 07-28). The engine's own count is on the last EVENT clock. Prefer
    # per-driving-minute rates, which are timestamp-based and unaffected.
    eng = None
    for ev in events:
        if ev["kind"] == "clock":
            v = fnum_of(ev["rest"], "frames")
            if v is not None:
                eng = int(v)
    if tf:
        thin = (f", {eng - len(frames)} thinned" if eng and eng > len(frames) else "")
        out.append(f"- frameLines: {len(frames)}"
                   + (f" / engineFrames: {eng}{thin}" if eng else "")
                   + f"  span {fmt_t(tf[0])} -> {fmt_t(tf[-1])} "
                   f"({(tf[-1]-tf[0])/60:.1f} min wall, {driving/60:.1f} min driving)")
    else:
        out.append(f"- frameLines: {len(frames)} (no timestamps in this log format)")
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
    # iter-39 L2: report the SETTLED cost. The old line summed the header's
    # single-frame healthDelta, which is captured before the impact has finished
    # accruing and is then protected by the 1 s cooldown — on 07-23 it reported
    # -201.1 for a session that actually cost ~501 body + ~740 engine, and the
    # two worst events in it summed as 0.0.
    costs = [c for m in markers if (c := m.cost()) is not None]
    if costs:
        out.append(f"- marked damage (settled): total {sum(costs):.1f}, worst {max(costs):.1f}")
    if deltas:
        out.append(f"- marked health loss (header healthDelta, under-reports): "
                   f"total {sum(deltas):.1f}, worst {min(deltas):.1f}")
    if last_fr and last_fr.bh is not None:
        out.append(f"- end health: bh={last_fr.bh:.0f} eh={last_fr.eh:.0f}")
    # iter-39 L8: per-vehicle segmentation. A session can silently span several
    # cars (07-23: an on-foot walk and a 4.4 km jump into a different vehicle,
    # with zero events), which makes "end health" describe a car that was not
    # driven for most of the session and makes any total meaningless.
    vsess = [ev for ev in events if ev["kind"] == "vehicle-session"]
    if vsess:
        out.append(f"- vehicles this session: {len(vsess)}")
        for ev in vsess:
            out.append(f"  - [F{ev['f']}] {ev['line'].split('vehicle-session:', 1)[-1].strip()}")
    # W1.1: shapecast health. farHits are physically-impossible results discarded
    # as "clear" (the SHVDN OutputArgument garbage-read); under shapeTestV2 they
    # should collapse from ~30% toward single digits. badNorm counts non-unit-normal
    # garbage hits rejected by the new backstop.
    st_evs = [ev for ev in events if ev["kind"] == "shapetest-stats"]
    if st_evs:
        def _stsum(key):
            return sum(int(kv(ev["line"], key) or 0) for ev in st_evs)
        s2 = _stsum("s2"); s2hit = _stsum("s2hit")
        far = _stsum("farHits"); bad = _stsum("badNorm")
        if s2:
            out.append(f"- shapecast: {s2} casts, {s2hit} hits, "
                       f"farHits {far} ({100*far/s2:.1f}% of casts), badNorm {bad}")
    # iter-39 L7: static-cast reject breakdown. The long-range forward wall cast
    # lifts its origin with a formula left over from an older cast origin, so
    # nearly every hit lands below the bumper plane and is discarded — and
    # closest-hit semantics mean each ground graze OCCLUDES the wall behind it.
    # 07-23: 6808 of 6811 rejects were belowBumper, i.e. the only long-range
    # wall sensor was operating as a ground detector. Surface it every session
    # until the lift fix (W1.2) ships.
    sr_evs = [ev for ev in events if ev["kind"] == "static-reject-summary"]
    if sr_evs:
        def _srsum(key):
            return sum(int(kv(ev["line"], key) or 0) for ev in sr_evs)
        tot = _srsum("count")
        if tot:
            out.append(f"- static-cast rejects: {tot} total — "
                       f"belowBumper {100*_srsum('belowBumper')/tot:.1f}%, "
                       f"outOfCone {100*_srsum('outOfCone')/tot:.1f}%, "
                       f"aboveOverhead {100*_srsum('aboveOverhead')/tot:.1f}%")
    # iter-39 L7: these lines match no EVENT branch, so they never reached
    # events.log or the histogram — yet they are the only surviving samples of
    # hit normals and relative height in the whole log.
    n_static = sum(1 for ln in lines if "shapecast: STATIC" in ln)
    if n_static:
        out.append(f"- shapecast STATIC diagnostic lines: {n_static} "
                   f"(grep the raw log for 'shapecast: STATIC' — normalZ/relZ samples)")
    out.append("")

    # iter-39 Stage 1B: THE VERDICT SECTION. Achieved deceleration per braking
    # episode, bucketed by commanded magnitude, measured from real speed deltas.
    # Before actuationV2 this is flat at ~1.2-1.7 m/s2 across every bucket, because
    # the brake command was written into a disabled control and never reached the
    # car. A working brake channel shows a dose-response: the top bucket should sit
    # at least 3 m/s2 below the bottom one, and reach >= 4.5 m/s2 near full command.
    bep = [ev for ev in events if ev["kind"] == "brake-episode"]
    if bep:
        out.append("## Brake authority (EVENT brake-episode — the Stage 1 verdict)")
        buckets = {"peakCmd 0.3-0.6": [], "peakCmd 0.6-0.9": [], "peakCmd >=0.9": []}
        for ev in bep:
            pc = fnum_of(ev["rest"], "peakCmd")
            a = fnum_of(ev["rest"], "aAch")
            if pc is None or a is None:
                continue
            k = ("peakCmd >=0.9" if pc >= 0.9
                 else "peakCmd 0.6-0.9" if pc >= 0.6 else "peakCmd 0.3-0.6")
            buckets[k].append(a)
        out.append(f"- episodes: {len(bep)}")
        # iter-41 T6/A9: aAch is dv averaged over the WHOLE episode, ramp-up and
        # ramp-down included, so buckets of different duration are not
        # comparable. On 07-28 the >=0.9 bucket's episodes ran 2.3x longer than
        # the mid bucket and its aAch "inverted" purely from that — mean dv was
        # monotonic. Report duration alongside, and prefer aPeak when present.
        durs = {}
        for ev in bep:
            pc = fnum_of(ev["rest"], "peakCmd")
            ms = fnum_of(ev["rest"], "epMs") or fnum_of(ev["rest"], "ms")
            if pc is None or ms is None:
                continue
            k = ("peakCmd >=0.9" if pc >= 0.9
                 else "peakCmd 0.6-0.9" if pc >= 0.6 else "peakCmd 0.3-0.6")
            durs.setdefault(k, []).append(ms)
        for k, v in buckets.items():
            if v:
                v = sorted(v)
                dv = sorted(durs.get(k, []))
                dtxt = f" medMs={dv[len(dv)//2]:.0f}" if dv else ""
                out.append(f"- {k}: n={len(v)} median {v[len(v)//2]:.2f} m/s2 "
                           f"(best {v[-1]:.2f}){dtxt}")
        meds = {k: sorted(v)[len(v)//2] for k, v in durs.items() if v}
        if (meds.get("peakCmd >=0.9") and meds.get("peakCmd 0.6-0.9")
                and meds["peakCmd >=0.9"] > 1.5 * meds["peakCmd 0.6-0.9"]):
            out.append("- [bucket durations differ >1.5x — aAch is a whole-episode "
                       "average and is duration-biased here; compare aPeak, not aAch]")
        neg = sum(1 for ev in bep if (fnum_of(ev["rest"], "aAch") or 0) < 0)
        if neg:
            out.append(f"- [{neg} episode(s) with aAch < 0 — the car ACCELERATED "
                       f"during a commanded brake; the metric is contaminated]")
        apk = sorted(a for ev in bep if (a := fnum_of(ev["rest"], "aPeak")) is not None)
        if apk:
            out.append(f"- aPeak (duration-independent, iter-41): n={len(apk)} "
                       f"median {apk[len(apk)//2]:.2f} m/s2 (best {apk[-1]:.2f})")
        gates = next((ev for ev in events if ev["kind"] == "pinfix-gates"), None)
        if gates:
            av = kv(gates["rest"], "actuationV2")
            if av is not None:
                out.append(f"- actuationV2={av} "
                           f"(0 = legacy: commands written into disabled controls)")
        flat = [a for v in buckets.values() for a in v]
        if flat and max(flat) < 3.0:
            out.append("- **NO BRAKE AUTHORITY DETECTED** — every episode is at "
                       "coasting-drag level. If actuationV2=1 here, the control "
                       "channel is still not reaching physics: enable "
                       "brakePhysicsAssist and re-measure.")
        out.append("")

    out.append("## Failure table")
    out.append("_cost = body+engine actually lost (EVENT marker-settle, else the header's "
               "bodyDelta+engineDelta). hdr = healthDelta, a SATURATING overall-health "
               "scalar that routinely reads -0.0 for a large event — do not rank on it. "
               "impactSpd is the PRE-impact peak. assist = who was driving: ON, "
               "SUSP:<ms> (assist suspended), GRACE (watchdog grace, player sovereign). "
               "iter-49: `fire` is GUN when a bullet was in the 7 m area at the marker "
               "and PRJ for a projectile inside 15 m — that damage is a firefight, not a "
               "driving failure, and must not be ranked as one. Blank means neither._")
    out.append("| # | typ | frame | time | kind | failClass | assist | fire | cost | hdr | impactSpd | street | incident |")
    out.append("|---|-----|-------|------|------|-----------|--------|------|------|-----|-----------|--------|----------|")
    for ci, cl in enumerate(clusters, 1):
        for m in cl:
            fr = m.frame
            # iter-36 P0-1: header kv beats the last-FRAME fallback; !stale flags
            # a marker whose lane model was frozen at press time. iter-39 L4: the
            # threshold is now 250 ms, not 2 s — a lane model a quarter-second old
            # is already suspect at speed, and perceptAgeMs (when the mod itself
            # reports it) covers the whole perception set, not just lat.
            street = m.street or (fr.street if fr else None) or "-"
            fc = m.failClass or "-"
            age = m.perceptAgeMs if m.perceptAgeMs is not None else m.latAgeMs
            if age is not None and age > 250:
                fc += f"!stale{age:.0f}ms"
            cost = m.cost()
            if m.settleBh is not None and m.settleEh is not None:
                cost_s = f"{cost:.1f} ({m.settleBh:.0f}b/{m.settleEh:.0f}e)"
            else:
                cost_s = f"{cost:.1f}" if cost is not None else "-"
            out.append(f"| {m.num} | {m.typ} | F{m.f} | {fmt_t(m.t)} | {m.kind or '-'} | "
                       f"{fc} | {m.authority()} | {m.fire_tag()} | {cost_s} | "
                       f"{m.delta if m.delta is not None else '-'} | "
                       f"{m.peakSpeed if m.peakSpeed is not None else (m.impactSpeed if m.impactSpeed is not None else '-')} | "
                       f"{street} | {ci:02d} |")
    out.append("")

    # iter-39 L7: physical traps — incidents that are one place, not N events.
    multi = [tr for tr in (traps or []) if len(tr["idx"]) > 1]
    if multi:
        out.append("## Traps (incidents within 20 m / 90 s — one physical place)")
        for tr in multi:
            cost = sum(c for cl in tr["clusters"] for m in cl
                       if (c := m.cost()) is not None)
            span = (tr["tlast"] - tr["tfirst"]) if (
                tr["tlast"] is not None and tr["tfirst"] is not None) else 0
            pos = f"({tr['pos'][0]:.1f},{tr['pos'][1]:.1f})" if tr["pos"] else "-"
            out.append(f"- incidents {'+'.join(f'{i:02d}' for i in tr['idx'])} "
                       f"at {pos} over {span:.0f}s — total cost {cost:.1f}")
        out.append("")

    out.append("## Incidents (one line each — detail in incidents.md, excerpts in incident-NN*.log)")
    # Never let a cap look like completeness: if some incidents got no excerpt,
    # say so here, in the one file the main session actually reads.
    if 0 < getattr(args, "max_incidents", 0) < len(clusters):
        out.append(f"- _[excerpt files written for the {args.max_incidents} "
                   f"worst-cost incidents only, of {len(clusters)}; every incident "
                   f"is still listed below and in the failure table. "
                   f"Re-run with --max-incidents 0 for all.]_")
    for ci, cl in enumerate(clusters, 1):
        first = cl[0]
        fr = first.frame
        icosts = [c for m in cl if (c := m.cost()) is not None]
        # iter-52: name the tag when it is one of the auto-detected failure classes.
        # "#12A" told you a snapshot fired; "#12A:STUCK-LAYER" tells you what fired,
        # which is the whole reason the snapshot call sites were broadened.
        def _mark(m):
            base = f"#{m.num}{m.typ}"
            if m.tag and m.tag not in PLAYER_MARKER_TYPES:
                base += ":" + m.tag
            return base + (f"({m.kind})" if m.kind else "")
        marks = "+".join(_mark(m) for m in cl)
        imgs = [n for m in cl for n in (m.images or [])]
        out.append(f"- {ci:02d} {fmt_t(first.t)} F{first.f} {marks}"
                   + (f" cost={sum(icosts):.1f}" if icosts else "")
                   + (f" failClass={first.failClass}" if first.failClass else "")
                   + f" street={first.street or (fr.street if fr else None) or '-'}"
                   + f" pos={fr.pos if fr else '-'}"
                   + (f" spd={fr.spd:.1f}" if fr and fr.spd is not None else ""))
        if imgs:
            # Named explicitly so an agent reading only digest.md knows these exist
            # and can open them directly — the Read tool renders images.
            out.append(f"  - _screenshots: {', '.join('snaps/' + n for n in sorted(imgs))}_")
    out.append("")

    # ---- iter-46: DRIVE-ASSIST COST RANKING ----
    # The digest has never answered "what should I fix first", which is the one
    # question the whole pipeline exists to serve. It listed incidents
    # chronologically and left the ranking to be re-derived by hand every
    # session — and on 07-31 that hand-derivation was done against a total that
    # was 60% police gunfire. Rank by cost, with wanted-window markers excluded
    # from the ranking but still shown, so the ordering is visible and auditable.
    ranked = []
    for ci, cl in enumerate(clusters, 1):
        clean = [m for m in cl if not marker_in_combat(m, cspans)]
        c = sum(x for m in clean if (x := m.cost()) is not None)
        cflag = sum(x for m in cl if marker_in_combat(m, cspans)
                    and (x := m.cost()) is not None)
        if c > 0 or cflag > 0:
            # Describe the cluster by its most EXPENSIVE marker, not its first.
            # Incident 16 opens with an `OTHER!stale491ms` grind and ends with the
            # 162.5 GRACE lateral departure that is the actual failure — keying
            # the table on cl[0] labelled the session's single worst incident
            # "OTHER", which is exactly the wrong pointer to hand a reader.
            worst = max(clean or cl, key=lambda m: (m.cost() or 0.0))
            ranked.append((c, cflag, ci, worst, cl[0]))
    if ranked:
        tot = sum(r[0] for r in ranked)
        ranked.sort(key=lambda r: -r[0])
        out.append("## Drive-assist cost ranking (fix in this order)")
        out.append(f"_Wanted-window markers are excluded from the `cost` column and shown "
                   f"separately. Total rankable drive-assist cost: **{tot:.1f}**._")
        out.append("| rank | incident | cost | share | combat-susp | worst marker | failClass | assist | street |")
        out.append("|------|----------|------|-------|-------------|--------------|-----------|--------|--------|")
        for rk, (c, cflag, ci, worst, first) in enumerate(ranked, 1):
            out.append(f"| {rk} | {ci:02d} | {c:.1f} | "
                       f"{(100*c/tot if tot else 0):.0f}% | "
                       f"{cflag:.1f} | #{worst.num}{worst.typ} F{worst.f} | "
                       f"{worst.failClass or '-'} | {worst.authority()} | "
                       f"{worst.street or first.street or '-'} |")
        out.append("")

    # iter-41 T3: a session with zero markers used to produce zero trace files,
    # which made the CLEANEST sessions the least legible. Synthetic incidents are
    # excluded from every marker count and rate — they are a reading aid.
    if sclusters:
        out.append("## Anomaly incidents (no marker — synthetic, excluded from rates)")
        for ci, cl in enumerate(sclusters, 1):
            for m in cl:
                fr = m.frame
                out.append(f"- S{ci:02d} {fmt_t(m.t)} F{m.f} [{m.kind}] "
                           + m.raw.split(") ", 1)[-1]
                           + (f" spd={fr.spd:.1f}" if fr and fr.spd is not None else ""))
        out.append("- _excerpts: incident-SNN.log / incident-SNN-frames.log_")
        out.append("")

    out.append("## Unmarked damage sweep (rolling: impact >=5.0/0.5s, scrape >=3.0/5.0s)")
    if gap_skips:
        out.append(f"- [pause-gap skipped: {gap_skips} drop(s) spanning a >5s wall gap "
                   f"— reload/teleport artifacts, not driving damage]")
    if unmarked:
        out.extend(f"- {h}" for h in unmarked[:40])
        if len(unmarked) > 40:
            out.append(f"- ... {len(unmarked)-40} more")
    else:
        # NEVER claim marker coverage when there are no markers. The old text
        # ("every damage episode has a marker") printed a clean bill of health
        # for a session with 0 markers and 26 points of damage.
        total_dd = sum(e["drop"] for e in (episodes or []))
        out.append(f"- none — no window reached either tier "
                   f"(session drawdown {total_dd:.1f})")
    out.append("")

    # ---- iter-41 T6: DISCARDED DAMAGE ----
    # The detector saw these and threw them away. On 07-28 these events were the
    # ONLY trace of 20.1 of the session's 26 lost body points, and they sat
    # buried in the chronological verbatim dump at the same weight as eight
    # no-op poly-hold-drift lines.
    disc = [ev for ev in events
            if ev["kind"] in ("damage-tally", "collision-suppressed")]

    # iter-46 0a-4: a "discarded" row that shares a frame with a marker, or lands
    # inside that marker's ~1 s settle window, is the SAME damage counted twice.
    # GTA11Y.cs:7452 chains `else if` to the marker-SETTLE test at :7434 rather
    # than to the marker test at :7401, so on the frame a marker fires
    # (markerSettleStartTicks == geNow, delta 0) control falls straight into the
    # suppressed branch and :7463 stamps reason=cooldown — true precisely
    # BECAUSE the marker fired. 07-31 printed sinceLastMs=604690 (10 minutes)
    # next to reason=cooldown, disproving its own reason. Segregate rather than
    # drop, so a genuine second impact inside a settle window stays visible.
    mk_win = [(m.f, m.t) for m in (markers or []) if m.t is not None]

    def _dup_marker(ev):
        for mf, mt in mk_win:
            if ev["f"] is not None and mf is not None and ev["f"] == mf:
                return True
            if ev["t"] is not None and mt <= ev["t"] <= mt + 1.05:
                return True
        return False

    # iter-46 0a-1 (second half): a discarded row logged while the hull is already
    # destroyed is wreck decay, not damage the detector threw away. 07-31's
    # `damage-tally F49111 bhDrop=475.9 window-expired` is stamped at the vehicle
    # swap and is pure post-death decay — counting it as "discarded" inflated the
    # bucket by 46x over the three genuine below-threshold hits.
    def _post_death(ev):
        if ev["t"] is None:
            return False
        # Sample backwards as well as at the stamp. A damage-tally is emitted when
        # its window EXPIRES — 07-31's 475.9 lands on the vehicle-swap frame, by
        # which point the replacement car is already in the FRAME line at
        # bh=1000, so testing only the stamp instant reports a wreck as healthy.
        offs = [0.0, 1.0, 5.0]
        win = fnum_of(ev["rest"], "winMs")
        if win:
            offs.append(win / 1000.0)
        for off in offs:
            fr = frame_at(frames, ev["t"] - off)
            if fr is None:
                continue
            if fr.eh is not None and fr.eh < 0:
                return True
            if (fr.bh is not None and fr.bh <= 0
                    and fr.eh is not None and fr.eh <= 0):
                return True
        return False

    dup = [ev for ev in disc
           if ev["kind"] == "collision-suppressed"
           and kv(ev["rest"], "reason") == "cooldown" and _dup_marker(ev)]
    dup_set = {id(ev) for ev in dup}
    dead = [ev for ev in disc if id(ev) not in dup_set and _post_death(ev)]
    dead_set = {id(ev) for ev in dead}
    real = [ev for ev in disc
            if id(ev) not in dup_set and id(ev) not in dead_set]

    if real:
        out.append("## Discarded damage (detector saw it and threw it away)")
        out.append("| src | frame | time | bhDrop | ehDrop | spd | contact | reason |")
        out.append("|-----|-------|------|--------|--------|-----|---------|--------|")
        dtot = 0.0
        for ev in real:
            r = ev["rest"]
            bd = (fnum_of(r, "bhDrop") if ev["kind"] == "damage-tally"
                  else abs(fnum_of(r, "bodyDelta") or 0.0))
            ed = (fnum_of(r, "ehDrop") if ev["kind"] == "damage-tally"
                  else abs(fnum_of(r, "engineDelta") or 0.0))
            dtot += (bd or 0.0) + (ed or 0.0)
            out.append(f"| {ev['kind']} | F{ev['f']} | {fmt_t(ev['t'])} | "
                       f"{bd or 0:.1f} | {ed or 0:.1f} | "
                       f"{kv(r,'spd') or '-'} | {kv(r,'contact') or '-'} | "
                       f"{kv(r,'reason') or 'window-expired'} |")
        out.append(f"- discarded total: {dtot:.1f}")
    else:
        dtot = 0.0

    if dup:
        dsum = sum(abs(fnum_of(ev["rest"], "bodyDelta") or 0.0)
                   + abs(fnum_of(ev["rest"], "engineDelta") or 0.0) for ev in dup)
        out.append(f"- _[{len(dup)} collision-suppressed row(s) totalling {dsum:.1f} "
                   f"were duplicates of a marker on the same frame (GTA11Y.cs:7452 "
                   f"`else if` mis-chaining) and are NOT counted as discarded: "
                   f"{', '.join('F' + str(ev['f']) for ev in dup[:12])}"
                   f"{' ...' if len(dup) > 12 else ''}]_")

    if dead:
        ddead = sum((fnum_of(ev["rest"], "bhDrop") or 0.0)
                    + (fnum_of(ev["rest"], "ehDrop") or 0.0)
                    if ev["kind"] == "damage-tally"
                    else abs(fnum_of(ev["rest"], "bodyDelta") or 0.0)
                    + abs(fnum_of(ev["rest"], "engineDelta") or 0.0) for ev in dead)
        out.append(f"- _[{len(dead)} row(s) totalling {ddead:.1f} were logged after the hull "
                   f"was destroyed — wreck decay, not discarded damage: "
                   f"{', '.join('F' + str(ev['f']) for ev in dead[:12])}"
                   f"{' ...' if len(dead) > 12 else ''}]_")

    # iter-46 0a-3: the residual used to subtract ONLY the discarded total and
    # never the MARKED damage, so it reported "attributed to NOTHING" for damage
    # that had a marker sitting next to it in the same digest (08-01: 35.0
    # printed against a true figure of ~0).
    #
    # The buckets are NOT independent, and treating them as such is how the first
    # attempt at this fix drove the residual to a false 0.0:
    #   drawdown = marked + unmarked          <- the only additive split
    #   combat   is a TAG on a subset of marked, not a fourth bucket
    #   discarded OVERLAPS unmarked (it is the detector's view of the same drops)
    # So only marked and unmarked are subtracted; combat and discarded are
    # reported as memos.
    if real or dup or episodes:
        eps_all = episodes or []
        sess_dd = sum(e["drop"] for e in eps_all)
        marked = sum(c for m in (markers or []) if (c := m.cost()) is not None)
        unmarked_tot = sum(e["drop"] for e in eps_all if not e.get("marked"))
        combat_mk = [m for m in (markers or []) if marker_in_combat(m, cspans)]
        combat = sum(c for m in combat_mk if (c := m.cost()) is not None)
        _, _, wreck = frame_drops(frames)
        out.append("")
        out.append("### Damage attribution")
        out.append(f"- session bh+eh drawdown (excl. wreck decay): {sess_dd:.1f}")
        out.append(f"- marked (sum of marker costs): {marked:.1f}")
        if cspans and combat > 0:
            out.append(f"  - of which inside a WANTED-LEVEL window (combat-suspected): "
                       f"{combat:.1f} across {len(combat_mk)} marker(s) — "
                       f"#{', #'.join(str(m.num) for m in combat_mk)}")
            out.append(f"  - **marked cost outside any wanted window: {marked - combat:.1f}"
                       f"** <- the safest drive-assist figure to rank on")
            out.append("  - _A wanted window does not make every marker in it gunfire: a "
                       "crash during a chase is still a driving failure. Check the "
                       "combat section's impact speeds before excluding any of these — "
                       "high cost at 1-3 m/s is weapon damage, cost that scales with "
                       "impact speed is a collision._")
        out.append(f"- unmarked damage episodes: {unmarked_tot:.1f}")
        residual = sess_dd - marked - unmarked_tot
        if residual < -0.05:
            # marked + unmarked slightly exceeding the drawdown means a small
            # double-count where an episode window and a marker settle window
            # overlap. Say so rather than printing a negative "unattributed".
            out.append(f"- residual attributed to NOTHING: 0.0 "
                       f"_(marked+unmarked exceed drawdown by {-residual:.1f} — "
                       f"episode/marker window overlap)_")
        else:
            out.append(f"- residual attributed to NOTHING: {residual:.1f}")
        out.append(f"- _[memo] detector-discarded rows: {dtot:.1f} — overlaps the unmarked "
                   f"total above, not an additional loss_")
        if wreck > 0:
            out.append(f"- _[memo] post-destruction wreck decay, excluded entirely: {wreck:.1f}_")
        if sess_dd > 0 and abs(residual) > max(5.0, 0.05 * sess_dd):
            out.append(f"- **MISMATCH**: {residual:.1f} of {sess_dd:.1f} is unexplained by "
                       f"markers or episodes. The attribution is incomplete — investigate "
                       f"before ranking anything on these numbers.")
    out.append("")

    # ---- iter-46 0a-5: COMBAT-ATTRIBUTED DAMAGE ----
    # A wanted window with no markers in it is noise — a brief star that cost
    # nothing. Only report windows that actually contain damage.
    cspans_costly = [sp for sp in cspans
                     if any(m.t is not None and sp[0] <= m.t <= sp[1]
                            for m in (markers or []))]
    if cspans_costly:
        out.append("## Wanted-level windows (combat-SUSPECTED damage)")
        out.append("_Delimited by wanted-level speech only, until the mod emits EVENT "
                   "combat-state (0b-4). These markers are NOT automatically excluded — "
                   "judge each by impact speed. High cost at 1-3 m/s with nothing in cast "
                   "range is weapon damage; cost that scales with impact speed is a real "
                   "collision that happened to occur during a chase._")
        for t0, t1, nh in cspans_costly:
            mk = [m for m in (markers or []) if m.t is not None and t0 <= m.t <= t1]
            mcost = sum(c for m in mk if (c := m.cost()) is not None)
            out.append(f"- **{fmt_t(t0)} -> {fmt_t(t1)}** ({t1-t0:.0f}s, "
                       f"{nh} hostile-detected cue(s)): {len(mk)} marker(s), "
                       f"cost {mcost:.1f}")
            for m in mk:
                c = m.cost()
                spd = m.impactSpeed
                # The discriminator, stated per marker rather than assumed.
                tag = ""
                if c and c > 20 and spd is not None and spd < 3.0:
                    tag = " **<- weapon signature (high cost, near-stationary)**"
                elif c and spd is not None and spd >= 8.0:
                    tag = " <- collision signature (cost at speed)"
                out.append(f"  - #{m.num}{m.typ} F{m.f} cost={c if c is not None else 0:.1f} "
                           f"impactSpd={spd if spd is not None else -1:.1f} "
                           f"failClass={m.failClass or '-'}{tag}")
        out.append("")

    # ---- iter-41 T6: THROTTLE AUTHORITY ----
    # The headline defect of 07-28: the mod zeroed the enabled throttle channel
    # on ~36% of frames with no log line anywhere. Answers "was the pedal taken,
    # from whom, for how long, why, and did the driver get told".
    rated = [fr for fr in frames if fr.ctrlT is not None and fr.ctrlTd is not None]
    if rated:
        held = [fr for fr in rated if fr.pedal_held()]
        out.append("## Throttle authority (enabled channel zeroed while the player's pedal was down)")
        out.append(f"- frames with pedal held: {len(held)} of {len(rated)} "
                   f"({100*len(held)/len(rated):.1f}%)")
        asking = [fr for fr in held if fr.thrP is not None and fr.thrP >= 0.9]
        out.append(f"- of those, player at full throttle (thrP>=0.9): {len(asking)}")
        moving = [fr for fr in asking if fr.spd is not None and fr.spd > 5.0]
        out.append(f"- of those, moving >5 m/s (not a stall — pedal taken at speed): {len(moving)}")
        ohist = {}
        for fr in held:
            if fr.owner:
                ohist[fr.owner] = ohist.get(fr.owner, 0) + 1
        if ohist:
            out.append("- brake owner while held: " + ", ".join(
                f"{k}:{v}" for k, v in sorted(ohist.items(), key=lambda x: -x[1])))
        he = [ev for ev in events if ev["kind"] == "throttle-hold-end"]
        if he:
            mss = sorted((fnum_of(ev["rest"], "ms") or 0.0) for ev in he)
            spoken = sum(1 for ev in events if ev["kind"] == "throttle-hold-end"
                         and kv(ev["rest"], "spoken") == "True")
            out.append(f"- hold episodes (EVENT throttle-hold-end): {len(he)}, "
                       f"median {mss[len(mss)//2]:.0f}ms, worst {mss[-1]:.0f}ms, "
                       f"spoken {spoken}/{len(he)}")
            for ev in sorted(he, key=lambda e: -(fnum_of(e["rest"], "ms") or 0))[:10]:
                out.append(f"  - [F{ev['f']}] {ev['line'].split('throttle-hold-end:',1)[-1].strip()}")
        else:
            out.append("- no EVENT throttle-hold-end in this log "
                       "(pre-iter-41 build: the hold is inferred from ctrlT/ctrlTd only)")
        out.append("")

    # ---- iter-42 A4: COMMANDED vs ACHIEVED ----
    # The whole point of iter-42 A1: every prior iteration could only see what was
    # written to the control channel. These rows are the first time the digest can
    # say whether the write reached the vehicle.
    acted = [fr for fr in frames if fr.brakeAch is not None or fr.steerAch is not None]
    if acted:
        out.append("## Actuation (commanded vs achieved — iter-42 A1)")
        out.append(f"- frames with achieved-state readback: {len(acted)} of {len(frames)}")

        # iter-46 0a-7: BANDED BY SPEED, WITH n, AND NO UNBANDED HEADLINE.
        # Three separate iterations have now been sent chasing a phantom control
        # fault by a single session-wide percentage: iter-39 and iter-43 on the
        # brake, iter-45 on the steer ("43.1% not actuated", which was 82.7%
        # below 2 m/s and 7.2% in the 5-10 m/s band). The readback is only
        # meaningful while the car is moving, and a session's speed profile
        # therefore sets the headline. Removing the aggregate number is a
        # STRUCTURAL fix — it makes the artifact unrepresentable rather than
        # merely re-thresholded. Any band under MIN_BAND_N is marked [low-n] and
        # must not be quoted: 08-01's >10 m/s band held 25 frames.
        def banded(rows, pred, label, extra=None):
            bands = [(0.0, 2.0), (2.0, 5.0), (5.0, 10.0), (10.0, float("inf"))]
            out.append(f"- {label}: {len(rows)} frames "
                       f"(no session-wide % — see bands; iter-46 0a-7)")
            for lo, hi in bands:
                seg = [fr for fr in rows
                       if fr.spd is not None and lo <= fr.spd < hi]
                if not seg:
                    continue
                d = [fr for fr in seg if pred(fr)]
                hi_s = "+" if hi == float("inf") else f"-{hi:g}"
                flag = "  **[low-n — do not quote]**" if len(seg) < MIN_BAND_N else ""
                out.append(f"  - {lo:g}{hi_s} m/s: n={len(seg)} dead={len(d)} "
                           f"({100*len(d)/len(seg):.1f}%){flag}")
            if extra:
                extra()

        bcmd = [fr for fr in acted
                if fr.ctrlB is not None and fr.brakeAch is not None and fr.ctrlB > 0.3]
        if bcmd:
            dead = [fr for fr in bcmd if fr.brake_not_actuated()]

            def _brake_extra():
                if dead:
                    out.append("  - worst offenders (first 5):")
                    for fr in dead[:5]:
                        out.append(f"    - F{fr.f} {fmt_t(fr.t)} ctrlB={fr.ctrlB:.2f} "
                                   f"brakeAch={fr.brakeAch:.2f} own={fr.owner or '-'} "
                                   f"spd={fr.spd if fr.spd is not None else -1:.1f} "
                                   f"pos={fr.pos or '-'}")
            banded(bcmd, lambda fr: fr.brake_dead_raw(),
                   "brake commanded >0.3", _brake_extra)
            # iter-51: the bands are correct and the headline is still misleading.
            # Vehicle.BrakePower is NOT an actuation signal below ~3 m/s — a stopped
            # car legitimately reads 0.00 under a full brake command (GTA11Y.cs, the
            # ACT_DIV_MIN_SPEED note). The 0-2 m/s row therefore reports 84% "dead"
            # on a healthy channel, and three separate iterations have already been
            # sent chasing it. Say so ON the table rather than in a comment nobody
            # reads at 3 a.m.
            out.append("  - _[iter-51: IGNORE the 0-2 and 2-5 m/s rows. BrakePower "
                       "reads 0.00 on a stopped or creeping car under ANY command, so "
                       "those rows measure the speed profile, not the brake channel. "
                       "Only 5+ m/s is evidence. The mod's own divergence detector is "
                       "speed-gated at 3 m/s for exactly this reason.]_")

        scmd = [fr for fr in acted
                if fr.steer is not None and fr.steerAch is not None and abs(fr.steer) > 0.3]
        if scmd:
            sdead = [fr for fr in scmd if fr.steer_not_actuated()]
            banded(scmd, lambda fr: fr.steer_dead_raw(), "steer commanded >0.3")
            # The dead-rate is U-shaped and BOTH ends are artifacts, for two
            # different reasons. Low speed: Vehicle.SteeringAngle reads 0.0 on a
            # stationary car (0a-6). High speed: GTA's own steering assist
            # "uses a percentage of the original steering lock" as speed rises
            # (gtamods.com/wiki/Handling.meta), so a large command legitimately
            # produces <1 deg of wheel movement and trips the <0.05 test. Only
            # the 2-10 m/s trough is evidence about the control channel.
            out.append("  - _[U-shaped by design: the sub-2 m/s end is the stationary "
                       "readback artifact, the 10+ m/s end is GTA scaling usable lock "
                       "down with speed. Read the 2-10 m/s bands, not the extremes.]_")
            # Sign convention is UNVERIFIED on the first iter-42 session: if the
            # normalisation or the sign is wrong, this row is how it shows up.
            # iter-51: BOTH sides now need real magnitude before a disagreement
            # counts. The old test required only |ach| > 0.05, so every frame near a
            # zero-crossing — where the wheel is passing through centre and its sign
            # is meaningless — was scored as a disagreement. On 08-11 that inflated
            # the count to 2514/10651 on a channel whose bulk inversion had already
            # been corrected in iter-43. A genuine sign fault shows up at LARGE
            # achieved angles, not at 0.06.
            SIGN_ACH_MIN = 0.15
            opp = [fr for fr in scmd
                   if fr.steerAch is not None and abs(fr.steerAch) > SIGN_ACH_MIN
                   and (fr.steer > 0) != (fr.steerAch > 0)]
            sign_pool = [fr for fr in scmd
                         if fr.steerAch is not None and abs(fr.steerAch) > SIGN_ACH_MIN]
            out.append(f"- steer sign disagreement (cmd vs achieved): {len(opp)} of "
                       f"{len(sign_pool)} frames with |ach| > {SIGN_ACH_MIN} — a high "
                       f"count means the sign convention or the steering-lock "
                       f"normalisation is inverted, NOT a control fault")
            mags = sorted(abs(fr.steerAch) for fr in acted if fr.steerAch is not None)
            if mags:
                out.append(f"- |steerAch| p50={mags[len(mags)//2]:.3f} "
                           f"p95={mags[int(len(mags)*0.95)]:.3f} max={mags[-1]:.3f} "
                           f"(values >>1.0 mean the lock normalisation is wrong)")

        # ---- iter-46 0a-8: PER-VEHICLE STEERING LOCK CROSS-CHECK ----
        # `EVENT vehicle-geometry` reported lockDeg ~19 for a CLIFFHANGER
        # motorcycle (mass 160), a TEZERACT, a PENUMBRA and a 35-tonne DUMP —
        # while the DUMP physically reached 40.1 deg for 200 frames. A car cannot
        # exceed its own steering lock, so that single observation falsifies
        # hd.SteeringLock without any bench test. It matters because
        # vehicleGeometryV2 divides the Stanley output by this value
        # (GTA11Y.cs, SteerNormLimitRad — the old :32489 reference here was stale
        # by several thousand lines), so a lock read 1.8x too small amplifies every
        # steering command by 1.8x. Segment by vehicle so the comparison is
        # per-model rather than session-wide.
        #
        # iter-51: the mod now LEARNS the real lock from the largest achieved angle
        # and announces the crossing as EVENT lock-override, so a LOCK SUSPECT row
        # should be accompanied by one. A suspect row with no lock-override event
        # means the correction did not engage and the row is still live.
        geo = [ev for ev in events if ev["kind"] == "vehicle-geometry"]
        if geo and any(fr.steerAchDeg is not None for fr in acted):
            out.append("")
            out.append("### Steering lock vs achieved angle (per vehicle — iter-46 0a-8)")
            out.append("| model | lockDeg (reported) | max |achDeg| | p95 | n | verdict |")
            out.append("|-------|--------------------|-----------|-----|---|---------|")
            for gi, ev in enumerate(geo):
                t0 = ev["t"]
                t1 = geo[gi + 1]["t"] if gi + 1 < len(geo) else float("inf")
                if t0 is None:
                    continue
                seg = [abs(fr.steerAchDeg) for fr in acted
                       if fr.t is not None and t0 <= fr.t < t1
                       and fr.steerAchDeg is not None]
                lock = fnum_of(ev["rest"], "lockDeg")
                model = kv(ev["rest"], "model") or "?"
                if not seg or lock is None:
                    out.append(f"| {model} | {lock if lock is not None else '-'} | - | - | "
                               f"{len(seg)} | no achDeg samples |")
                    continue
                seg.sort()
                mx, p95 = seg[-1], seg[int(len(seg) * 0.95)]
                verdict = ("**LOCK SUSPECT**" if mx > lock * 1.1
                           else "consistent")
                out.append(f"| {model} | {lock:.1f} | {mx:.1f} | {p95:.1f} | "
                           f"{len(seg)} | {verdict} |")
            out.append("_A max exceeding the reported lock by >10% means hd.SteeringLock "
                       "(SHVDN reads a hardcoded MemoryAddress+0x80) is not this vehicle's "
                       "real lock. Vehicle.SteeringAngle resolves its offset by runtime "
                       "pattern scan, so on a disagreement the ACHIEVED angle is the "
                       "trustworthy side._")

        dv = [ev for ev in events if ev["kind"] == "actuation-divergence"]
        out.append(f"- EVENT actuation-divergence: {len(dv)}")
        for ev in dv[:10]:
            out.append(f"  - [F{ev['f']}] {ev['line'].split('actuation-divergence:',1)[-1].strip()}")
        out.append("")

    # ---- iter-42 A4: WHEEL STATE ----
    wfr = [fr for fr in frames if fr.whlContact is not None]
    if wfr:
        out.append("## Wheel state (grip and ground contact — iter-42 A3)")
        out.append(f"- frames with a wheels: block: {len(wfr)} "
                   f"(emitted only when abnormal, plus a 1 Hz heartbeat)")
        spin = [fr for fr in wfr if fr.whlMode == "spin"]
        lock = [fr for fr in wfr if fr.whlMode == "lockup"]
        out.append(f"- wheelspin frames (throttle achieving nothing): {len(spin)}")
        out.append(f"- lockup frames (brake saturated, steering will not bite): {len(lock)}")
        chist = {}
        for fr in wfr:
            chist[fr.whlContact] = chist.get(fr.whlContact, 0) + 1
        # A mask that is not all-1s means a wheel is off the ground: airborne,
        # high-centred, or hung on a kerb. These are different failures that were
        # previously all just "wheelsDown=False".
        lifted = {k: v for k, v in chist.items() if "0" in k}
        if lifted:
            out.append("- contact masks with a lifted wheel: " + ", ".join(
                f"{k}:{v}" for k, v in sorted(lifted.items(), key=lambda x: -x[1])[:8]))
        else:
            out.append("- contact masks: all wheels down on every sampled frame")
        slips = sorted(fr.whlSlip for fr in wfr if fr.whlSlip is not None)
        if slips:
            out.append(f"- slip (wheelSpeed-speed) min={slips[0]:.1f} "
                       f"p50={slips[len(slips)//2]:.1f} max={slips[-1]:.1f} m/s")
        pitches = [fr.whlPitch for fr in wfr if fr.whlPitch is not None]
        if pitches:
            out.append(f"- wheel-plane pitch: min={min(pitches):.1f} max={max(pitches):.1f} deg "
                       f"(cross-check for the B2 ground-normal grade)")
        out.append("")

    # ---- iter-42 B1/B2: MEASURED ROAD GEOMETRY (dark) ----
    # These rows exist to answer one question each, and nothing else: is the
    # native reliable enough to steer on (B1), and do two independent derivations
    # of ground slope agree (B2)? Both must pass BEFORE either feeds a control path.
    onroad = [fr for fr in frames if fr.onRoad == "True"]
    rw = [fr for fr in onroad if fr.roadW is not None]
    if rw or any(fr.gradeDeg is not None for fr in frames):
        out.append("## Measured road geometry (iter-42 B1/B2 — DARK, feeds nothing)")
    if rw:
        widths = sorted(fr.roadW for fr in rw)
        # Failure rate is the gate: a native that refuses most of the time is a
        # fallback case, not a replacement for the modelled lane width.
        denom = len(onroad) or 1
        out.append(f"- B1 road boundary: {len(rw)} readings on {len(onroad)} on-road "
                   f"frames (**refusal rate {100*(denom-len(rw))/denom:.1f}%**)")
        out.append(f"- measured width p05={widths[int(len(widths)*0.05)]:.1f} "
                   f"p50={widths[len(widths)//2]:.1f} "
                   f"p95={widths[int(len(widths)*0.95)]:.1f} "
                   f"min={widths[0]:.1f} max={widths[-1]:.1f} m")
        # Frame-to-frame jitter separates a usable signal from a noisy one. A
        # stable road width should barely move between consecutive scans.
        jumps = []
        prev = None
        for fr in rw:
            if prev is not None and fr.roadW is not None:
                jumps.append(abs(fr.roadW - prev))
            prev = fr.roadW
        if jumps:
            jumps.sort()
            out.append(f"- frame-to-frame width jitter p50={jumps[len(jumps)//2]:.2f} "
                       f"p95={jumps[int(len(jumps)*0.95)]:.2f} max={jumps[-1]:.2f} m "
                       f"(large p95 == unusable for lane targeting)")
        off = [fr for fr in rw if fr.edgeL is not None and fr.edgeR is not None]
        if off:
            bias = sorted((fr.edgeR - fr.edgeL) for fr in off)
            out.append(f"- lateral position within carriageway (edgeR-edgeL) "
                       f"p50={bias[len(bias)//2]:.1f} m (0 == centred)")
    grd = [fr for fr in frames if fr.gradeDeg is not None]
    if grd:
        gs = sorted(fr.gradeDeg for fr in grd)
        out.append(f"- B2 ground normal: {len(grd)} readings; grade "
                   f"min={gs[0]:.1f} p50={gs[len(gs)//2]:.1f} max={gs[-1]:.1f} deg")
        cams = sorted(fr.camberDeg for fr in grd if fr.camberDeg is not None)
        if cams:
            out.append(f"- camber min={cams[0]:.1f} p50={cams[len(cams)//2]:.1f} "
                       f"max={cams[-1]:.1f} deg")
        # THE acceptance test: the ground-normal grade and the wheel-contact plane
        # fit are computed from entirely different data. If they agree, both are
        # trustworthy on the first session; if they do not, neither is.
        both = [fr for fr in grd if fr.whlPitch is not None]
        if both:
            diffs = sorted(abs(fr.gradeDeg - fr.whlPitch) for fr in both)
            p50 = diffs[len(diffs)//2]
            p95 = diffs[int(len(diffs)*0.95)]
            verdict = "AGREE" if p95 < 3.0 else "DISAGREE — do not promote B2"
            out.append(f"- **cross-check vs A3 wheel-plane pitch on {len(both)} frames: "
                       f"p50={p50:.2f} p95={p95:.2f} deg -> {verdict}**")
        else:
            out.append("- cross-check vs A3 wheel-plane pitch: no frames had both "
                       "(needs wheelStateV2 and groundNormalV2 on together)")
    if rw or grd:
        out.append("")

    # ---- iter-42 C2: JUNCTION COVERAGE ----
    # ---- iter-49 A1/A2/B1/C1: THE ENGINE'S OWN ROAD MODEL ----
    # These four gates ship as pure measurement, and every one of them exists to
    # answer a specific question that has been open for several iterations. The
    # section renders the ANSWERS, not the raw samples — a digest that says
    # "1,204 road-facts events" has told the reader nothing they can act on.
    rf = [ev for ev in events if ev["kind"] == "road-facts"]
    npv = [ev for ev in events if ev["kind"] == "node-props"]
    kerbs = [ev for ev in events if ev["kind"] == "kerb"]
    estk = [ev for ev in events if ev["kind"] == "engine-stuck"]
    if rf or npv or kerbs or estk:
        out.append("## Engine road model (iter-49 — vanilla-script batch, DARK)")

    if rf:
        ok = [e for e in rf if kv(e["rest"], "ok") == "True"]
        out.append(f"- **A1 road facts**: {len(ok)}/{len(rf)} samples answered "
                   f"(**refusal rate {100*(len(rf)-len(ok))/len(rf):.1f}%**)")
        # Cross-check 1: does the engine agree with the shipped graph on lanes?
        pairs = [(fnum_of(e["rest"], "lanesFwd"), fnum_of(e["rest"], "graphFwd"),
                  fnum_of(e["rest"], "lanesBwd"), fnum_of(e["rest"], "graphBwd"))
                 for e in ok]
        pairs = [p for p in pairs if all(v is not None for v in p)]
        # graphFwd==0 and graphBwd==0 together means the node-graph lookup found
        # nothing at that vertex, which is not a disagreement — exclude it or the
        # rate reports the graph's coverage instead of its accuracy.
        pairs = [p for p in pairs if not (p[1] == 0 and p[3] == 0)]
        if pairs:
            agree = sum(1 for f, gf, b, gb in pairs if f == gf and b == gb)
            pct = 100.0 * agree / len(pairs)
            verdict = ("AGREE — the shipped counts are sound"
                       if pct >= 90 else
                       "DISAGREE — the lane-offset sign is built on the graph's counts")
            out.append(f"  - cross-check 1 lane counts vs shipped graph: "
                       f"**{agree}/{len(pairs)} agree ({pct:.1f}%) -> {verdict}**")
        # Cross-check 2: is `width` a median gap or a carriageway width?
        ws = sorted(w for w in (fnum_of(e["rest"], "medianW") for e in ok)
                    if w is not None)
        if ws:
            out.append(f"  - cross-check 2 width p05={ws[int(len(ws)*0.05)]:.2f} "
                       f"p50={ws[len(ws)//2]:.2f} p95={ws[int(len(ws)*0.95)]:.2f} m "
                       f"(a MEDIAN gap sits near 0-2 m; a carriageway width near 8-20 m)")
        # Cross-check 3: how far is the engine's edge from our steering polyline?
        dv = sorted(d for d in (fnum_of(e["rest"], "polyDiverge") for e in ok)
                    if d is not None and d >= 0)
        if dv:
            p95 = dv[int(len(dv)*0.95)]
            verdict = "polyline tracks the engine" if p95 < 6.0 else \
                      "SNAP — the polyline is on a different road than the engine's edge"
            out.append(f"  - cross-check 3 polyline divergence p50={dv[len(dv)//2]:.1f} "
                       f"p95={p95:.1f} max={dv[-1]:.1f} m -> **{verdict}**")
        # The node validation R* runs and the mod never did.
        so = sum(1 for e in ok if kv(e["rest"], "switchedOff") == "True")
        ng = sum(1 for e in ok if kv(e["rest"], "gpsAllowed") == "False")
        if ok:
            out.append(f"  - nodes the mod steered against that vanilla would reject: "
                       f"switchedOff {so}/{len(ok)} ({100*so/len(ok):.1f}%), "
                       f"not-GPS-allowed {ng}/{len(ok)} ({100*ng/len(ok):.1f}%)")

    if npv:
        okn = [e for e in npv if kv(e["rest"], "ok") == "True"]
        out.append(f"- **A2 node properties**: {len(okn)}/{len(npv)} samples answered")
        # Flag census: which of the eleven documented bits actually appear.
        counts = {}
        for e in okn:
            for f in (kv(e["rest"], "flags") or "").split("|"):
                if f and f != "none":
                    counts[f] = counts.get(f, 0) + 1
        if counts:
            top = sorted(counts.items(), key=lambda kv_: -kv_[1])
            out.append("  - flags seen: "
                       + ", ".join(f"{k} {v}" for k, v in top))
        # The coverage question the whole gate exists for.
        tl = counts.get("trafficlight", 0)
        dbarm = sum(1 for e in okn if kv(e["rest"], "junctionArmed") == "True")
        if tl or dbarm:
            out.append(f"  - **traffic-light nodes seen {tl} vs shipped-source arms "
                       f"{dbarm} — the gap is the map-wide coverage nodeHazardV2 buys**")
        # The three detectors this is meant to cross-check.
        offr = sum(1 for e in okn if "offroad" in (kv(e["rest"], "flags") or ""))
        natoff = sum(1 for e in okn if kv(e["rest"], "onRoadNative") == "False")
        out.append(f"  - OFF_ROAD flag {offr} vs IS_POINT_ON_ROAD false {natoff} "
                   f"(a large split means the two disagree about what a road is)")

    if kerbs:
        okk = [e for e in kerbs if kv(e["rest"], "ok") == "True"]
        s0 = sum(1 for e in kerbs if kv(e["rest"], "s0ok") == "True")
        s1 = sum(1 for e in kerbs if kv(e["rest"], "s1ok") == "True")
        sN = sum(1 for e in kerbs if kv(e["rest"], "sNok") == "True")
        flip = sum(1 for e in okk if kv(e["rest"], "flipped") == "True")
        out.append(f"- **B1 kerb geometry**: {len(okk)}/{len(kerbs)} resolved; "
                   f"side 0 answered {s0}, side 1 answered {s1}, side -1 answered {sN}")
        if okk:
            out.append(f"  - far-kerb flips {flip}/{len(okk)} "
                       f"({100*flip/len(okk):.1f}%) — roughly half is what a "
                       f"correct near-side test looks like over mixed headings")
        d0 = sorted(d for d in (fnum_of(e["rest"], "s0d") for e in kerbs)
                    if d is not None)
        d1 = sorted(d for d in (fnum_of(e["rest"], "s1d") for e in kerbs)
                    if d is not None)
        if d0 and d1:
            out.append(f"  - node-to-kerb distance side0 p50={d0[len(d0)//2]:.1f} m, "
                       f"side1 p50={d1[len(d1)//2]:.1f} m "
                       f"(**settles the undocumented side parameter: near-equal "
                       f"means 0/1 are opposite kerbs**)")
        hw = sorted(h for h in (fnum_of(e["rest"], "halfCarriageway") for e in kerbs)
                    if h is not None)
        if hw:
            out.append(f"  - implied half-carriageway p50={hw[len(hw)//2]:.1f} m "
                       f"(compare: mod LANE_WIDTH_M 5.3, vanilla 4.2 per lane)")

    if estk:
        out.append(f"- **C1 engine stuck timers**: {len(estk)} transitions")
        tally = {}
        for e in estk:
            for field in ("probe2500", "fast7000", "slow30000"):
                v = kv(e["rest"], field)
                if v and v != "-":
                    for t in v.split(","):
                        tally[f"{field}:{t}"] = tally.get(f"{field}:{t}", 0) + 1
        if tally:
            out.append("  - type firings: "
                       + ", ".join(f"{k} {v}" for k, v in
                                   sorted(tally.items(), key=lambda kv_: -kv_[1])))
            out.append("  - to settle the undocumented type meanings, read these "
                       "against `upright=` / `onAllWheels=` / `roll=` on the same "
                       "lines: a type that only fires when upright=False is the "
                       "roll detector, one that fires at spd~0 on all wheels is a jam")
        arms = [ev for ev in events if ev["kind"] == "engine-stuck-arm"]
        if arms:
            out.append(f"  - **C2: the engine armed recovery {len(arms)} times "
                       f"where the mod's own heuristics had not**")

    if rf or npv or kerbs or estk:
        out.append("")

    # ---- iter-49 D: GUNFIRE VS DRIVING DAMAGE ----
    uf = [ev for ev in events if ev["kind"] == "under-fire"]
    vetoes = [ev for ev in events if ev["kind"] == "gunfire-arm-veto"]
    gun_markers = [m for m in markers if m.fire_tag() in ("GUN", "PRJ")]
    if uf or gun_markers:
        out.append("## Gunfire attribution (iter-49 D)")
        gun_cost = sum(c for m in gun_markers if (c := m.cost()) is not None)
        all_cost = sum(c for m in markers if (c := m.cost()) is not None)
        out.append(f"- under-fire episodes: {len(uf)}")
        out.append(f"- **markers taken under fire: {len(gun_markers)}/{len(markers)} "
                   f"carrying {gun_cost:.1f} of {all_cost:.1f} total marked damage "
                   f"— that share is a firefight, not a drive-assist failure**")
        if vetoes:
            out.append(f"- damage-arm vetoes (recovery correctly stood down): {len(vetoes)}")
        out.append("")

    # The premise of C2 is that the shipped junction data covers a small fraction
    # of the map. This section measures that fraction instead of assuming it.
    gj = [fr for fr in frames if fr.genDist is not None]
    if gj:
        out.append("## Junction coverage (iter-42 C2 — engine routing vs shipped data)")
        backstop = [fr for fr in gj if fr.jSrc == "gen"]
        both = [fr for fr in gj if fr.jSrc in ("db", "node") and fr.jDist is not None]
        out.append(f"- frames with an engine routing answer: {len(gj)} "
                   f"(requires an active GPS route)")
        out.append(f"- **of those, {len(backstop)} had NO static junction data "
                   f"({100*len(backstop)/len(gj):.1f}%) — coverage the 82-junction "
                   f"DB and the TrafficLight node fallback both miss**")
        if both:
            diffs = sorted(abs(fr.genDist - fr.jDist) for fr in both)
            out.append(f"- both sources armed on {len(both)} frames; "
                       f"distance disagreement p50={diffs[len(diffs)//2]:.1f} "
                       f"p95={diffs[int(len(diffs)*0.95)]:.1f} m")
        dirs = {}
        for fr in gj:
            if fr.genDir is not None:
                k = int(fr.genDir)
                dirs[k] = dirs.get(k, 0) + 1
        if dirs:
            # The direction-code mapping is unverified: this histogram is how it
            # gets settled. Codes 2/3 are believed to be left/right.
            out.append("- genDir code histogram (mapping UNVERIFIED — 2/3 assumed "
                       "left/right): " + ", ".join(
                           f"{k}:{v}" for k, v in sorted(dirs.items(), key=lambda x: -x[1])))
        dis = [ev for ev in events if ev["kind"] == "junction-disagree"]
        if dis:
            out.append(f"- EVENT junction-disagree: {len(dis)}")
            for ev in dis[:5]:
                out.append(f"  - [F{ev['f']}] "
                           f"{ev['line'].split('junction-disagree:',1)[-1].strip()}")
        out.append("")

    # ---- iter-42 A4: NODE STREAMING ----
    nnl = [ev for ev in events if ev["kind"] == "nodes-not-loaded"]
    if nnl:
        out.append("## Path-node streaming gaps (iter-42 C1)")
        out.append(f"- EVENT nodes-not-loaded: {len(nnl)} "
                   f"(each one is a window where a node query would previously have "
                   f"been misread as 'off-road')")
        shist = {}
        for ev in nnl:
            s = kv(ev["rest"], "site") or "?"
            shist[s] = shist.get(s, 0) + 1
        out.append("- by site: " + ", ".join(
            f"{k}:{v}" for k, v in sorted(shist.items(), key=lambda x: -x[1])))
        for ev in nnl[:10]:
            out.append(f"  - [F{ev['f']}] {ev['line'].split('nodes-not-loaded:',1)[-1].strip()}")
        out.append("")

    # ---- iter-41 T6: IMMOBILITY ----
    if immobiles:
        out.append("## Immobility under throttle (driver asking, car not moving)")
        tot = sum(sp["dur"] for sp in immobiles)
        out.append(f"- spans >=3s: {len(immobiles)}, total {tot:.1f}s")
        for sp in sorted(immobiles, key=lambda s: -s["dur"])[:10]:
            own = ", ".join(f"{k}:{v}" for k, v in
                            sorted(sp["owners"].items(), key=lambda x: -x[1])[:4])
            out.append(f"- {sp['dur']:.1f}s at {fmt_t(sp['t0'])} F{sp['f0']}-F{sp['f1']} "
                       f"pedalHeld={sp['heldFrac']*100:.0f}% street={sp['street'] or '-'} "
                       f"pos={sp['pos'] or '-'}" + (f" own={own}" if own else ""))
        out.append("")

    # ---- iter-41 T6 / iter-46 0a-9,0a-10,0a-11: GATE EVIDENCE ----
    #
    # This table is what a decision to delete a setting would rest on, and in the
    # 07-31/08-01 audit it was wrong in three independent ways:
    #
    #   0a-9  `set` ignored EVENT gate-toggle, so vehicleGeometryV2 read "off"
    #         for a session it was ON for 96% of (toggled at F2190).
    #   0a-10 only census[-1] was read, discarding the series. skewGateV2's fire
    #         count froze at 140 while eval climbed 981->2667, and
    #         junctionDirectionsV2's froze at 4048 while eval climbed
    #         8668->21595 — gates that died mid-session, both printed ACTIVE.
    #   0a-11 gates absent from GATE_EXPECTED_EVENTS were `continue`d out of the
    #         table entirely, hiding 12 of the mod's 32.
    #
    # And the verdicts themselves were unsound: WriteGateCensus prints eval:0/fire:0
    # for any ON gate with no GateReach() call, so "SILENT — never reached" was
    # indistinguishable from "nobody counted". actuationV2, which runs every frame,
    # censused eval:0/fire:0. A COUNTER-MISSING verdict now says that out loud.
    #
    # iter-51: the old "only 16 of 32" figure written here was stale. iter-46
    # Stage 1-1 instrumented nine more gates and iter-49/51 added their own, so as
    # of iter-51 GATE_SETTINGS holds 47 entries and all but a handful carry a
    # GateReach() call. The remaining uninstrumented ones name themselves in the
    # log via EVENT gate-quiet (why=no-GateReach-call-site), which is a better
    # source than a hard-coded count in this file that nobody updates — so the
    # count is no longer asserted here at all.
    gstate = gate_state_at_start(events)
    if gstate:
        census = [ev for ev in events if ev["kind"] == "gate-census"]
        # 0a-9: replay gate-toggle chronologically over the F0 state.
        final = dict(gstate)
        toggled = {}
        for ev in events:
            if ev["kind"] != "gate-toggle":
                continue
            nm, to = kv(ev["rest"], "name"), kv(ev["rest"], "to")
            if nm and to is not None:
                final[nm] = to
                toggled.setdefault(nm, []).append((ev["f"], to))
        census_end = census[-1]["f"] if census and census[-1]["f"] else None
        out.append("## Gate evidence (ON but silent == indistinguishable from broken)")
        out.append("| gate | set | reach | fire | lastFire | verdict |")
        out.append("|------|-----|-------|------|----------|---------|")
        for g in sorted(set(gstate) | set(final)):
            val = final.get(g, gstate.get(g))
            # The gates events also carry non-boolean CONFIG values on the same
            # line (brakeDeadzone=0.25, damageSensor=alwaysOn). They are not
            # gates and rendering them as "off" is misleading.
            if val not in ("0", "1"):
                continue
            # 0a-10: walk the whole series, not just the last census.
            reach = fire = veto = None
            unit = None
            last_fire_f = None
            prev_fire = None
            reach_at_last_fire = None
            for ev in census:
                spec = kv(ev["rest"], g)
                if not spec:
                    continue
                rm = re.search(r"eval:(\d+)", spec)
                fm = re.search(r"fire:(\d+)", spec)
                # iter-46 1-4/1-5: a gate whose guarded action is a REFUSAL counts
                # vetoes separately, so `fire` can no longer be read backwards.
                vm = re.search(r"veto:(\d+)", spec)
                # iter-46 1-9: the denominator `reach` is counted in.
                um = re.search(r"/([a-z]+)$", spec)
                if rm:
                    reach = int(rm.group(1))
                if vm:
                    veto = int(vm.group(1))
                if um:
                    unit = um.group(1)
                if fm:
                    fire = int(fm.group(1))
                    if prev_fire is None or fire > prev_fire:
                        last_fire_f = ev["f"]
                        reach_at_last_fire = reach
                    prev_fire = fire
            # 0a-11: an event-derived count, always computed, never a silent
            # substitute. A gate with fire:0 but events on the ground is a
            # missing counter, not a quiet gate.
            fire_ev = (sum(1 for ev in events if ev["kind"] in GATE_EXPECTED_EVENTS[g])
                       if g in GATE_EXPECTED_EVENTS else None)
            # iter-46 1-10: the gate's own explanation for its silence, if it gave
            # one. This is the whole point of gate-quiet — a gate that cannot say
            # why it is quiet is not instrumented, and the old table rendered that
            # indistinguishably from a genuinely unreachable gate.
            quiet = next((ev for ev in events
                          if ev["kind"] == "gate-quiet"
                          and kv(ev["rest"], "name") == g), None)
            if val != "1":
                verdict = "off"
            elif reach is None:
                verdict = ("**COUNTER-MISSING**" if fire_ev
                           else "**NO CENSUS** — uninstrumented")
            elif reach == 0:
                if quiet:
                    verdict = ("**QUIET** — " + (kv(quiet["rest"], "why") or "?"))
                else:
                    verdict = ("**COUNTER-MISSING** — gate ran but has no GateReach()"
                               if fire_ev else "**NO COUNTER** — cannot distinguish "
                               "unreached from uninstrumented")
            elif fire == 0:
                verdict = "**ON-NEVER-FIRED**"
            elif (last_fire_f is not None and reach is not None
                  and reach_at_last_fire is not None
                  and reach > reach_at_last_fire
                  and census_end and last_fire_f < DEAD_GATE_FRAC * census_end):
                # The gate kept being evaluated long after it last did anything.
                # Two things matter here:
                #  - compare against reach AT THE LAST FIRE, not the first census:
                #    junctionDirectionsV2 fired 4048 times up to F21756 and never
                #    again while eval climbed to 21595, which a first-census
                #    comparison misses entirely;
                #  - judge staleness in FRAMES, not evals. `reach` units differ
                #    per gate (per-frame / per-scan / per-cue — see E2, fixed by
                #    Stage 1's unit= token), so a flat eval threshold flagged
                #    gates that merely went quiet in the last few seconds.
                verdict = (f"**DEAD-MID-SESSION** (last fired F{last_fire_f} of "
                           f"F{census_end}, {reach - reach_at_last_fire} evals since)")
            else:
                verdict = "ACTIVE"
            if g in toggled:
                verdict += f" _[toggled x{len(toggled[g])}]_"
            fire_s = str(fire) if fire is not None else "-"
            if veto:
                fire_s += f" (veto:{veto})"
            if fire_ev is not None and (fire in (0, None)) and fire_ev:
                fire_s += f" (ev:{fire_ev})"
            reach_s = (f"{reach}/{unit}" if reach is not None and unit
                       else (str(reach) if reach is not None else "-"))
            out.append(f"| {g} | {val} | {reach_s} | "
                       f"{fire_s} | {'F' + str(last_fire_f) if last_fire_f else '-'} | "
                       f"{verdict} |")
        out.append("_`set` replays EVENT gate-toggle over the F0 state (0a-9). `fire` walks "
                   "the whole census series, not just the last line (0a-10). `(ev:N)` is a "
                   "count of the gate's expected event kinds — `fire:0 (ev:N)` means the "
                   "counter is missing, NOT that the gate is quiet. A gate with no "
                   "GateReach() call site says so itself via EVENT gate-quiet "
                   "(why=no-GateReach-call-site); trust that line over this table's "
                   "verdict, and never delete a gate on a QUIET row alone._")
        if not census:
            out.append("")
            out.append("_No EVENT gate-census in this log: `fire` is inferred from expected "
                       "event kinds and `reach` is unknown, so SILENT cannot be distinguished "
                       "from never-evaluated. Stage 0.3 adds the counters._")
        out.append("")

    # ---- iter-41 T6: CUE LEDGER ----
    sup = [ev for ev in events if ev["kind"] == "cue-suppressed"]
    if sup:
        out.append("## Cue ledger (speech the driver did not get)")
        by_site = {}
        for ev in sup:
            s = kv(ev["rest"], "site") or "?"
            by_site[s] = by_site.get(s, 0) + 1
        for k, v in sorted(by_site.items(), key=lambda x: -x[1]):
            out.append(f"- site {k}: {v} suppressed")
        out.append("")

    # ---- iter-41 T6: BRAKE GATE REJECTIONS ----
    brj = [ev for ev in events if ev["kind"] == "brake-reject"]
    brs = [ev for ev in events if ev["kind"] == "brake-reject-summary"]
    if brj or brs:
        out.append("## Brake gate rejections (EVENT brake-reject — what never became a brake)")
        if brj:
            reasons = {}
            near = 0
            for ev in brj:
                r = kv(ev["rest"], "reason") or kv(ev["rest"], "why") or "?"
                reasons[r] = reasons.get(r, 0) + 1
                ttc = fnum_of(ev["rest"], "ttc")
                if ttc is not None and ttc < 2.0:
                    near += 1
            out.append(f"- total {len(brj)} (1s/entity log cooldown => a floor, not a count)")
            for k, v in sorted(reasons.items(), key=lambda x: -x[1]):
                out.append(f"- {k}: {v} ({100*v/len(brj):.0f}%)")
            out.append(f"- rejections with ttc < 2.0: {near}  <-- the dangerous subset")
        for ev in brs[-3:]:
            out.append(f"- [F{ev['f']}] {ev['line'].split('brake-reject-summary:',1)[-1].strip()}")
        out.append("")

    # ---- iter-41 T8: EPOCH COMPARISON ----
    # The point of the one-build/many-gate-configs protocol. Without this every
    # number above is an average across several different experiments.
    if epochs and len(epochs) > 1:
        out.append("## Epoch comparison (gate config x vehicle — one row per experiment)")
        out.append("| # | span | drv-min | model | gates on | holdFrac | drawdown | dmgEp | mk | immobS | stuckArm | modeChg | aPeak |")
        out.append("|---|------|---------|-------|------------------|----------|----------|-------|----|--------|----------|---------|-------|")
        for ep in epochs:
            gates_on = ",".join(ep["on"]) if ep["on"] else "(none = BASELINE)"
            if ep["tooShort"]:
                gates_on += " _(too short to judge)_"
            hf = f"{ep['holdFrac']*100:.0f}%" if ep["holdFrac"] is not None else "-"
            ap = f"{ep['medAPeak']:.1f}" if ep["medAPeak"] is not None else "-"
            out.append(
                f"| E{ep['n']:02d} | {fmt_t(ep['t0'])} | {ep['drvMin']:.1f} | "
                f"{ep['model']} | {gates_on} | {hf} | {ep['bhDrawdown']:.1f} | "
                f"{ep['damageEpisodes']} | {ep['markers']} | "
                f"{ep['immobileSeconds']:.0f} | {ep['stuckArms']} | "
                f"{ep['modeChanges']} | {ap} |")
        base = next((e for e in epochs if e.get("baseline")), None)
        if base is None:
            out.append("")
            out.append("_No all-gates-off epoch in this session: nothing to read the other "
                       "epochs against. Drive the BASELINE first, every session._")
        out.append("")

    # ---- iter-40 L2: DAMAGE LEDGER ----
    # The sweep above is a consecutive-FRAME difference that also skips anything
    # within +/-3 s of a marker, so it can never contradict the marker set: its
    # "none" is a tautology unless corroborated. Reconcile total marked damage
    # against an independent source — the vehicle-repair events, which report the
    # health that was restored and therefore the health that had been lost.
    repairs = [ev for ev in events if ev["kind"] == "vehicle-repair"]
    if repairs or clusters:
        out.append("## Damage ledger (independent check on marker coverage)")
        marked = sum(c for c in (m.cost() for cl in clusters for m in cl)
                     if c is not None)
        rep_b = sum(fnum_of(ev["line"], "dBh") or 0.0 for ev in repairs)
        rep_e = sum(fnum_of(ev["line"], "dEh") or 0.0 for ev in repairs)
        out.append(f"- marked (sum of marker costs): {marked:.1f}")
        if repairs:
            out.append(f"- repaired ({len(repairs)} x vehicle-repair): "
                       f"{rep_b:.1f} body + {rep_e:.1f} engine = {rep_b + rep_e:.1f}")
            resid = (rep_b + rep_e) - marked
            verdict = ("coverage looks complete" if abs(resid) < 10
                       else "UNACCOUNTED DAMAGE — the detectors missed something")
            out.append(f"- residual (repaired - marked): {resid:+.1f} — {verdict}")
        else:
            out.append("- no vehicle-repair events: ledger cannot be closed this "
                       "session (repair the car in-game to give the next one a "
                       "second, independent damage source)")
        out.append("")

    out.append("## Notable events (verbatim)")
    notable = [ev for ev in events if ev["kind"] in NOTABLE_EVENT_KINDS
               and ev["kind"] not in ("map-data", "iter-version", "v2-selftest")]
    if notable:
        # iter-40 L1: CAP PER KIND BEFORE THE BUDGET.
        #
        # The flat [:80] slice is chronological, so one high-frequency kind can
        # push the entire back half of a session out of the digest — and digest.md
        # is the ONLY file the main session reads. On 07-28, 103 of 162 notable
        # events were brake-episode; the section ended at F16789 and silently
        # dropped both vehicle-repair events (the whole damage ledger), a 5,862 m
        # teleport, wedge-damage, all three recovery-giveups, and every event
        # belonging to incidents 03, 04, 05 and 06 — i.e. the four incidents that
        # accounted for 62% of the session's damage.
        #
        # Keep a few of each kind so no kind can crowd out another, roll the rest
        # up by count, then spend any remaining budget in chronological order.
        PER_KIND_CAP = 3
        seen_by_kind = {}
        kept, overflow = [], []
        for ev in notable:
            k = ev["kind"]
            seen_by_kind[k] = seen_by_kind.get(k, 0) + 1
            (kept if seen_by_kind[k] <= PER_KIND_CAP else overflow).append(ev)
        kept.sort(key=lambda e: e.get("i", 0))
        budget = 80
        shown = kept[:budget]
        # Spend leftover budget on the overflow, still chronological.
        if len(shown) < budget:
            shown = shown + overflow[:budget - len(shown)]
            shown.sort(key=lambda e: e.get("i", 0))
        shown_ids = set(id(e) for e in shown)
        out.extend(f"- {ev['line'].strip()}" for ev in shown)
        # Roll up what was held back, BY KIND, so a reader can see that a kind
        # was frequent without the lines themselves eating the budget.
        held = {}
        for ev in notable:
            if id(ev) not in shown_ids:
                held[ev["kind"]] = held.get(ev["kind"], 0) + 1
        if held:
            roll = ", ".join(f"{k} x{v}" for k, v in
                             sorted(held.items(), key=lambda kv2: -kv2[1]))
            out.append(f"- [not shown, see events.log: {roll}]")
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
        out.append("_drawdown/holdFrac are marker-independent and are the only columns "
                   "comparable across clean and dirty sessions; blank = the row predates "
                   "the schema. **iter-46 0a-2: rows marked `!s3` were computed under the "
                   "OLD drawdown definition, which counted post-destruction wreck decay — "
                   "07-31 read 6378 then and 1574 now, from the same log. Do NOT read a "
                   "trend across an `!s3` boundary; re-run triage on the older log to "
                   "backfill it.**_")
        out.append("| log | iter | drv-min | markers | /min | drawdown | dmgEp | holdFrac | immobS |")
        out.append("|-----|------|---------|---------|------|----------|-------|----------|--------|")
        for r in index_rows[-8:]:
            dd = r.get("bhDrawdown")
            stale = (r.get("costSchema") or 0) < 4
            dd = (f"{dd + (r.get('ehDrawdown') or 0):.1f}" + (" !s3" if stale else "")
                  if dd is not None else "-")
            wd = r.get("wreckDecay")
            if wd:
                dd += f" (+{wd:.0f} wreck)"
            hf = r.get("holdFrac")
            hf = f"{hf*100:.0f}%" if hf is not None else "-"
            out.append(f"| {r['log']} | {r.get('iter','?')} | "
                       f"{r.get('minutes','-')} | {r.get('markers','-')} | "
                       f"{r.get('ratePerMin','-')} | {dd} | "
                       f"{r.get('damageEpisodes','-')} | {hf} | "
                       f"{r.get('immobileSeconds','-')} |")
        out.append("")
    return "\n".join(out) + "\n"


def index_row(log_path, frames, markers, clusters, header,
              episodes=None, immobiles=None, epochs=None, events=None):
    driving, _, _ = driving_stats(frames)
    deltas = [m.delta for m in markers if m.delta is not None]
    # iter-39 L2: worstCost/totalCost are the honest damage series. worstDelta/
    # totalDelta are retained only so old rows stay readable — they were built on
    # the marker header's single-frame healthDelta and systematically under-report
    # (07-23: -201.1 reported vs ~1241 body+engine actually lost). Rows written
    # before iter-39, and rows from logs with no marker-settle events, are NOT
    # comparable to new ones on the cost fields; compare ratePerMin instead, or
    # re-run triage over the archives to backfill.
    costs = [c for m in markers if (c := m.cost()) is not None]
    # iter-41 T4: MARKER-INDEPENDENT damage fields. worstCost/totalCost go null
    # the moment a session has no markers, which broke the cross-session trend
    # precisely for the good sessions — 07-28 recorded null/null for a drive that
    # lost 26 body points. endBh/endEh are NOT a usable proxy either: 07-28-222428
    # ended 1000/1000 after a mid-session repair having cost 821.5. These fields
    # are computed from frame deltas and are always emitted as numbers, never null.
    # iter-46 0a-2: ONE implementation. This used to re-derive the drawdown with
    # its own loop, which is why digest.md and log-index.json disagreed (6370.0
    # vs 6378.0) for the same session. Both now read frame_drops, which also
    # applies the dead-vehicle guard, so bh/ehDrawdown finally exclude wreck
    # decay and become comparable across sessions — the property the trend
    # table's own header claims for them.
    drops, _, wreck_decay = frame_drops(frames)
    bh_dd = sum(d[3] for d in drops)
    eh_dd = sum(d[4] for d in drops)
    eps = episodes or []
    rated = [fr for fr in frames if fr.ctrlT is not None and fr.ctrlTd is not None]
    held = sum(1 for fr in rated if fr.pedal_held())
    eng = None
    for ev in (events or []):
        if ev["kind"] == "clock":
            v = fnum_of(ev["rest"], "frames")
            if v is not None:
                eng = int(v)
    return {
        "log": log_path.name,
        "iter": header.get("iter"),
        "frames": len(frames),
        "engineFrames": eng,
        "minutes": round(driving / 60, 1),
        "markers": len(markers),
        "incidents": len(clusters),
        "ratePer1k": round(len(markers) / len(frames) * 1000, 2) if frames else None,
        "ratePerMin": round(len(markers) / (driving / 60), 2) if driving > 0 else None,
        # iter-46 0a-1/0a-2 changed what `drawdown` MEANS: it now excludes
        # post-destruction wreck decay and comes from a single implementation.
        # 07-31 went 6378 -> 1574 on the same log. Rows written under schema 3
        # are therefore NOT comparable to schema 4 rows, and the trend table
        # marks them rather than letting a reader infer a 4x improvement that
        # never happened. Re-run triage over an archived log to backfill it.
        "costSchema": 4,
        "worstCost": round(max(costs), 1) if costs else 0.0,
        "totalCost": round(sum(costs), 1) if costs else 0.0,
        # Marker-independent — comparable across clean and dirty sessions.
        "bhDrawdown": round(bh_dd, 1),
        "ehDrawdown": round(eh_dd, 1),
        # iter-46 0a-1: post-destruction decay, excluded from the drawdown above.
        # Non-zero here means a vehicle was destroyed during the session, so the
        # marker costs are not a drive-assist bill.
        "wreckDecay": round(wreck_decay, 1),
        "worstWindowDrop": round(max((e["drop"] for e in eps), default=0.0), 1),
        "damageEpisodes": len(eps),
        "unmarkedDrawdown": round(
            sum(e["drop"] for e in eps if not e.get("marked")), 1),
        "holdFrac": round(held / len(rated), 3) if rated else None,
        "immobileSeconds": round(sum(sp["dur"] for sp in (immobiles or [])), 1),
        "gateEpochs": len(epochs or []),
        "epochs": [
            {"n": e["n"], "model": e["model"], "on": e["on"],
             "drvMin": round(e["drvMin"], 1),
             "holdFrac": round(e["holdFrac"], 3) if e["holdFrac"] is not None else None,
             "bhDrawdown": round(e["bhDrawdown"], 1),
             "markers": e["markers"], "immobileSeconds": round(e["immobileSeconds"], 1)}
            for e in (epochs or [])],
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
        # iter-43 D1: write_excerpt() renders m.raw for every marker in a cluster.
        # This was never set, so ANY session containing a vehicle-death crashed the
        # whole run with AttributeError the moment the death landed in a cluster —
        # 07-31 died at incident 40 of 64 and produced no digest.md at all.
        mk.raw = ev["line"]
        mk.snap0, mk.snap1 = ev["i"], ev["i"] + 1
        mk.frame = frame_at(frames, ev["t"]) if ev["t"] is not None else None
        markers.append(mk)
    # iter-39 L2: join EVENT marker-settle back onto its marker. The header's
    # healthDelta is a single frame captured before the impact finished; the
    # settle event carries the real cost of the ~1 s that followed.
    by_num = {m.num: m for m in markers}
    for ev in events:
        if ev["kind"] != "marker-settle":
            continue
        num = fnum_of(ev["rest"], "marker")
        mk = by_num.get(int(num)) if num is not None else None
        if mk is not None:
            mk.settleBh = fnum_of(ev["rest"], "bhDrop")
            mk.settleEh = fnum_of(ev["rest"], "ehDrop")
            if mk.peakSpeed is None:
                mk.peakSpeed = fnum_of(ev["rest"], "peakSpd")
    # iter-39 L2 (legacy reconstruction): logs written before marker-settle
    # existed can still be costed, because the damage the marker header missed
    # was recorded elsewhere — the grind episode's own cumBhDrop/cumEhDrop, and
    # the collision-suppressed(reason=cooldown) events that ARE the swallowed
    # tail. Take the max of the three readings rather than the sum: grind-end
    # totals already include the frames that were separately suppressed, so
    # adding them would double-count. This is what makes the pre-iter-39
    # sessions comparable to the new ones.
    for mk in markers:
        if mk.settleBh is not None or mk.t is None:
            continue
        hdr = abs(mk.delta) if mk.delta is not None else 0.0
        ge = 0.0
        for ev in events:
            if ev["kind"] != "grind-end" or ev["t"] is None:
                continue
            if 0 <= ev["t"] - mk.t <= 3.0:
                ge = max(ge, (fnum_of(ev["rest"], "cumBhDrop") or 0.0)
                             + (fnum_of(ev["rest"], "cumEhDrop") or 0.0))
        supp = 0.0
        for ev in events:
            if ev["kind"] != "collision-suppressed" or ev["t"] is None:
                continue
            if 0 <= ev["t"] - mk.t <= 1.2 and kv(ev["rest"], "reason") == "cooldown":
                supp += abs(fnum_of(ev["rest"], "bodyDelta") or 0.0)
                supp += abs(fnum_of(ev["rest"], "engineDelta") or 0.0)
        best = max(hdr, ge, hdr + supp)
        if best > hdr:
            mk.settleBh = best      # reconstructed COMBINED body+engine total
            mk.settleEh = None      # no split available; suppresses the b/e display
    # iter-39 L3: pre-iter-39 logs have no peakSpd300, and their impactSpeed is a
    # POST-impact reading. Recover the pre-impact peak from the frames so old
    # sessions are comparable (07-23 marker #7: impactSpeed=1.5, real peak 24.8).
    for mk in markers:
        if mk.peakSpeed is None and mk.t is not None:
            win = [fr.spd for fr in frames
                   if fr.t is not None and 0 <= mk.t - fr.t <= 0.4 and fr.spd is not None]
            if win:
                mk.peakSpeed = max(win)
    clusters = cluster_markers(markers, args.cluster_gap) if markers else []
    traps = cluster_traps(clusters)

    # ---- iter-41 T2/T3/T8 ----
    # Order matters: episodes must be computed (and marked/unmarked resolved)
    # before synth_markers can decide which ones deserve a trace file.
    unmarked, gap_skips, episodes = unmarked_damage(frames, markers, events)
    immobiles = immobile_spans(frames)
    smarkers = synth_markers(frames, events, episodes, immobiles)
    sclusters = cluster_markers(smarkers, args.cluster_gap) if smarkers else []
    epochs = build_epochs(frames, events, markers, episodes, immobiles)
    print(f"   frames={len(frames)} events={len(events)} markers={len(markers)} "
          f"incidents={len(clusters)} synthetic={len(sclusters)} "
          f"dmgEpisodes={len(episodes)} epochs={len(epochs)} "
          f"iter={header.get('iter')}")

    row = index_row(log_path, frames, markers, clusters, header,
                    episodes, immobiles, epochs, events)
    index[:] = [r for r in index if r.get("log") != row["log"]]
    index.append(row)
    index.sort(key=lambda r: r.get("log", ""))

    if args.index_only:
        return

    out_dir = args.out_dir / log_path.stem
    out_dir.mkdir(parents=True, exist_ok=True)

    # iter-52: failure screenshots. The mod writes them to <log-stem>-snaps/ next to
    # the log, named snap-<seq>-<TAG>[-preN].jpg where <seq> is the same
    # collisionMarkerSeq printed in the marker header — so the join needs no extra
    # bookkeeping. Copy them in beside the excerpts, because the whole point is that
    # an analysis agent can open them without being told where to look.
    n_img = attach_snapshot_images(log_path, out_dir, markers)
    if n_img:
        print(f"   copied {n_img} failure screenshots into snaps/")

    # iter-43 D6: 07-31 was 266.7 MB and produced a 18 MB events.log — bigger than
    # some whole logs. Cap the spammiest kinds rather than the tail, so the rare
    # events (which are the interesting ones) always survive, and SAY what was
    # dropped: a silently truncated artifact reads as "covered everything".
    ev_path = out_dir / "events.log"
    ev_lines, ev_dropped = [], {}
    if args.max_per_kind > 0:
        kept_by_kind = {}
        for ev in events:
            k = ev["kind"]
            kept_by_kind[k] = kept_by_kind.get(k, 0) + 1
            if kept_by_kind[k] <= args.max_per_kind:
                ev_lines.append(ev["line"])
            else:
                ev_dropped[k] = ev_dropped.get(k, 0) + 1
    else:
        ev_lines = [ev["line"] for ev in events]
    if ev_dropped:
        roll = ", ".join(f"{k} x{v}" for k, v in
                         sorted(ev_dropped.items(), key=lambda x: -x[1]))
        ev_lines.append(f"# TRUNCATED at --max-per-kind={args.max_per_kind}: {roll}")
    ev_path.write_text("\n".join(ev_lines) + "\n", encoding="utf-8")

    # Worst-cost incidents first when capping, so the cap drops the least
    # interesting rather than simply the latest.
    inc_order = sorted(
        range(len(clusters)),
        key=lambda i: -sum(c for m in clusters[i] if (c := m.cost()) is not None))
    inc_keep = set(inc_order[:args.max_incidents]) if args.max_incidents > 0 else set(
        range(len(clusters)))
    inc_skipped = 0
    for ci, cl in enumerate(clusters, 1):
        if (ci - 1) not in inc_keep:
            inc_skipped += 1
            continue
        p, pf = write_excerpt(out_dir, ci, lines, frames, events, cl, args.pre,
                              args.post, args.trace_pre, args.trace_post)
        print(f"   wrote {p.name} ({p.stat().st_size/1024:.0f} KB) + "
              f"{pf.name} ({pf.stat().st_size/1024:.0f} KB)")
    if inc_skipped:
        print(f"   [{inc_skipped} incident(s) had no excerpt written "
              f"(--max-incidents={args.max_incidents}, kept worst-cost first)]")

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

    for ci, cl in enumerate(sclusters, 1):
        p, pf = write_excerpt(out_dir, ci, lines, frames, events, cl, args.pre,
                              args.post, args.trace_pre, args.trace_post,
                              prefix="incident-S")
        print(f"   wrote {p.name} ({p.stat().st_size/1024:.0f} KB) + "
              f"{pf.name} ({pf.stat().st_size/1024:.0f} KB)")

    digest = build_digest(log_path, lines, frames, events, markers, header,
                          clusters, unmarked, index, args, gap_skips, traps,
                          episodes, immobiles, epochs, sclusters)
    dp = out_dir / "digest.md"
    dp.write_text(digest, encoding="utf-8")
    print(f"   wrote {dp} ({dp.stat().st_size/1024:.1f} KB), events.log "
          f"({ev_path.stat().st_size/1024:.0f} KB)")


def _mk_frame(f, t, bh, eh=1000.0, spd=5.0, thrP=None, ctrlT=None, ctrlTd=None):
    fr = Frame(f, t, 0)
    fr.bh, fr.eh, fr.spd = bh, eh, spd
    fr.thrP, fr.ctrlT, fr.ctrlTd = thrP, ctrlT, ctrlTd
    return fr


def selftest():
    """iter-41 T7. Reproduces the four real damage episodes of 07-28 and asserts
    the sweep sees them. The bug this guards against printed
    "none — every damage episode has a marker" for a session with zero markers
    and 26 points of damage, because it diffed ADJACENT FRAME PAIRS against a
    threshold of 5.0 while health falls ~1.0 per frame.
    """
    fails = []

    def check(name, cond, detail=""):
        print(f"  {'PASS' if cond else 'FAIL'}  {name}" + (f" — {detail}" if detail else ""))
        if not cond:
            fails.append(name)

    # --- damage episodes -------------------------------------------------
    # The four real episodes of driveassist-2026-07-28-233606, at 60 Hz:
    #   7.0 in 0.12 s (impact)  |  15.0 bled over 3.5 s (scrape)
    #   3.0 in one frame        |  1.0 isolated (must NOT register)
    sched = ([(10.0 + i / 60.0, 1.0) for i in range(7)]
             + [(100.0 + i * 0.25, 1.0) for i in range(15)]
             + [(200.0, 3.0), (300.0, 1.0)])
    sched.sort()
    frames, bh, f, t = [], 1000.0, 0, 0.0
    while t <= 310.0:
        while sched and t >= sched[0][0]:
            bh -= sched[0][1]
            sched.pop(0)
        frames.append(_mk_frame(f, t, bh))
        f += 1
        t = f / 60.0     # derive from the counter so error cannot accumulate
    check("harness: every scheduled drop was applied", not sched,
          f"{len(sched)} left unapplied")

    eps, _ = damage_episodes(frames)
    check("damage: exactly 3 episodes (the isolated 1.0 is correctly ignored)",
          len(eps) == 3, f"got {len(eps)}: {[round(e['drop'],1) for e in eps]}")
    drops = sorted(round(e["drop"], 1) for e in eps)
    check("damage: magnitudes 3.0 / 7.0 / 15.0 recovered",
          drops == [3.0, 7.0, 15.0], f"got {drops}")
    tiers = {round(e["drop"], 1): e["tier"] for e in eps}
    check("damage: the 7.0 burst is tier=impact", tiers.get(7.0) == "impact",
          f"got {tiers.get(7.0)}")
    check("damage: the 15.0 bleed is tier=scrape", tiers.get(15.0) == "scrape",
          f"got {tiers.get(15.0)}")
    hits, _, eps2 = unmarked_damage(frames, [], [])
    check("damage: all 3 report as UNMARKED when there are no markers",
          len(hits) == 3, f"got {len(hits)}")
    check("damage: total drawdown is 26.0 (matches the real session)",
          abs(sum(e["drop"] for e in eps2) - 25.0) < 0.01,
          f"got {sum(e['drop'] for e in eps2):.1f} (25.0 = 26.0 minus the ignored 1.0)")

    # --- pedal-held detection --------------------------------------------
    held = [_mk_frame(i, i / 60.0, 1000.0, spd=0.5, thrP=1.0, ctrlT=0.0, ctrlTd=1.0)
            for i in range(300)]
    check("throttle: pedal_held() true when enabled=0 and disabled=1",
          all(fr.pedal_held() for fr in held))
    check("throttle: pedal_held() false when the enabled channel carries the pedal",
          not _mk_frame(0, 0.0, 1000.0, ctrlT=1.0, ctrlTd=1.0).pedal_held())
    sp = immobile_spans(held)
    check("immobility: a 5s full-throttle standstill is detected",
          len(sp) == 1 and sp[0]["dur"] > 3.0,
          f"got {len(sp)} span(s)")
    check("immobility: it is reported as 100% pedal-held",
          bool(sp) and sp[0]["heldFrac"] > 0.99)

    # --- iter-42 A1: commanded vs achieved --------------------------------
    # These guard the ONE claim iter-42 A1 exists to make: a command that was
    # accepted by the control channel but never reached the vehicle is visible.
    dead = _mk_frame(0, 0.0, 1000.0, ctrlT=0.0, ctrlTd=0.0)
    dead.ctrlB, dead.brakeAch = 0.90, 0.00
    dead.steer, dead.steerAch = 0.50, 0.01
    check("actuation: brake_not_actuated() true when ctrlB high and brakeAch ~0",
          dead.brake_not_actuated())
    check("actuation: steer_not_actuated() true when steer high and steerAch ~0",
          dead.steer_not_actuated())
    live = _mk_frame(1, 0.1, 1000.0)
    live.ctrlB, live.brakeAch = 0.90, 0.85
    live.steer, live.steerAch = 0.50, 0.47
    check("actuation: neither fires when the command did reach the vehicle",
          not live.brake_not_actuated() and not live.steer_not_actuated())
    # A command below the 0.3 threshold is not evidence of anything — a car can
    # legitimately show ~0 brake for a 0.1 command. Guard against the detector
    # widening into noise.
    small = _mk_frame(2, 0.2, 1000.0)
    small.ctrlB, small.brakeAch = 0.10, 0.00
    check("actuation: a below-threshold command is not reported as dead",
          not small.brake_not_actuated())
    # A frame from a pre-iter-42 log has no achieved fields at all; the
    # detectors must stay silent rather than treating None as zero.
    old = _mk_frame(3, 0.3, 1000.0)
    old.ctrlB, old.steer = 0.90, 0.50
    check("actuation: pre-iter-42 frames (no readback) never report dead",
          not old.brake_not_actuated() and not old.steer_not_actuated())

    # --- epochs -----------------------------------------------------------
    efr = [_mk_frame(i, i / 60.0, 1000.0) for i in range(60 * 200)]
    evs = [
        {"f": 1, "t": 0.5, "kind": "vehicle-session",
         "rest": "kind=first model=TEZERACT", "line": "", "i": 0},
        {"f": 2, "t": 0.0, "kind": "iter41-gates",
         "rest": "creepClearV2=0 accStandstillV2=0", "line": "", "i": 1},
        {"f": 3, "t": 100.0, "kind": "gate-toggle",
         "rest": "name=creepClearV2 from=0 to=1", "line": "", "i": 2},
        {"f": 4, "t": 100.5, "kind": "vehicle-swap",
         "rest": "model=SULTAN", "line": "", "i": 3},
    ]
    eps3 = build_epochs(efr, evs, [], [], [])
    check("epochs: a toggle + a vehicle swap produce 3 epochs", len(eps3) == 3,
          f"got {len(eps3)}")
    check("epochs: the SWAPPED-IN model is read from newModel= (iter-43 D3)",
          session_model("kind=change oldModel=TEZERACT newModel=FLATBED2") == "FLATBED2",
          f"got {session_model('kind=change oldModel=TEZERACT newModel=FLATBED2')}")
    check("epochs: the opening kind=first line still reads lowercase model=",
          session_model("kind=first handle=1 model=TEZERACT") == "TEZERACT")
    # iter-43 D2: the mod re-emits the gates event after every toggle. Only the
    # FIRST may define the starting state, or the final all-on snapshot is applied
    # retroactively to epoch 1 and every epoch looks identical.
    reemit = [
        {"f": 0, "t": 0.0, "kind": "iter41-gates",
         "rest": "creepClearV2=0 skewGateV2=0", "line": "", "i": 0},
        {"f": 9, "t": 50.0, "kind": "iter41-gates",
         "rest": "creepClearV2=1 skewGateV2=0", "line": "", "i": 1},
        {"f": 9, "t": 99.0, "kind": "iter41-gates",
         "rest": "creepClearV2=1 skewGateV2=1", "line": "", "i": 2},
    ]
    st = gate_state_at_start(reemit)
    check("epochs: gate_state_at_start takes the FIRST emission, not the last",
          st.get("creepClearV2") == "0" and st.get("skewGateV2") == "0",
          f"got {st}")

    # --- iter-43 D1: a vehicle-death marker must be renderable ------------
    dmk = Marker()
    dmk.typ, dmk.kind, dmk.num = "A", "death", 900
    dmk.raw = "[F1] EVENT vehicle-death: bodyDrop=803.9"
    check("death marker: .raw is set so write_excerpt cannot crash",
          dmk.raw is not None and dmk.raw.strip() != "")

    # --- iter-43 C2: brake actuation is only meaningful while moving ------
    slow = _mk_frame(0, 0.0, 1000.0, spd=0.4)
    slow.ctrlB, slow.brakeAch = 1.0, 0.0
    check("actuation: a stopped car's zero BrakePower is NOT reported as dead",
          not slow.brake_not_actuated())
    fast = _mk_frame(1, 0.1, 1000.0, spd=12.0)
    fast.ctrlB, fast.brakeAch = 1.0, 0.0
    check("actuation: the SAME reading while moving IS reported as dead",
          fast.brake_not_actuated())
    # --- iter-49 D: gunfire attribution on the marker header --------------
    # The whole point is that a marker taken in a firefight must be visibly
    # separated from a driving failure. A silent regression here would put
    # gunfire back into the drive-assist cost ranking, which is the exact
    # mistake that produced the 07-31 headline.
    gmk = Marker()
    gmk.bullet, gmk.proj = "True", "False"
    check("gunfire: bullet=True tags the marker GUN", gmk.fire_tag() == "GUN")
    pmk = Marker()
    pmk.bullet, pmk.proj = "False", "True"
    check("gunfire: proj=True alone tags the marker PRJ", pmk.fire_tag() == "PRJ")
    cmk = Marker()
    cmk.bullet, cmk.proj = "False", "False"
    check("gunfire: a clean marker is tagged blank, not GUN",
          cmk.fire_tag() == "")
    omk = Marker()
    check("gunfire: a pre-iter-49 marker (no fields) reads '-' not GUN",
          omk.fire_tag() == "-")

    check("epochs: E01 is the all-off BASELINE",
          bool(eps3) and eps3[0]["on"] == [] and eps3[0]["baseline"],
          f"on={eps3[0]['on'] if eps3 else '?'}")
    check("epochs: creepClearV2 is recorded ON after the toggle",
          len(eps3) > 1 and "creepClearV2" in eps3[1]["on"],
          f"on={eps3[1]['on'] if len(eps3) > 1 else '?'}")
    check("epochs: the model change is carried into the last epoch",
          len(eps3) > 2 and eps3[2]["model"] == "SULTAN",
          f"model={eps3[2]['model'] if len(eps3) > 2 else '?'}")

    print()
    if fails:
        print(f"SELFTEST FAILED: {len(fails)} check(s): {', '.join(fails)}")
        return 1
    print("SELFTEST PASSED")
    return 0


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
    # iter-43 D6: bounds for very large sessions (07-31 was 266.7 MB / 64
    # incidents / 84k events). 0 = unlimited for both.
    ap.add_argument("--max-incidents", type=int, default=25,
                    help="write excerpts for at most N incidents, worst-cost "
                         "first (0 = all); the digest still lists every one")
    ap.add_argument("--max-per-kind", type=int, default=2000,
                    help="cap events.log at N lines per EVENT kind "
                         "(0 = unlimited); the drop is reported in the file")
    ap.add_argument("--selftest", action="store_true",
                    help="run the built-in detector checks and exit")
    args = ap.parse_args()

    if args.selftest:
        sys.exit(selftest())

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
