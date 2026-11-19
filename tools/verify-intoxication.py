#!/usr/bin/env python3
"""Deterministic source and model checks for the intoxication subsystem."""

from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "GTA" / "GTA11Y.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def stage_for(impairment: float, current: int, thresholds: list[float]) -> int:
    level = current
    while level < 3 and impairment >= thresholds[level + 1]:
        level += 1
    while level > 0 and impairment < thresholds[level] - 0.5:
        level -= 1
    return level


def main() -> None:
    text = SOURCE.read_text(encoding="utf-8")
    region = text.split("#region INTOXICATION", 1)[1].split("#endregion", 1)[0]

    banners = re.findall(r"EVENT iter-version:\s*([^\"\s]+)", text)
    require(banners == ["iter69-intox-lifecycle", "iter69-intox-lifecycle"],
            f"unexpected iteration banners: {banners}")

    require("GlobalVariable.Get" not in region,
            "intoxication region must not access build-specific script globals")
    require("VG_DRUNK_" not in region,
            "obsolete vanilla-global constants remain")
    require("unsafe build-specific globals" in region,
            "fail-closed vanilla bridge event is missing")

    owned = region.split("private bool HasOwnedIntoxState()", 1)[1].split("}", 1)[0]
    for token in ("intoxConsuming >= 0", "intoxAnimPending", "intoxSyncScene >= 0",
                  "intoxWeaponStashed", "intoxAssistSuspend"):
        require(token in owned, f"owned-state teardown omits {token}")

    animate = region.split("private void TickConsumeAnim(Ped player)", 1)[1]
    animate = animate.split("private void StartConsumeScenario", 1)[0]
    require("bool bankReady = intoxBankName == null || intoxBankLoaded;" in animate,
            "audio bank is not part of the animation start gate")
    require(animate.index("if ((!propsReady || !bankReady) && !timedOut) return;")
            < animate.index("StartConsumeAudioScene(kind);"),
            "audio scene can start before the bank readiness gate")

    tick = region.split("private void TickIntoxication()", 1)[1]
    tick = tick.split("private void EndIntoxBlackoutDriving", 1)[0]
    require(tick.index("now >= intoxConsumeHardTicks")
            < tick.index("now >= intoxConsumeEndTicks"),
            "normal timer still precedes the synchronized-scene hard watchdog")
    require(tick.index("CommitConsumeDose(intoxConsuming, why);")
            < tick.index("TryStartConsumeOutro(player, intoxConsuming, why)"),
            "dose must commit before the outro starts")

    driving = region.split("private void TickIntoxDriving()", 1)[1]
    require("bool impairEligible = impairOn && intoxStage >= 2;" in driving,
            "ordinary driving gate is not independent")
    require("bool blackoutEligible = blackoutOn && intoxBlackout;" in driving,
            "blackout driving gate is not independent")
    require("if (!impairEligible && !blackoutEligible)" in driving,
            "driving eligibility still has a hidden parent gate")

    require('Speech.Info("Drunk. Your driving is impaired.")' not in region,
            "stage speech still claims a disabled driving effect")
    require('Speech.Critical("Very drunk. Drive assist is off.")' not in region,
            "stage speech still claims drive assist is suspended")

    match = re.search(r"INTOX_STAGE_AT\s*=\s*\{([^}]+)\}", region)
    require(match is not None, "stage threshold table not found")
    thresholds = [float(value.rstrip("f"))
                  for value in re.findall(r"[0-9.]+f", match.group(1))]
    require(thresholds == [0.0, 1.0, 3.0, 6.0],
            f"unexpected stage thresholds: {thresholds}")
    cases = (
        (0.0, 0, 0), (1.0, 0, 1), (3.0, 1, 2), (6.0, 2, 3),
        (5.49, 3, 2), (2.49, 2, 1), (0.49, 1, 0),
        (0.75, 1, 1), (2.75, 2, 2), (5.75, 3, 3),
    )
    for impairment, current, expected in cases:
        actual = stage_for(impairment, current, thresholds)
        require(actual == expected,
                f"stage_for({impairment}, {current}) = {actual}, expected {expected}")

    print("intoxication verification passed: 10 model cases and source invariants")


if __name__ == "__main__":
    main()
