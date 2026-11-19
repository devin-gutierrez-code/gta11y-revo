# iter-63 — make the experiment visible (retro note)

Banner: `iter63-baseline-visibility-governor-bisect`. Played twice:
`driveassist-2026-08-19-051310` (11.8 min, 8.87 mk/min) and
`driveassist-2026-08-19-074058` (6.2 min, 2.27 mk/min pooled).

**This file was written retrospectively on 2026-08-31**, during the iter-71 reconciliation of
`DRIVE-ASSIST-REVIEW-2026-08-29.md`. iter-63 shipped without a plan file. It is reconstructed
from the code and tooling comments that cite it — `GTA11Y.cs:1425`, `:1561`, and
`tools/triage-log.py:159`, `:1628`, `:1947`, `:3578`, `:4004`, `:4022`, `:4690` — not from a
contemporaneous document. Where it states intent, that intent is inferred from what the change
guards against.

## What happened

`driveassist-2026-08-17-111533` ran 20.6 driving minutes and flipped `driveAssistBaseline`
**nine times**. That switch stands eleven experimental drive-assist gates down as one unit — it
exists precisely so a session can carry both arms of an A/B and be compared against itself.

The session did exactly that, and **not one artifact could say so.** All seventeen of its epochs
rendered an identical gate list, because none of the eleven were in `TRACKED_GATE_SETTINGS`. The
digest printed *"No all-gates-off epoch in this session"* at a player who had just driven ten
balanced minutes of one. The experiment was on disk the whole time and the report was blind to it.

That is the iteration's theme: **a measurement apparatus that cannot see the experiment it was
built for is worse than no apparatus**, because it produces a confident wrong answer instead of
an admission of ignorance. Every change below is a variant of that failure.

## What shipped

**1. `EVENT gate-drift` names the gates.** `WriteGateDrift` (`GTA11Y.cs:1420-1470`) previously
reported `onAgainstDefault=26` and stopped. A count cannot tell you that `roadSpeedGovernorV2` —
default-OFF in code — was ON from a persisted settings file. **The settings file, not the code
default, is what runs**, and 08-17 ran a governor nobody had chosen for it that session. The
line now lists them.

**2. The dependency warning re-arms on every configuration change.** The iter-51 warning ("these
gates are inert without `roadFactsV2`'s sampler") was a once-per-session latch. `roadFactsV2` is
in the baseline set, so across nine flips, five separate epochs ran with `roadSpeedGovernorV2`
and `polyDistrustV2` silently dead — and the log said so exactly once, at F884. A warning that
fires for the first of nine identical configurations is not a warning. `iter51DepWarned` is now
cleared in the gate-toggle handler (`:1561`).

**3. `BASELINE_EXPERIMENTAL` in the triage tool** (`triage-log.py:165`), kept in sync with
`GTA11Y.cs`'s own list. An epoch is `BASE` when all eleven read 0, `EXP` when all eleven read 1,
and `mixed` otherwise — and `mixed` is not a normal state, it means the eleven were flipped
apart and the epoch is attributable to neither arm.

**4. `Baseline vs experimental`, segmented by vehicle** (`:3473`). The vehicle confound is
large enough to swamp the gate effect, so the only honest comparison is EXP against BASE for the
*same* car. The rollup is reported but is explicitly an average of per-vehicle experiments.

**5. The result moved above the fold** (`:4022`). If a session ran both arms, that *is* the
result; it does not belong 400 lines down. 08-17's lead had said only "below baseline".

**6. A mid-session manifest change that is EXACTLY the baseline set is no longer a fault**
(`:4004`). The digest had been telling the player to "re-run a session with a manifest that does
not change" after nine deliberate flips — advice against the feature the mod ships for this
purpose. It is now reported as the experiment, with the honest caveat that the headline rate is
still a blend of both arms.

**7. The damage-coverage verdict had its sign backwards** (`:3578`). A repair restores only the
damage standing at that moment, so `repaired` is a floor on damage that existed, never a total.
Damage the player never repaired makes `marked >> repaired`, which is normal. 08-17 read
"-990.8 UNACCOUNTED DAMAGE — the detectors missed something" directly above a damage-attribution
block correctly reporting 0.0 attributed to nothing.

**8. The periodic capture census joined the census chain** (`:1947`). `capture-census` is the
only census that survives a hard exit. 08-17 quit without the abort path, so it has no
`capture-final`; it does have 44 `capture-census` lines whose last reads `written=335` against
340 files on disk. The digest printed the recovery census — `written=0` — and told the reader to
go looking for a `capture-final` that a hard exit can never write.

**9. Selftest regression guards** (`:4690`): every `BASELINE_EXPERIMENTAL` gate is tracked, and
the `BASE`/`EXP`/`mixed` classifier is exercised directly.

## Verdict, read on 2026-08-31

The visibility work succeeded — it is what made the two 08-19 sessions readable at all, and it
is what surfaced the finding below. The bisect half did not resolve anything.

**The baseline A/B contradicts itself across the two sessions that have run it:**

| session | BASE | EXP | ratio |
|---|---|---|---|
| 08-17-111533 (20.6 min) | 4.02/min | 7.62/min | 1.90x |
| 08-19-074058 (6.2 min) | 9.31/min | 8.35/min | 0.90x |

This is the loudest unread signal in the archive. iter-71 deliberately leaves the eleven alone
so they do not confound its own three gates, and adds the bridge harness that makes the question
cheaply testable — same scenario, same placement, gates flipped from outside the game.

Two later reads sharpen it further, and both post-date this iteration:

- iter-71's C2 shows 08-17's 1.90x is an average of `_all car_` at 2.63x and `_all two-wheel_`
  at 1.03x. Those are two different experiments and the mod has no motorcycle-specific code path.
- iter-71's C1 shows 71% of 08-19-074058's markers were auto-drive, which is GTA's task driving,
  not the lane/ACC loop. Its real drive-assist rate is 0.65/min, not 2.27. Whether the 0.90x
  survives that split has not been re-checked and should not be assumed.
