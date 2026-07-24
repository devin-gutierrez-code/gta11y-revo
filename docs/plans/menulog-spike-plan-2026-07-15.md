# Menulog spike — pause-menu & phone introspection probe

## Context

Users keep requesting screen-reader access to GTA V's pause menu and iFruit phone. Research (2026-07-15) established both are Scaleform GFx (not CEF) and should be introspectable: pause-menu selection via undocumented natives, phone state via script-thread checks + the `GET_CURRENT_SELECTION` scaleform method. Before building the real reader, a one-session in-game spike must validate these natives return sane values on Devin's install (Legacy, SHVDN 3.6).

The spike is a **menulog**: a new debug log mirroring the drive-assist debug-log conventions (`menulog-<timestamp>.log` in ModSettings), plus live spoken raw values so Devin hears immediately whether the probes track his key presses. **Spike only** — no label mapping, no pausemenu.xml tables, no script-global reads; the real reader gets its own plan after reviewing an actual menulog. User confirmed: live speech + spike-only scope.

Key exploration facts (verified in code):
- **onTick fires while paused** — per-feature `Game.IsPaused` guards exist (GTA11Y.cs:16834, 17309); suppression is opt-in. The probe simply omits that guard.
- Logger template: `GTA\DriveAssistLogger.cs` (118 lines, double-buffered, 500 ms background flush). csproj auto-globs new .cs files; no csproj edit.
- `menulog-*` is invisible to `tools\triage-log.py` (globs `driveassist-*.log` only) — no tooling collision.
- SHVDN 3.6 Hash enum has IS_PAUSE_MENU_ACTIVE, GET_PAUSE_MENU_STATE, GET_HASH_KEY, GET_NUMBER_OF_THREADS_RUNNING_THE_SCRIPT_WITH_THIS_HASH (= `_GET_NUMBER_OF_INSTANCES_OF_STREAMED_SCRIPT`, returns numRefs−1), and the full scaleform set incl. IS_SCALEFORM_MOVIE_METHOD_RETURN_VALUE_READY. `Game.GenerateHash(string)` = managed joaat.
- `Speech.LogSink` is a single static Action owned by the drive logger — menulog must NOT claim it; it writes its own `EVENT speak:` lines.
- Drive-assist iter banner (iter37h, uncommitted, verification pending) must NOT be touched; menulog carries its own sentinel `EVENT menulog-version: spike1`.

## Files

- **Create `GTA\MenuLogger.cs`** — near-copy of `DriveAssistLogger.cs`: class `MenuLogger`, filename prefix `menulog-`, header `# GTA11Y Menu/Phone Probe Debug Log`, thread name `MenuLogWriter`, same API (`Start/Stop/Write/IsRunning/FilePath`). Comment: deliberate spike-scoped copy; don't refactor the drive logger while iter-37 verification is pending.
- **Modify `GTA\GTA11Y.cs`** — five touch points:
  1. Field block next to `driveLogger` fields (~line 83): `menuLogger`, `menuLogWasEnabled`, `menuLogFrameCount`, pause last-seen values (`mlPauseActive/mlPauseState/mlLastMenuID/mlSelUniqueID/mlSelData0-2`), phone state (`mlPhoneOpen/mlPhoneMovie/mlPhoneMovieHandle/mlPhoneRow/mlFlashhandHash/mlAppNames/mlAppHashes/mlAppMask`), scaleform state machine (`SfProbeState` enum: Off/AwaitMovieLoad/ReadyToIssue/AwaitReturn/Disabled; `mlSfReturnHandle/mlSfIssuedAt/mlSfNextIssueAt/mlSfConsecTimeouts`), speech coalescer (`mlPendingSpeech/mlLastSpokeAt`), per-probe error fuses (`mlPauseErrors/mlPhoneErrors`, disable at 20).
  2. Ctor (line 3318, next to `driveLogger = new DriveAssistLogger();`): `menuLogger = new MenuLogger();`
  3. Dispatch block: insert `try { TickMenuProbe(); } catch (Exception ex) { ReportSubsystemError("menu probe", ex); }` after `NavGuidanceTick` (line 7492), BEFORE `Speech.Drain()` (must stay last). No `Game.IsPaused` guard.
  4. Settings: add `"menuDebugLog"` to `ids[]` right after `"driveAssistDebugLog"` (line 9555); `idToName` case ~16570: `"Menu Debug Logging (writes log file). "`. Default 0 automatic; auto-appears in spoken Settings menu with Off/On values.
  5. New `#region MENU/PHONE PROBE (menulog spike)` with `TickMenuProbe()`, `TickPauseProbe()`, `TickPhoneProbe()`, `PhoneMovieForCurrentPed()`, `MLog(string)`, `MSpeak(string)` + coalescer drain.

## Probe design

**TickMenuProbe** — edge-triggers Start/Stop from `getSetting("menuDebugLog")` (drive-log pattern, GTA11Y.cs:5595-5596; but edge logic lives here, not in the drive-log block). On start: banner + `EVENT menulog-version: spike1` + `Speech.Info("Menu debug logging started")`. On stop: release/leak scaleform ref (below), `menuLogger.Stop()`, Info announcement. Then `menuLogFrameCount++`, heartbeat FRAME line (1 s while pause menu or phone active, 5 s otherwise), dispatch both sub-probes each in its own try/catch → `EVENT probe-error` + 20-error fuse → `EVENT probe-disabled`.

**TickPauseProbe** — every tick:
- `Hash.IS_PAUSE_MENU_ACTIVE` edges → `EVENT pause-open: state=N` / `EVENT pause-close:` (+ speech "pause state N" / "pause closed")
- `Hash.GET_PAUSE_MENU_STATE` change → `EVENT pause-state: state=N prev=N`
- While active, OutputArgument reads of `(Hash)0x36C1451A88A09630` (_GET_PAUSE_MENU_SELECTION → lastMenuID, selectedUniqueID) and `(Hash)0x7E17BE53E1AAABAF` (_GET_PAUSE_MENU_SELECTION_DATA → 3 ints); any change → `EVENT pause-sel: menu= item= d0= d1= d2=` + speech "menu 42, item 7" (data triple log-only)
- One-frame signals `(Hash)0x2E22FEFA0100275E` (highlight moved) → `EVENT pause-nav:`; `(Hash)0xF284AC67940C6812` (activated) → `EVENT pause-activate:`
- Native style: enum members where they exist, raw hash + inline name comment for the four undocumented ones (matches codebase convention, e.g. GTA11Y.cs:13066).

**TickPhoneProbe**:
- Init once: `mlFlashhandHash = Game.GenerateHash("cellphone_flashhand")`; app table joaat'd likewise: `appcontacts, appcamera, appsettings, apptext, appsms, apptextmessage, appemail, appinternet, appmedia, apporganiser, apptrackify, appsidetask, appchecklist, appzit` (wrong names harmlessly return ≤0; log raw counts, treat >0 as running per numRefs−1 semantics).
- Phone open/close edges via `GET_NUMBER_OF_THREADS_RUNNING_THE_SCRIPT_WITH_THIS_HASH(mlFlashhandHash)` → `EVENT phone-open: movie=<name> ped=<Michael|Franklin|Trevor|other:0xHASH>` / `EVENT phone-close:` (+ speech). Movie per protagonist via `(PedHash)Game.Player.Character.Model.Hash` (pattern GTA11Y.cs:7388-7407): cellphone_ifruit / cellphone_badger / cellphone_facade; unknown ped → ifruit + logged hash.
- App poll at 250 ms while phone open; set change → `EVENT phone-app: on=<name:count,...> off=<...>` + speech "app appcontacts" on gains.
- **Scaleform state machine** (all timeouts wall-clock `DateTime.Now` per ModClock rule at ModClock.cs:16-19):
  - Off → phone-open edge: `REQUEST_SCALEFORM_MOVIE(movie)` → AwaitMovieLoad
  - AwaitMovieLoad → `HAS_SCALEFORM_MOVIE_LOADED`: `EVENT phone-movie: name= handle= loadMs=` → ReadyToIssue; 2 s timeout → Disabled (`EVENT sf-disabled: why=movie-load-timeout`)
  - ReadyToIssue (when `now >= mlSfNextIssueAt`): `BEGIN_SCALEFORM_MOVIE_METHOD(handle, "GET_CURRENT_SELECTION")` → `END_SCALEFORM_MOVIE_METHOD_RETURN_VALUE` → store return handle → AwaitReturn
  - AwaitReturn → `IS_SCALEFORM_MOVIE_METHOD_RETURN_VALUE_READY`: read `GET_SCALEFORM_MOVIE_METHOD_RETURN_VALUE_INT`; on change `EVENT phone-row: row= prev= latencyMs=` + speech "phone row 3"; next issue in 200 ms → ReadyToIssue. 1 s timeout → abandon handle, `EVENT sf-timeout:`, 500 ms backoff; 5 consecutive → Disabled (`EVENT sf-disabled: why=return-timeouts`)
  - Any → phone-close edge: → Off, abandon outstanding return handle, KEEP movie handle
  - Invariant: single `mlSfReturnHandle`, issued only from ReadyToIssue, zeroed on every AwaitReturn exit — a stuck handle can't wedge the machine.
- **Movie ref release (conservative)**: never release while phone open or on close edges. At probe stop only, and only if flashhand count is 0: `SET_SCALEFORM_MOVIE_AS_NO_LONGER_NEEDED(new OutputArgument(handle))` → `EVENT sf-released:`; else leak-and-log `EVENT sf-leaked: handle= why=phone-open-at-stop`.

**Speech UX** — all value announcements `Speech.Ambient`; start/stop `Speech.Info`. Local trailing-edge coalescer: min 250 ms between menulog speech emissions; a change inside the gap overwrites `mlPendingSpeech` (latest wins), emitted when the gap expires — fast arrowing speaks first + final selection while the log records every change. Every spoken line also logged as `EVENT speak: text="..."`. Never speak on heartbeat.

## Implementation order (testing branch, NO commits)

1. `GTA\MenuLogger.cs` (copy/rename). `dotnet build GTA\GrandTheftAccessibilityRevo.csproj` — confirms glob.
2. GTA11Y.cs fields + ctor init.
3. Settings wiring (`ids[]` + `idToName`). Build — setting alone is testable (toggle creates/closes bannered log).
4. `TickMenuProbe()` skeleton: edge trigger, frame counter, heartbeat, dispatch hook at 7492. Build.
5. `TickPauseProbe()` complete.
6. `TickPhoneProbe()` part 1: joaat init, open/close edges, movie name, app poll.
7. `TickPhoneProbe()` part 2: scaleform state machine + fuses + release-on-stop.
8. Final `dotnet build` (auto-deploys via csproj DeployToGameScripts target). Hand off to Devin's in-game session.

## Verification

Build passes (`dotnet build`, 0 warnings→errors introduced). Then Devin's session:
1. Settings menu → "Menu Debug Logging" → On → expect "Menu debug logging started".
2. Pause probe: Esc → expect "pause state N"; arrow within a tab (~10 rows); cycle ALL tabs; enter Settings→Audio submenu; activate an item; unpause.
3. Phone probe: phone up → "phone open, ifruit"; arrow home screen slowly, then fast (flood test → coalescer check); open Contacts, arrow rows; open Settings app; phone down. If save allows, switch protagonist and repeat (validates badger/facade).
4. Toggle Off → "stopped"; read newest `Documents\Rockstar Games\GTA V\ModSettings\menulog-*.log`.

**Success**: `pause-sel` item= changes on every arrow, menu= changes on tab switch (not frozen 0/−1); `pause-nav` coincides with arrows; ≥1 `pause-activate`; `phone-movie` loads fast; `phone-row` tracks arrows 1:1, latencyMs ≲ 100; `phone-app on=appcontacts:...` when Contacts opens; heartbeats throughout; zero `probe-error`/`sf-disabled`.

**Failure signatures map to distinct next steps** (the point of the spike): frozen `pause-sel` → selection natives don't reflect SP frontend (fall back to script-global route); `sf-timeout`×5 → wrong method name or wrong movie; constant `phone-row` → method means something else; empty `phone-app` → need true streamed-script native variant.

## Risks

- Raw pause-selection hashes may be MP-frontend-oriented → that's the hypothesis under test; heartbeats disambiguate "always zero" from "probe dead".
- One-frame signal natives (0x2E22FEFA..., 0xF284AC...) might consume events the game's own frontend scripts read → watch for pause-menu misbehavior while probing; if observed, drop those two polls (they're nice-to-have).
- `GET_CURRENT_SELECTION` may not exist on SP phone movies → timeout fuse contains it; game unaffected.
- Return-handle pool exhaustion → single-outstanding invariant + backoff + 5-strike fuse.
- Releasing the movie ref could disturb the live phone → conservative release-only-at-stop-with-phone-closed; else bounded leak, logged.
- Speech flood → 250 ms trailing-edge coalescer.
- Flaky native throwing per-tick → per-probe 20-error fuse + outer ReportSubsystemError backstop.

## Out of scope (follow-up plan after log review)

Label mapping (pausemenu.xml → GXT), real spoken menu reader, phone dynamic text (script globals), Enhanced-edition support, triage tooling for menulogs.

---

## Spike 2 addendum (2026-07-17)

Spike-1 session verdict (menulog-2026-07-17-155613.log): PHONE fully validated (facade movie 49ms load, GET_CURRENT_SELECTION tracks rows 1:1 at ~33ms, app scripts appchecklist/appsettings/appemail confirmed live, zero errors). PAUSE natives never ran: the SP pause menu suspends all SHV scripts (52s tick hole, F1740-F1790, zero heartbeats) - the "onTick fires while paused" assumption was wrong.

Spike-2 change (gated on menuDebugLog=1, normal play untouched when off):
- Esc now opens the REAL pause menu WITHOUT pausing: TickPauseProbe disables control 200 (INPUT_FRONTEND_PAUSE_ALTERNATE, Esc) each frame while no menu is up; onKeyDown Escape (keyState[21]) calls ACTIVATE_FRONTEND_MENU(joaat("FE_MENU_VERSION_SP_PAUSE"), 0, -1) and logs EVENT frontend-open.
- P (control 199, INPUT_FRONTEND_PAUSE) untouched: still opens the normal PAUSED menu.
- Sentinel bumped to menulog-version: spike2.
- Known spike-mode side effects: game world keeps running while the unpaused menu is open; nav-audio Game.IsPaused guards will not mute (Game.IsPaused stays false). Acceptable for the spike; real feature decides mitigation (invincibility/timescale).

Spike-2 verification: with menuDebugLog on, press Esc -> menu opens, game audio keeps running, probe should speak "pause state N" then "menu N, item N" per arrow press. Arrow rows + tabs, activate an item, Esc out. Press P -> normal paused menu (probe goes silent while paused - expected). Success = pause-sel lines tracking arrows in the unpaused menu.

---

## Spike 3 addendum (2026-07-17)

Spike-2 session verdict (menulog-2026-07-17-171637.log): unpaused-frontend hack WORKS - selection natives fire live (pause-nav/sel/activate all tracked). Semantics discovered: GET_PAUSE_MENU_SELECTION_DATA = (previousMenuState-1000, selectedItemMenuId [CURRENT], selectedItemUniqueId); the 0x36C1 pair is the LAST item and lags one move (each event''s d1 = next event''s menu=). Gaps: several tab-bar moves emit nothing; settings left/right value changes emit nothing (uniqueID static for list items). Some menuIds are joaat-like hashes (-925456543, -1265285960).

Spike-3 changes (sentinel spike3):
- Speech now reads the CURRENT highlight (d1/d2) instead of the laggy pair.
- EVENT input: ctrl=<up|down|left|right|accept|cancel> logged for frontend group-2 controls (187/188/189/190/201/202) while menu active - makes silent tab arrows visible; basis for future shadow tab-tracker (SP pause always opens on Map).
- Profile-setting diff scanner: baseline snapshot of ids 0-800 (Hash.GET_PROFILE_SETTING) on pause-open; rescan+diff on left/right/accept press (immediate) + 1s backstop; EVENT profile-change: id= old= new= and speaks "setting N, now V". Known audio ids: 300 SFX vol, 306 music vol, 305 output, 308 dialogue boost; display: 203 subtitles, 213 brightness. PC-only pages (Graphics/Keybinds) are NOT profile settings - expected silent.

Spike-3 verification: tab arrows -> input events 1:1 even where sel is silent; Settings->Audio left/right -> "setting 306, now N" etc.; Graphics page silent (confirm only); current-item speech no longer lags.

---

## Menu Reader v1 (2026-07-18) - IMPLEMENTED

Spike-3 verdict: phone fully working; pause selection natives fire live in unpaused menu BUT tab bar ~70% silent (3 leak anchors), settings values invisible (profile scanner zero diffs), JUST_PRESSED missed keys, 4x duplicate frontend-open.

R1 research NAILED the ID pipeline: eMenuScreen enum (lua-gtav-enums commands_hud.lua) maps ALL observed runtime ids - 51=SETTINGS_LIST, 22=SETTINGS_AUDIO, leaks 15=HOME_MISSION(Brief)/24=SETTINGS_CONTROLS(Settings)/75=REPLAY_MISSION(Game); hashed ids = MenuUniqueIdHash screens via joaat (3/3 matched: RESTORE_DEFAULTS_AUDIO, UR_COMPLETESCAN, UR_QUICKSCAN). Tab wrap arithmetic confirms 10 tabs.

Landed (testing branch, uncommitted, sentinel reader1):
- tools/build-menulabels.py -> GTA/scripts/gta11y-menulabels.json (175 menus incl 14 hashed, 156 ordered SP settings rows w/ gxt+pref, 10 tabs w/ leak anchors, phone tables). Sources cached in tools/_cache/.
- GTA/MenuLabelDb.cs (MapDb pattern; lazy GXT via Game.GetLocalizedString; TryRowLabel/TryMenuLabel/TabName/TryTabForLeakId/PhoneHomeLabel/PhoneAppLabel).
- pauseMenuReader setting DEFAULT ON; Esc hijack now gated on (menuDebugLog OR pauseMenuReader) + 500ms cooldown.
- Shared plumbing: CaptureFrontendState single-caller for consume-on-read one-frame natives; FrontendSnap struct feeds probe (logs) + reader (speech); IS_CONTROL_PRESSED edge detector w/ auto-repeat (400/160ms) replaces JUST_PRESSED; phone core (renamed TickPhoneCore) runs for either consumer; probe MSpeak muted while reader on.
- Reader: "Pause menu. Map tab." on open; shadow tab tracker (seed Map, modulo 10, leak resync + reader-tab-resync event); ReaderRowText fallback chain (calibrated row -> screen label + item N -> raw) with reader-unmapped harvest lines; activation earcon (interact.wav); "Menu closed."; menuReaderMenuOpen suppresses NavAssistAudioTick/NavGuidanceTick; phone: brand names, home-grid labels (provisional), app names, row N in apps.
- Value probe: 1Hz value-probe line ids 290-330; snapshot to 0-1023; profile-commit-diff on menu close (commit-on-close hypothesis).
- NO world freeze (user decision).

Next: verification session, then calibration sessions A/B (walk tabs x2, every settings category/row, phone tiles+apps; harvest reader-unmapped; sequence-align against XML rows; edit JSON only, redeploy without rebuild).

---

## Menu Reader v1.1 (2026-07-18) - IMPLEMENTED

v1 verification diagnosis: (1) settings rows all spoke "Sound Effects Volume, item N" - link-label heuristic gave screen 51 the MO_SFX row label; (2) harvest never fired (screen fallback always succeeded); (3) reader speech unlogged; (4) VALIDATED: settings-row uniqueId == unfiltered XML item index (uid9=MO_OUTP, 17=MO_RAD, 43/46=GFX all matched) -> all 156 rows labeled with no calibration; (5) value probe: audio profile ids hold real values (300=10, 306=9), changes COMMIT ON CLOSE (id 220 diff observed); ids >800 exist (903 in decompiled scripts); (6) phone home grid is DYNAMIC per decompiled cellphone_flashhand sub_5c67 SP branch: slots 0-7 = Email(CELL_5)/Texts/Checklist/Settings/Contacts/QuickSave(CELL_16)/Camera/Internet, slot 8 = Trackify (mission flag), slot 0 has mission override (CELL_25 contacts-variant - explains observed row0->appcontacts).

v1.1 changes (sentinel reader1.1):
- build-menulabels.py: rows uid=xmlOrder (all 156, unfiltered); PM_-preferring link-label heuristic; SETTINGS_LIST label+gxt null; phoneHome schema v2 per-slot objects w/ CELL_* gxt + alt override info; JSON version 2.
- MenuLabelDb: PhoneSlot DTO, PhoneHomeLabel resolves CELL_* GXT (game-localized app names), PhoneHomeScript for correlation; TryProfileName table (audio/display ids).
- GTA11Y.cs: harvest fires on EVERY row-table miss; MrDrainSpeech logs reader-speak; TickProfileShared (baseline/live-diff/commit-on-close moved out of log-gated probe so values work reader-only); live value announcements via reader w/ names; close summary "Changed: X to N" (max 3 + count); phone-slot-corrected diagnostic when opened app disagrees with slot table.

Verify: settings rows speak real names; uids 173-175 raw + harvested (PC-only rows not in mirrored XML - hand-add after session); phone tiles speak game GXT app names; value changes announced at close (live too if they ever move); reader-speak lines reconstruct sessions.

---

## Menu Reader v1.2 (2026-07-19) - IMPLEMENTED

v1.1 verdict (4 logs, multi-agent analysis + inline verification): plumbing worked, data was wrong. Root causes ALL confirmed: (1) Esc-cancel leak - the opening Esc press registered as INPUT_FRONTEND_CANCEL and self-closed the menu (every open in one session); (2) arrow capture dead - IS_CONTROL_PRESSED group-2 returns false for nav controls 187-190 in the hijacked frontend (110 navs, 0 arrows captured); (3) row table corrupt - regex missed <Item platform=...> openings (120/151 uids wrong past index 25); (4) uid==xmlOrder REFUTED - runtime uniqueId of pref rows is the eMenuPref ENUM VALUE (audio pane fully validates: 8=SFX, 9=Music, 11=SpeakerOutput, 13=RadioStation, 43=DiagBoost, 45/46=SS Front/Rear; camera cluster 125-139 coherent); hash rows carry d2=visible position instead; (5) phone slots 3/5 GXT keys paired backwards; (6) values commit on close (verified id305 Audio-output 0->2) but spoke raw enums.

v1.2 (sentinel reader1.2, JSON schema v3):
- Data: build-menulabels.py rewritten on ElementTree; rows keyed by (51, prefValue) with screenMenuId/kind/optionType/basis/status; optionValues from <DisplayValues> (47 types); screenItems for hashed triggers (own gxt e.g. MO_UR_QUICKSCAN); profilePrefs from NEW tools/menulabels-overrides.json (merged last, survives regen); eMenuPref.hpp cached (Stand-OSS). Phone slots 3/5 swapped; structured overrides[] on slot 0.
- Esc: hold-off disables CANCEL until opening press's KeyUp +150ms; cancel held-state pre-seeded.
- Input: OS KeyDown/KeyUp primary source (arrows/WASD/Enter/Backspace/Esc; auto-repeat passes through), native poll secondary, src=key|ctl logged.
- Speech: Info tier (interrupts, not droppable); ALL d1==-1 suppressed; 2-tick (d1,d2) stability gate; back-outs speak screen alone; no ", item N" suffixes; internal ids (INCEPT_TRIGGER, -1000 sentinel) never spoken; unknown rows say "unknown setting"; pane honesty gate (assumed rows speak only when their screen matches the tracked pane; reader-pane / reader-pane-mismatch events); double-MAP fixed; earcon + accept-noop logged.
- Values: focused verified rows append committed value ("Output, Headphones" via optionValues; sliders "N of 10"); close summary uses words; live diffs routed through reader.
- Phone: stable-home-row (>=250ms) feeds slot diagnostics; known mission overrides log phone-slot-override; phone+pause overlap guard (pause owns voice, frontend-overlap event).
- Hygiene: profile scans throttled (>=300ms nav, skip at tab level) + 20-error fuse; toggle-off resets; failed GXT retries once per menu-open; '~' strings rejected.
- Instrumentation: reader-speak src= on every utterance incl announces; reader-unmapped vs reader-gxt-fail split; labeldb-version at open; row-visit (dwell + LR count) for offline uid<->profile-id<->label binding.

Calibration protocol for next session: walk Audio/Display/Graphics rows top to bottom (row-visit events accumulate); one-change-then-close per pane binds profile ids; activate phone slots 3 and 5 to ground-truth scripts.

---

## Menu Reader v1.4 (2026-07-21) - IMPLEMENTED

v1.3 verdict (menulog-2026-07-20-134947 = deliberate calibration walk, plus -222901). Provenance confirmed first: deployed DLL md5 == GTA\bin\Debug\net48 build, build (13:33) newer than every source (13:32), log sentinel reader1.3 == source, labeldb ok(v3) overrides=4. The log DID run the latest code.

ROOT CAUSE - the eMenuPref shift boundary was wrong. build-menulabels.py guessed +4 across the whole unknown 47..120 band. The 7 pane-chain events refute it: pane 137 replays the real Graphics pane exactly at offset 0 (DXVersion, Screen Type, Resolution, Aspect, Refresh, Monitor, FXAA, MSAA, ...), pane 138 gives Long/Ultra Shadows, HD Flight, Max LOD, Shadow Dist, Frame Scaling at offset 0, while pane 140 is FORCED to +4 (uids 179/120/144/145/147 have no offset-0 candidate). True step function: s(h)=0 for h<=96, s(h)=+4 for h>=108, with the 4 insertions inside the PREF_VOICE_* block (97..107). Consequence: EVERY Graphics/Advanced Graphics/Display row spoke a confidently wrong name softened only by an "unverified," cue - uid 79 (FXAA) said "Grass Quality", uid 84 (Ambient Occlusion) said "MSAA", uid 89 (Distance Scaling) said "Tessellation" - and uids 47/48/56/57/58/63 were missing entirely.

Other confirmed defects: Esc self-closed the pause menu on 3 of 15 opens (cancel leaked during the ~6 frames between ACTIVATE_FRONTEND_MENU and IS_PAUSE_MENU_ACTIVE, where the v1.3 hold-off did not run); descending from the tab strip into a tab's content list was SILENT (the leak-resync early return swallowed it); the Settings category list's 3rd row spoke "unknown item" (INCEPT row - d2 is the TARGET SCREEN, KEYMAP=148, gxt PM_PANE_KEYS); most rows spoke no value at all (graphics settings are not profile settings); phone in-app rows all spoke "row N" with NO harvest logging, and PhoneGlobals failed detection (empty count==222 sweep refuted the count-sentinel premise).

v1.4 changes (sentinel reader1.4, JSON schema v4):
- Data: SHIFT_BOUNDARY 121->108, unpinned band 97..107; build-time anchor assertions (uid 79->FXAA, 83->Anisotropic, 84->AmbientOcc, 89->DistScale, 96->VidOverride, 112->MouseType, 175->MuteOnFocusLoss) - one of these caught an error in the plan itself. keyConfidence: unpinned 63->11 (Voice Chat only), confirmed 26->98.
- align-panes.py repaired: the three constraints that made all six panes fail were all bugs in the tool. (1) reconstruct_track rebuilds the row cycle from every observed adjacency, surviving oscillating walks that cut-at-first-repeat mangled (Audio recovered 4->10 rows, Controls 2->17). (2) The "unconditional rows are never skipped" hard rule is gone - false on this build. (3) The offset window is now the piecewise s(h). Rooting is decided by root_track against pausemenu.xml order (the chain never records the activation-focus row, so the track is an arbitrary rotation); ord is claimed ONLY on a unique fit. Recovered s(h) has a single step at header 108, monotonic, both anchors satisfied.
- Runtime pane membership fixed: this build moved uids 56/57/58/61 into Graphics(137) and 81 into Advanced Graphics(138) vs the mirrored XML - source of all 18 reader-pane-mismatch events.
- Reader: INCEPT rows resolve via TryMenuLabel(d2) -> "Key Bindings"; leak-descent detection speaks the first content row instead of returning silently (EVENT reader-tab-descend); Esc hold-off now runs regardless of mrSnap.Active AND masks MR_IN_CANCEL from the native poll (which read IS_DISABLED_CONTROL_PRESSED and so saw the disabled key anyway), with EVENT esc-holdoff telemetry; pane no longer mis-seeded from the tab leak anchor on tab activation; reader-unmapped/gxt-fail carry pane=; pause-sel and FRAME lead with cur=d1/d2 (menu=/item= are one nav stale and misled triage).
- Values: new GTA\SettingsXmlDb.cs reads Documents\Rockstar Games\GTA V\settings.xml (re-read per menu open; GTA rewrites it on close, matching commit-on-close). Mapping is DATA (settingsXmlMap in menulabels-overrides.json), modes index|lookup|bool|pct|number|res. 30 of 33 graphics rows now speak a value; MSAA/ReflectionMSAA read as sample counts (settings.xml demonstrably stores literals - AnisotropicFiltering is 16); Shadow_SoftShadows(=5 vs a 4-entry list) and SamplingMode are deliberately UNMAPPED so they stay silent and log settingsxml-unmapped rather than risk a wrong value. Values are suppressed for cued/unverified rows and mid-change (existing B5 guard).
- Phone: in-app labelling refactored into a provider chain (static phoneAppRows table -> globals -> "row N"), all fail-closed. reader-unmapped-phone now fires for IN-APP rows (script=, row=) - v1.3 harvested the home grid only, so no calibration signal existed. First in-app row announces via a 400 ms armed timer rather than resetting mrPhoneLastRow (which would have announced the stale home row as an in-app one - the mirror of the B4 bug). "Phone closed" is overlap-gated like every other phone utterance; app exit folds into one line, "Home, <tile>".
- PhoneGlobals rewritten: signature is now the DATA (>=6 distinct printable names at a fixed stride), stride is searched {29..33}, the scan is frame-budgeted (200 probes) and resumable, offsets can be PINNED as data (phoneGlobals block) so a future session is a JSON redeploy not a rebuild. RangeAllocated() resolves the far end of every struct walk through GlobalVariable.Get first - GetArrayItem/GetStructField are unchecked pointer arithmetic and an access violation is not a catchable managed exception; crashing a blind user's game is far worse than never resolving a name.
- Texts/Email: zero runtime evidence exists for those stores, so v1.4 builds the machinery and the EVIDENCE, not a guess. ArmStoreSweep/StoreSweepTick emit phone-store-candidate lines only; nothing from that channel is ever spoken until a log pins it.
- New one-shot CELL_* GXT dump (menuDebugLog-gated, 40 keys/frame, once per session): phone app row tables have no offline source the way pausemenu.xml serves the pause menu, so this harvests the game's own localized string table to build phoneAppRows from.

Verification: offline assertions all pass (14/14 uid->pref->pane checks, JSON v4, unpinned == the 11 voice prefs, settings.xml decode simulated across panes 137/138). dotnet build clean, deployed to the Legacy install (DLL + JSON md5 verified). IN-GAME SESSION STILL PENDING - see the plan file's verification section; phoneAppRows is still EMPTY by design, so every in-app row correctly still says "row N" until the harvest lands.
