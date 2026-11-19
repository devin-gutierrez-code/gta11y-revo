# iter69 intoxication lifecycle repair

## Scope

This iteration rolls the full intoxication review into one safety and correctness repair.
It adds no new setting and changes no default. The two existing driving effects still ship
off; the visual and audio presentation retain their existing defaults.

## Decisions

- The raw-global `intoxVanillaOnly` bridge is disabled fail-closed. Updating an offset is
  insufficient because allocation and self-readback cannot identify a script structure,
  and the former bridge did not reproduce Rockstar's per-ped registration protocol.
- Every consume and long-lived ped mutation has an explicit owner. Teardown acts on that
  owner and never on the protagonist who happens to be current later.
- The dose commits when the enter leg finishes. The outro is presentation, not consumption.
- A safehouse scene starts only after its dictionary, props, and ambient bank are ready,
  or after the shared three-second degraded-start deadline.
- Engine scene phase owns normal completion. The hard deadline is the only timer allowed
  to break a synchronized enter scene.
- `intoxDriveImpair` and `intoxBlackoutDrive` are independent control owners. Speech about
  drive-assist availability comes from the authority-latch transition, not the stage label.

## Required field checks

- Start a first drink while sober, then turn intoxication off during streaming, enter, and
  outro. Each path must release movement, delete props, stop owned audio, and restore the
  original weapon.
- Repeat with a zero-dose food item and with a mid-consume protagonist switch.
- Cancel before enter completion: no dose. Cancel during an outro: full dose remains.
- On a cold-cache safehouse consume, `intox-bank: ... ok=1` must precede `intox-anim:`. A
  timeout must emit `intox-bank-start-timeout` before the degraded animation begins.
- Exercise driving settings as 00, 10, 01, and 11. The blackout-only combination must
  reach and fire `intoxBlackoutDrive`; no stage line may claim drive assist is off.
- Turn either driving switch off while it owns the controls. The player must hear that the
  wheel is back, and steering/throttle transient state must return to zero.
- Reload the script during a consume and while visually intoxicated. Only presentation
  owned by this subsystem may be cleared.

## Verification loop

Run `python tools\verify-intoxication.py`, then build and deploy. The next in-game debug
log must contain `EVENT iter-version: iter69-intox-lifecycle` before the session counts.
