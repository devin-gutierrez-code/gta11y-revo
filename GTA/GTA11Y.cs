using GTA;
using GTA.Native;
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using DavyKager;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json;

namespace GrandTheftAccessibility
{
    enum GuardCombatState { Idle, Engaging, Flanking, Suppressing, Repositioning, Protecting }

    class GTA11Y : Script
    {
        private string currentWeapon;
        private string street;
        private string zone;
        private int health;
        private int wantedLevel;
        private float z;
        private float p;
        private bool timeAnnounced;
        private Dictionary<string, string> hashes = new Dictionary<string, string>();
        private bool[] keyState = new bool[20];
        private Random random = new Random();
        private List<Location> locations = new List<Location>();
        private List<VehicleSpawn> spawns = new List<VehicleSpawn>();
        private long targetTicks;
        private long drivingTicks;
        private bool keys_disabled = false;

        private int locationMenuIndex = 0;
        private int spawnMenuIndex = 0;
        private int mainMenuIndex = 0;
        private List<string> mainMenu = new List<string>();
        private int funMenuIndex = 0;
        private List<string> funMenu = new List<string>();
        private int driveMenuIndex = 0;
        private List<string> driveMenu = new List<string>();
        private int settingsMenuIndex = 0;
        private List<Setting> settingsMenu = new List<Setting>();

        // Status menu
        private int statusMenuIndex = 0;
        private const int STATUS_MENU_ITEM_COUNT = 85;
        private HashSet<int> statusMonitoredItems = new HashSet<int>();
        private long statusMonitorTicks = 0;
        private int statusMonitorRotation = 0;

        private WaveOutEvent out1;
        private WaveOutEvent out2;
        private WaveOutEvent out3;
        private WaveOutEvent out11;
        private WaveOutEvent out12;

        private AudioFileReader tped;
        private AudioFileReader tvehicle;
        private AudioFileReader tprop;
        private SignalGenerator alt;
        private SignalGenerator pitch;
        private WaveOutEvent out13;
        private SignalGenerator navBeep;
        private long navAssistTicks;

        // Navigation Assist Debug Mode - set to true to enable detailed logging
        private bool navAssistDebug = false; // DISABLED - causes game freeze when enabled
        private System.IO.StreamWriter navDebugLog = null;
        private int raycastCounter = 0;

        // Drive Assist Debug Logging - background-threaded per-frame telemetry
        private DriveAssistLogger driveLogger;
        private bool driveLogWasEnabled = false;
        private long driveLogFrameCount = 0;
        private GTA.Math.Vector3 lastLoggedPos = GTA.Math.Vector3.Zero;
        private float lastLoggedHeading = 0f;
        private float lastLoggedZ = 0f;
        private DriveMode driveLogLastMode = DriveMode.LaneKeeping;
        private const float DRIVE_LOG_BIG_Z_THRESHOLD = 1.5f;

        // Ring buffer of the most recent discrete drive-assist decisions
        // (mode changes, teleports, recovery searches). Captured for the F1
        // "player-indicated failure" debug snapshot so a play-tester can mark
        // a failure moment and see what the assist had just decided.
        // Ring of recent drive-assist decisions. Iter-9 Patch F bumped this
        // 5 -> 30 so the F1 (and AUTO-COLLISION) snapshot captures ~6 seconds
        // of decisions instead of ~1 second at the typical ~5 decisions/sec
        // rate. RecordDriveDecision and the LogCollisionSnapshot reader both
        // operate modulo the array length so only the constant changes.
        private readonly string[] driveDecisionLog = new string[30];
        private int driveDecisionLogCount = 0;
        // Monotonically incrementing failure-snapshot sequence number; F1 and
        // auto-collision events both consume from this counter so the user can
        // cross-reference markers across analysis tools.
        private int collisionMarkerSeq = 0;
        // Auto-collision last-fire-time so multi-frame contact doesn't flood
        // the log. 1 s cooldown matches the player's F1 reflex window.
        private long autoCollisionLastTicks = 0;
        private const long AUTO_COLLISION_COOLDOWN_TICKS = 10000000; // 1 s
        // Iter-11 Patch N: raised 5f -> 10f to suppress false positives from
        // scraping a curb / dropping off a ledge / fall damage. Three -6 to -8
        // healthDelta auto-collisions in driveassist-2026-05-27-171958
        // (F11991/F9918/F11824) were not real collisions. Real impacts in the
        // same log were all -30 or worse, so 10f catches every drive-assist-
        // relevant event without polluting the marker set.
        private const float AUTO_COLLISION_HEALTH_DROP = 10f;
        // Tracks chassis health between scans for the auto-collision signal.
        // Independent of lastVehicleHealth at line 790 which feeds the speech
        // "your engine is damaged" feedback feature.
        private float autoCollisionLastHealth = -1f;
        private int autoCollisionLastVehHandle = 0;

        // Navigation Assist - Track last hit to reduce repetitive beeping when stationary
        private float lastNavHitDistance = -1f;
        private int sameDistanceCount = 0;

        // Navigation Assist - Additional audio outputs for different entity types
        private WaveOutEvent out14; // Ped beep
        private WaveOutEvent out15; // Vehicle beep
        private SignalGenerator pedBeep;
        private SignalGenerator vehicleBeep;

        // Navigation Assist - Multi-directional detection with stereo panning
        // Separate outputs for left, center, right channels
        private WaveOutEvent outNavLeft;
        private WaveOutEvent outNavCenter;
        private WaveOutEvent outNavRight;
        private SignalGenerator navBeepLeft;
        private SignalGenerator navBeepCenter;
        private SignalGenerator navBeepRight;

        // Track last detection per direction to reduce spam
        private float lastDistLeft = -1f;
        private float lastDistCenter = -1f;
        private float lastDistRight = -1f;
        private float lastDistBehind = -1f;
        private int sameCountLeft = 0;
        private int sameCountCenter = 0;
        private int sameCountRight = 0;
        private int sameCountBehind = 0;

        // Navigation Assist - Behind detection (only when moving backwards)
        private WaveOutEvent outNavBehind;
        private SignalGenerator navBeepBehind;

        // ============================================
        // SMART STEERING ASSISTS SYSTEM
        // ============================================
        private long steeringAssistTicks = 0;
        private bool steeringAssistActive = false;
        private float smoothedSteerCorrection = 0f;
        private long lastAssistAnnounceTicks = 0;

        // Amphibious mode: tracks whether underwater protections are currently applied,
        // so they can be torn down exactly once when the vehicle leaves the water.
        private bool amphibiousProtectionActive = false;

        // Collision prediction tracking
        private float threatTimeToCollision = 999f;
        private string threatDirection = "none";
        private string threatType = "none";

        // Cached values for continuous input application (must be applied every tick)
        private float cachedSteerCorrection = 0f;
        private float cachedBrakeMagnitude = 0f;
        private int cachedAvoidDirection = 0;
        // Hysteresis on the avoidance direction so per-scan ping-pong doesn't
        // yaw-oscillate the wheel. Tracks the last proposed direction and how
        // many consecutive frames we've seen it. A sign flip against the most
        // recent non-zero published direction (within AVOID_DIR_MEMORY_TICKS)
        // triggers the hold — including the +1 -> 0 -> -1 sequence that the
        // earlier 3-frame gate let through because it compared to a zero
        // cached value. Clearing to 0 still publishes immediately.
        private int proposedAvoidDirection = 0;
        private int avoidDirHoldFrames = 0;
        private const int AVOID_DIR_FLIP_HOLD = 6;                  // ~100 ms at 60 fps
        private int lastNonZeroAvoidDir = 0;                        // most recent non-zero publishedAvoidDir
        private long lastNonZeroAvoidDirTicks = 0;                  // wall-clock for memory decay
        private const long AVOID_DIR_MEMORY_TICKS = 5000000;        // 500 ms — clear memory after this idle
        private bool cachedIsFullMode = false;
        private bool cachedIsBraking = false; // True when system is actively braking (blocks throttle in full mode)

        // Per-frame ramped brake input — actual control value sent to the game each
        // frame. Lerps toward cachedBrakeMagnitude (target) at BRAKE_RAMP_RATE/sec so
        // the brake feels physical instead of jumping from 0 → 1 in one frame.
        private float rampedBrakeInput = 0f;
        // True when the first-contact emergency-brake path latched a max-brake request.
        // Read by ApplyCachedSteeringInputs to force cachedBrakeMagnitude = 1.0 until
        // the obstacle leaves the critical zone.
        private bool emergencyBrakeActive = false;

        // Cached closest brake-relevant threat position & detection timestamp. Used by
        // ApplyCachedSteeringInputs to recompute TTC every frame against the live
        // vehicle speed/position (between the ~50 ms full re-scans), so player input
        // changes and threat motion are reflected in real time.
        private GTA.Math.Vector3 cachedBrakeThreatPos = GTA.Math.Vector3.Zero;
        private GTA.Math.Vector3 cachedBrakeThreatVel = GTA.Math.Vector3.Zero;
        private long cachedBrakeThreatStamp = 0;
        // First-seen tracking for the cached brake threat. The Stamp field is
        // refreshed every frame the scan reconfirms the threat — useless for
        // staleness when the cache is being continuously re-confirmed by a curb
        // or roadside feature. FirstSeenStamp captures when the threat was
        // initially noticed (only reset when the cache actually clears) plus the
        // player position at that moment, so we can detect the F4137-style
        // "stuck against same spot, cache never clears" pattern.
        private long cachedBrakeThreatFirstSeenStamp = 0;
        private GTA.Math.Vector3 cachedBrakeThreatFirstSeenPlayerPos = GTA.Math.Vector3.Zero;
        // Iter-11 Patch I: 3-frame distance history for the drove-past
        // confirmation. The legacy single-frame fwdDot<=0.15 predicate fires
        // on momentary heading swings during evasion — F5650 in
        // driveassist-2026-05-27-171958 cleared the cache 26 times in 1 sec
        // before the -84.8 health impact. We now require BOTH fwdDot<=0.15
        // AND distance-to-threat rose by >0.5 m across the last 3 frames.
        // The history is reset when the cache clears, on teleport-reset,
        // and when a new threat replaces a different one.
        private float[] cachedBrakeDistHistory = new float[3];
        private int cachedBrakeDistIdx = 0;
        private bool cachedBrakeDistHistoryValid = false;
        // Same for the closest steer-relevant threat.
        private GTA.Math.Vector3 cachedSteerThreatPos = GTA.Math.Vector3.Zero;
        private GTA.Math.Vector3 cachedSteerThreatVel = GTA.Math.Vector3.Zero;
        private long cachedSteerThreatStamp = 0;
        // How long a cached threat remains "trusted" (5 detection cycles ~ 250 ms).
        // Beyond this we don't try to recompute — wait for the next full scan.
        private const long THREAT_CACHE_VALID_TICKS = 2500000; // 250 ms
        // Iter-11 Patch J: speed-scaled cache validity. A 250 ms-old threat
        // position is 4 m off at 16 m/s and 6 m off at 25 m/s — exactly the
        // F2894 (-102.6 health) error magnitude. Floor at 800000 ticks (80 ms)
        // so we never starve under the next full scan's latency. Used at the
        // hasLiveBrakeThreat staleness check in ApplyCachedSteeringInputs.
        private const long THREAT_CACHE_VALID_TICKS_MIN = 800000; // 80 ms floor
        private long ThreatCacheValidTicksForSpeed(float speedMs)
        {
            // At 8 m/s -> 2,500,000 (unchanged 250 ms).
            // At 16 m/s -> 1,250,000 (125 ms).
            // At 25 m/s -> 800,000 (80 ms floor).
            float scale = 8f / Math.Max(8f, speedMs);
            long scaled = (long)(THREAT_CACHE_VALID_TICKS * scale);
            return Math.Max(THREAT_CACHE_VALID_TICKS_MIN, scaled);
        }

        // Brake input ramps toward target at this rate (per second). 5.0 means a full
        // 0 → 1 transition takes 200 ms — fast enough for emergencies, smooth enough
        // not to feel like a slam.
        private const float BRAKE_RAMP_RATE = 5.0f;
        // Iter-11 Patch M: asymmetric urgent-up ramp rate. The default 5.0/sec
        // (0->1 in 200 ms) is the right comfort shape for routine brakes but
        // 200 ms is too long when liveBrakeTarget jumps from 0 to high
        // magnitude at highway speed — driveassist-2026-05-27-171958 F3542
        // (-83.2 health) impact happened with rampedBrake=0.341 because the
        // ramp was still climbing from 0. URGENT path: when the target leaps
        // ahead of the ramp by >0.3 AND speed > 10 m/s, ramp up at 12/sec
        // (0->1 in 83 ms). Down-ramp stays at the comfort rate so brake
        // release isn't grabby.
        private const float BRAKE_RAMP_RATE_URGENT = 12.0f;

        // Lane-keeping state. lastRoadCorrection is the previous frame's raw road
        // steer output; we use it to rate-limit changes so a noisy lookahead point
        // can't whip the wheel back and forth ("drives in circles" complaint).
        private float lastRoadCorrection = 0f;
        // Below this pursuit angle (degrees) the lane-keeping system contributes ZERO
        // steering. This deadband is the single biggest fix for the oscillation bug:
        // on a straight road, suspension/road-node noise produces ~1° heading jitter,
        // and the previous proportional-only controller chased that noise into
        // ever-growing corrections.
        private const float LANE_DEADBAND_DEGREES = 2.0f;
        // Lateral deadband (metres): tolerate small cross-track drift before nudging.
        private const float LANE_LATERAL_DEADBAND_METERS = 0.75f;
        // Max change in lane-keeping correction per second. Smaller = smoother, less
        // oscillation, but slower response to genuine curves. 1.5 is enough to track
        // a normal city corner taken at moderate speed.
        private const float LANE_CORRECTION_RATE = 1.5f;
        // Lane width assumption used by IsObstacleInTravelLane when MapDb returns
        // no road match. Overridden by per-road-type values from LANE_PRESETS.
        private const float LANE_DEFAULT_WIDTH = 3.5f;

        // Road-type-aware lane-keeping presets. Looser on highways (where wheel
        // jitter is amplified by speed and a strict deadband would feel grabby),
        // tighter in alleys (where the car is centimetres from walls and any drift
        // is meaningful). The "surface" / unknown fallback matches the legacy
        // constants above so behaviour is unchanged when MapDb is empty.
        private struct LanePreset { public float deadbandDeg, lateralM, rate, widthM; }
        private static LanePreset GetLanePreset(string roadType)
        {
            switch (roadType)
            {
                case "freeway":
                    return new LanePreset { deadbandDeg = 3.5f, lateralM = 1.5f,  rate = 0.9f, widthM = 4.0f };
                case "highway":
                    return new LanePreset { deadbandDeg = 3.0f, lateralM = 1.2f,  rate = 1.0f, widthM = 3.8f };
                case "alley":
                    return new LanePreset { deadbandDeg = 1.5f, lateralM = 0.4f,  rate = 2.0f, widthM = 2.5f };
                default: // surface, unknown
                    return new LanePreset {
                        deadbandDeg = LANE_DEADBAND_DEGREES,
                        lateralM    = LANE_LATERAL_DEADBAND_METERS,
                        rate        = LANE_CORRECTION_RATE,
                        widthM      = LANE_DEFAULT_WIDTH,
                    };
            }
        }
        private bool wasObstacleInBrakeZone = false; // Track latched emergency-brake critical-zone state (release-hysteresis gated)
        private int criticalZoneArmFrames = 0;        // Consecutive enter-frames; arm only after CRITICAL_ARM_FRAMES

        // Iter-12 Patch Q: surface-aware off-road detection. The existing
        // distance-only logic in CheckRoadTeleport conflates dirt-road nodes
        // in GTA V's nav graph with real roads — Sandy Shores has dirt nodes
        // within 5-15 m of the player even when they're 50+ m into pure
        // desert. GET_STREET_NAME_AT_COORD returns streetHash=0 anywhere
        // off a named street, so a SUSTAINED streetHash=0 reading is a
        // strong off-road signal independent of nav-node distance. Hysteresis
        // (500 ms) prevents false-positives in large parking lots and during
        // momentary nav-mesh-glitch frames.
        private long offNamedStreetSinceTicks = 0;
        private bool offNamedStreetLogged = false;
        private const long OFF_NAMED_STREET_HYSTERESIS_MS = 500;

        // Last-announced location strings for the autodrive informational
        // announcements. Empty until the first announcement; updated only on
        // transition so the system speaks "Entering Vinewood" once, not every tick.
        private string lastAnnouncedRoadName = "";
        private string lastAnnouncedDistrict = "";

        // NPC AI-inspired obstacle avoidance tracking
        private float previousFrameSteer = 0f;                  // Last frame's combined steer value (for rate limiting)
        private GTA.Math.Vector3 closestThreatPosition = GTA.Math.Vector3.Zero; // Position of closest steering threat
        private string closestBrakeObstacleType = "none";       // Obstacle type for brake decisions ("vehicle", "pedestrian", "obstacle")

        // Nav assist distances shared with drive assist for improved obstacle avoidance
        private float navAssistDistLeft = 999f;
        private float navAssistDistCenter = 999f;
        private float navAssistDistRight = 999f;
        private float navAssistDistBehind = 999f;
        private string navAssistTypeLeft = "none";
        private string navAssistTypeCenter = "none";
        private string navAssistTypeRight = "none";
        private string navAssistTypeBehind = "none";

        // Surface normal of the center shape-cast hit (drive-assist uses this to
        // decide brake-vs-swerve). Zero when no usable normal was captured.
        private GTA.Math.Vector3 navAssistNormalCenter = GTA.Math.Vector3.Zero;

        // Nav assist detected entities - shared for drive assist to use actual velocities for TTC
        private Vehicle navAssistVehicleCenter = null;
        private Vehicle navAssistVehicleLeft = null;
        private Vehicle navAssistVehicleRight = null;
        private Vehicle navAssistVehicleBehind = null;
        private Ped navAssistPedCenter = null;
        private Ped navAssistPedLeft = null;
        private Ped navAssistPedRight = null;

        // Vehicle spatial awareness for handbrake turn assistance
        private float cachedHandbrakeMagnitude = 0f;  // Handbrake input for corrective turns
        private bool vehicleIsSkewed = false;          // True if vehicle angle doesn't match road direction
        private float vehicleSkewAngle = 0f;           // Angle difference between vehicle heading and road heading

        // Auto-teleport to road tracking
        private float lastValidRoadDistance = 999f;    // Distance to last valid same-direction road node
        private long offRoadStartTicks = 0;            // When we started being far from road (for 5-second timer)
        private bool wasCloseToRoad = true;            // Track if we were recently close to road
        // Iter-10 Patch C: hysteresis on the close-to-road reset path. Without
        // this, a single frame of "close to road" (e.g. a shoulder clip
        // during off-road drift) wipes the 5 s off-road accumulator and the
        // timeout teleport never fires. driveassist-2026-05-25-231603 F5397
        // shows the car off-road for 30 s without an offroad-timeout teleport
        // for exactly this reason. The accumulator now only clears after
        // OFFROAD_RESET_HYSTERESIS_MS of CONTINUOUS close-band frames.
        private long closeToRoadConfirmTicks = 0;
        private const long OFFROAD_RESET_HYSTERESIS_MS = 1000;
        private const float ROAD_CLOSE_THRESHOLD_BASE = 15f; // Base max distance to be considered "on road"
        private const float ROAD_FAR_THRESHOLD_BASE = 10f;   // Base distance for timer (increased from 8)
        private const long ROAD_TELEPORT_DELAY_TICKS = 50000000;  // 5 seconds (was 10; at 30 m/s the old window let the car drift 300 m off-road before recovery teleport — audit driveassist-2026-05-25-121415)
        private const long ROAD_TELEPORT_COOLDOWN_TICKS = 30000000; // 3 second cooldown between teleports
        private long lastTeleportTicks = 0;              // Last time we teleported

        // Audio feedback
        private WaveOutEvent outSteerAssist;
        private SignalGenerator steerAssistBeep;

        // Pre-impact brake warning beep. Sawtooth at middle C — distinct from the
        // sine-wave steer beep so a VI user can tell "stop now" apart from a steer
        // nudge. Rate and gain ramp with TTC; see PlayBrakeWarning.
        private WaveOutEvent outBrakeWarn;
        private SignalGenerator brakeWarnTone;
        private long lastBrakeWarnTicks = 0;

        // Thresholds (seconds to collision)
        private const float STEER_SMOOTHING_RATE = 5.0f; // Units per second (frame-rate independent, reduced from 8 for smoother lane keeping)
        private const float BRAKE_THRESHOLD_FULL = 1.5f;       // Only brake when collision is very imminent
        private const float BRAKE_THRESHOLD_ASSIST = 1.0f;
        private const float MIN_BRAKE_DISTANCE = 3f;          // Emergency-latch threshold: obstacles inside this slam-brake
        private const float MIN_BRAKE_DISTANCE_FULL = 5f;     // Full mode has slightly more buffer
        // Floor for the *graceful* threat-brake. Obstacles between this and
        // MIN_BRAKE_DISTANCE still get ramped braking (which reduces impact
        // speed) instead of relying solely on the emergency one-shot — and the
        // resulting brakeCmd telemetry is non-zero so the brake state is visible.
        private const float BRAKE_MIN_USEFUL_DIST = 1.0f;
        private const float STEER_THRESHOLD_FULL = 4.0f;
        private const float STEER_THRESHOLD_ASSIST = 2.5f;
        private const float BASE_COLLISION_RADIUS = 2.5f;     // Base collision radius, scaled by vehicle size

        // NPC AI-inspired per-obstacle-type parameters (from vehicleaihandlinginfo.meta)
        // Braking initiation distances (forward distance at which braking begins)
        private const float BRAKE_DIST_VEHICLE = 6.0f;
        private const float BRAKE_DIST_PED = 4.0f;
        private const float BRAKE_DIST_OBJECT = 3.5f;
        // Swerve initiation distances (forward distance at which swerving begins - larger than brake)
        private const float SWERVE_DIST_VEHICLE = 8.0f;
        private const float SWERVE_DIST_PED = 5.0f;
        private const float SWERVE_DIST_OBJECT = 5.0f;
        // Lateral avoidance clearance (how far to the side we aim to pass)
        private const float AVOID_LATERAL_VEHICLE = 4.0f;
        private const float AVOID_LATERAL_PED = 3.0f;
        private const float AVOID_LATERAL_OBJECT = 2.5f;
        // Above this speed (m/s), prefer swerving over braking (from fSpeedForSwerving)
        private const float SPEED_FOR_SWERVING = 10.0f;
        // Max steering change per second - prevents flip-around and oscillation
        private const float MAX_STEER_RATE = 2.0f;
        // Minimum angle (degrees) to obstacle before braking is preferred over swerving
        private const float MIN_STEER_ANGLE_FOR_BRAKING = 30.0f;
        // Base look-ahead distance for path projection (from fLookAheadDist / fAheadSpeedFollowDist)
        private const float LOOK_AHEAD_BASE = 20.0f;

        // ============================================
        // ALIGNMENT / RECOVERY (strict fallback when lane-keep fails)
        // ============================================
        // Recovery engages when the nearest road node is farther than this in
        // meters; otherwise the closer-but-skewed case becomes AligningHeading.
        private const float RECOVERY_DISTANCE_ENGAGE = 8f;
        // Hysteresis exit: once in RecoveringToRoad, only drop to AligningHeading
        // when the target is closer than this. The 2 m band stops the mode
        // flip-flop the log showed at the 8 m boundary.
        private const float RECOVERY_DISTANCE_DISENGAGE = 6f;
        // Abandon a latched recovery target and re-resolve once it is this far
        // away — the car has wandered and the node is no longer useful.
        private const float RECOVERY_TARGET_ABANDON = 60f;
        // When |delta| above this AND speed below REVERSE_UTURN_SPEED, perform a
        // reverse U-turn (back up + counter-steer) to break out of the dead zone.
        // Lowered from 150 deg: the common stuck case is a ~85 deg skew at creep
        // speed, which 150 never caught — so the maneuver never fired.
        private const float REVERSE_UTURN_ANGLE = 75f;
        private const float REVERSE_UTURN_SPEED = 3f;
        // Below this speed the alignment steer is allowed to saturate; above it
        // alignment softens so high-speed mistakes don't pitch the car sideways.
        private const float ALIGN_LOW_SPEED_SATURATE = 5f;
        // How many nth-closest nodes to scan when picking a recovery target.
        private const int ALIGN_SCAN_NODE_COUNT = 15;

        // ============================================
        // STEERING ASSIST v3 — PURE PURSUIT + SAFETY GATES + WARNING BEEP
        // ============================================
        // Pure-pursuit lateral controller (replaces the Stanley-ish raw-error model
        // that chattered on noisy path-node data and hugged guard rails).
        private const float PURE_PURSUIT_K_V = 0.55f;         // Lookahead seconds: L_d = k_v*v + L_min
        private const float PURE_PURSUIT_L_MIN = 6.0f;        // Lookahead floor (m), creep speed
        private const float PURE_PURSUIT_L_MAX = 35.0f;       // Lookahead ceiling (m), highway cap
        private const float PURE_PURSUIT_WHEELBASE = 2.8f;    // Approximate sedan wheelbase
        private const float PURE_PURSUIT_DELTA_MAX = 0.61f;   // ~35° max steering lock in radians
        private const float GOAL_LPF_TAU = 0.25f;             // First-order LPF on goal point
        private const float LANE_WIDTH_HIGHWAY = 3.7f;        // US highway lane width
        private const float LANE_WIDTH_SURFACE = 3.2f;        // Surface street lane width
        private GTA.Math.Vector3 ppGoalSmoothed = GTA.Math.Vector3.Zero;
        private bool ppGoalInitialized = false;
        // Lane-selection hysteresis. Without this, when the player sits near a
        // lane boundary the Math.Round() in LaneCenterFromNode flips between
        // adjacent lane indices on noise frames, producing a goal point that
        // jumps a full lane width side-to-side. Requires the player to cross
        // the boundary by 0.4 m before accepting a one-step lane change.
        private int lastLaneIndex = int.MinValue;
        private const float LANE_CHANGE_HYSTERESIS_M = 0.4f;

        // Pre-emptive curve braking: slow down before a sharp bend so the
        // vehicle does not exceed its lateral grip limit and spin out.
        private const int   CURVE_BRAKE_LOOKAHEAD_PTS = 3;    // polyline segments to inspect ahead
        private const float CURVE_SHARP_ANGLE_DEG     = 25f;  // below this = gentle bend, ignore
        private const float CURVE_LATERAL_ACCEL_MAX   = 4.5f; // m/s^2 comfortable cornering grip
        private const float CURVE_BRAKE_MAX           = 0.6f; // never full-brake just for a curve
        private float curveBrakeRequest = 0f;   // 0..CURVE_BRAKE_MAX, recomputed per scan
        private int   curveBrakeStreak  = 0;     // consecutive scans a curve brake was wanted
        private int   lastClosestPolySeg = 0;    // closest pathPolyline segment, set by ComputeStanleySteer
        private long  lastCurveCueTicks = 0;     // throttle for the Assisted-mode "curve ahead" cue

        // Adaptive cruise control: PID throttle/brake to hold a safe headway
        // behind a tracked lead vehicle (Full mode only).
        private const float ACC_TIME_GAP  = 1.8f;   // desired seconds of headway
        private const float ACC_MIN_GAP_M = 6.0f;   // absolute minimum follow distance
        private const float ACC_KP        = 0.08f;
        private const float ACC_KI        = 0.01f;
        private const float ACC_KD        = 0.04f;
        private const float ACC_I_CLAMP   = 0.5f;   // anti-windup clamp on the integral term
        private float accIntegral = 0f;
        private float accPrevError = 0f;
        private long  accPrevTicks = 0;
        private float accThrottleOut = 0f;   // 0..1, applied every tick when ACC is active
        private float accBrakeOut = 0f;      // 0..1, merged into the brake pipeline
        private int   lastAccLeadHandle = 0; // detects lead-vehicle handoff for PID reset

        // Brake gating (kills spurious slams)
        private const float BRAKE_ARM_TTC = 0.8f;             // Arm autobrake at TTC <= this
        private const float BRAKE_RELEASE_TTC = 1.6f;         // Release at TTC >= this (hysteresis)
        private const float BRAKE_LOW_SPEED_TTC = 0.3f;       // Below cutoff, require imminent TTC
        private const float BRAKE_LOW_SPEED_CUTOFF = 2.24f;   // ~5 mph in m/s
        private const int BRAKE_MONOTONIC_FRAMES = 3;         // Require N drops before arming
        private const long ENTITY_AGE_MIN_TICKS = 1000000;    // 100 ms before an entity can fire brake
        private bool brakeArmed = false;
        private Dictionary<int, long> entityFirstSeenTicks = new Dictionary<int, long>();
        private Dictionary<int, float> entityLastTtc = new Dictionary<int, float>();
        private Dictionary<int, int> entityMonotonicCount = new Dictionary<int, int>();
        private long entityTrackingLastPrune = 0;
        // Iter-9 Patch H: throttle for the EVENT brake-reject log lines so a
        // single tailed lead vehicle doesn't flood the log every scan.
        private Dictionary<int, long> entityLastRejectLogTicks = new Dictionary<int, long>();
        private const long BRAKE_REJECT_LOG_COOLDOWN_TICKS = 10000000; // 1 s

        // Pre-impact brake warning beep
        private const float BRAKE_WARN_TTC_MAX = 1.8f;        // Start warning at this TTC
        private const float BRAKE_WARN_TTC_MIN = 0.2f;        // Saturate (fastest+loudest) below this
        private const float BRAKE_WARN_FREQ_HZ = 262f;        // Middle C

        // Adjacent-vehicle latch (no more side-brushing)
        private const float ADJACENT_LATERAL_BAND = 2.0f;     // ±2 m lateral overlap
        private const float ADJACENT_LONG_BACK = 3.0f;        // 3 m behind to
        private const float ADJACENT_LONG_FRONT = 0.5f;       //   0.5 m ahead = "adjacent"
        private const long ADJACENT_LATCH_TICKS = 15000000;   // 1.5 s
        private const float ADJACENT_CLEAR_LATERAL = 2.5f;    // Must separate this much to clear
        private const float LATERAL_AVOID_MARGIN = 0.4f;      // Bumper margin past required clearance
        private Dictionary<int, long> adjacentLatchUntilTicks = new Dictionary<int, long>();
        private bool adjacentLatchedLeft = false;             // Vehicle currently latched on our left
        private bool adjacentLatchedRight = false;            // Vehicle currently latched on our right

        // Reverse driving support
        private bool isReversing = false;                     // True when vehicle is moving backward

        // Road-following pathfinding
        private float roadSteerCorrection = 0f;       // Steering needed to stay on road
        private float smoothedRoadCorrection = 0f;    // Smoothed road correction
        private bool isOnValidRoad = false;           // Whether we found a valid road node
        private float roadHeadingDelta = 0f;          // Difference between vehicle heading and road heading

        // Drive-mode state machine. Priority: Recovery > Alignment > LaneKeeping.
        // The alignment / recovery branches override lane-keeping so a wrong-way
        // spawn or an off-road drift gets corrected before normal lane following
        // resumes.
        private enum DriveMode { LaneKeeping, AligningHeading, RecoveringToRoad }
        private DriveMode currentDriveMode = DriveMode.LaneKeeping;
        private DriveMode lastAnnouncedMode = DriveMode.LaneKeeping;
        private long lastDriveModeAnnounceTicks = 0;
        private bool alignmentEngageReverse = false;          // Reverse U-turn currently active

        // Recovery target cache — populated by FindBestRecoveryNode each detection cycle.
        private GTA.Math.Vector3 recoveryTargetPos = GTA.Math.Vector3.Zero;
        private float recoveryTargetHeading = 0f;
        private float recoveryTargetDistance = 999f;
        private float recoveryHeadingDelta = 0f;
        private bool hasRecoveryTarget = false;

        // Spawn / vehicle-change detection: force alignment mode briefly when the
        // player enters a new vehicle so wrong-way spawns get corrected.
        private int lastDriveAssistVehicleHandle = 0;
        private long vehicleEntryTicks = 0;
        // Teleport detection: the player can move long distances (custom warps,
        // in-game teleports, fast-travel) without changing vehicle handle, so
        // the per-vehicle-change reset below misses the carryover. Track last
        // observed position and treat any planar jump > TELEPORT_JUMP_M as a
        // teleport, clearing the same state the vehicle-change path clears
        // PLUS the threat caches and path polyline (which would otherwise
        // continue steering / braking toward the OLD location).
        private GTA.Math.Vector3 lastDriveAssistVehiclePos = GTA.Math.Vector3.Zero;
        private bool lastDriveAssistVehiclePosValid = false;
        // Iter-9 Patch C lowered this from 50 m to 10 m. The 50 m threshold
        // was sized for player long-range teleports (>500 m), but
        // driveassist-2026-05-25-180748 showed CheckRoadTeleport jumps were
        // 15-25 m and never tripped the iter-7 reset. CheckRoadTeleport also
        // calls ResetForTeleport() directly now; this constant is a defensive
        // belt-and-suspenders for any other code path that moves the vehicle.
        private const float TELEPORT_JUMP_M = 10f;
        // Iter-9 Patch C: after any teleport (player or CheckRoadTeleport),
        // hold a moderate brake for ~500 ms so the next 1-2 scans can rebuild
        // a fresh threat picture before the car continues approaching anything
        // at the new location. Field is set by ResetForTeleport(); consumed by
        // ApplyCachedSteeringInputs which floors liveBrakeTarget at 0.4 while
        // active.
        private long postTeleportBrakeHoldUntilTicks = 0;
        // Iter-10 Patch E: shorter / gentler post-teleport brake hold. The
        // original 500 ms × 0.4 floor combined with the reverse-arrest
        // velocity (-5 m/s set inside TeleportToNearestRoad) reliably
        // stalled the car to zero before recovery could engage — every one
        // of the 7 teleports in driveassist-2026-05-25-231603 shows this.
        // 250 ms × 0.25 floor still dampens the immediate post-teleport
        // approach but releases in time for recovery steering to take over
        // before speed hits zero.
        private const long POST_TELEPORT_BRAKE_HOLD_TICKS = 2500000; // 250 ms
        private const float POST_TELEPORT_BRAKE_FLOOR = 0.25f;
        // Count consecutive cycles where GetLaneCenterGuidance returned no valid
        // road. Lane-keep can momentarily fail at sharp curves, overpass shadows,
        // or odd node layouts; flipping to alignment on a single failure caused
        // wandering. Require 3 consecutive failures before falling back.
        private int laneKeepFailureStreak = 0;
        private const int LANEKEEP_FAILURE_HYSTERESIS = 3;
        // Wall-clock decay for the fail streak. Under contested chaos
        // laneKeepOk flickers and the streak gets stuck near its cap. Track the
        // last time the streak was incremented and decay by 1 every
        // STREAK_TIME_DECAY_MS that passes without a new increment, regardless
        // of the per-frame OK check. This lets the streak drain on its own when
        // the player has driven through a noisy patch.
        private long lastStreakIncrementTicks = 0;
        private const long STREAK_TIME_DECAY_MS = 500;
        // EARLY SKEW DETECTION: lane-keep reports "on road" whenever a path
        // polyline exists, even while the car is badly rotated off the road
        // tangent. A sustained large heading error counts as a lane-keep
        // failure so recovery engages BEFORE the polyline collapses at ~75 deg.
        // Tightened 2026-05-25 (driveassist-2026-05-25-121415 audit): the old
        // 38 deg / 10 frame gate let skewAngle climb to 94 deg with mode still
        // LaneKeeping (F26733). 30 deg / 6 frames catches off-ramp / sharp-
        // corner misalignments while they're still correctable.
        private const float LANEKEEP_SKEW_FAIL_ANGLE = 30f;
        private const int LANEKEEP_SKEW_FAIL_FRAMES = 6;
        private int skewFailureStreak = 0;
        // PERSISTENT-SKEW ESCALATION. The 4-layer stuck recovery (see
        // MonitorStuckAutodrive) only fires when speed < 0.7 m/s, which means a
        // car still rolling but pinned >50 deg sideways never escalates. The
        // dominant failure pattern in driveassist-2026-05-25-012227 was exactly
        // this: skew 78-97 deg, failStreak=20, mode-flipping every ~180 ms and
        // never recovering. This adds two earlier escalation rungs that fire on
        // SKEW DURATION, not on stop duration:
        //   2 s skewed -> bypass the steer rate-clamp so the saturated road
        //                 correction actually executes in one frame
        //   4 s skewed + low speed -> SET_VEHICLE_ON_GROUND_PROPERLY directly
        private const float PERSISTENT_SKEW_ANGLE       = 50f;
        private const int   PERSISTENT_SKEW_BYPASS_MS   = 2000;
        private const int   PERSISTENT_SKEW_RIGHT_MS    = 4000;
        private const float PERSISTENT_SKEW_RIGHT_SPEED = 2.0f;
        private long persistentSkewStartTicks = 0;
        private bool bypassSteerRateClampThisFrame = false;
        private long lastSkewRighteningTicks = 0;
        private const long SKEW_RIGHTEN_COOLDOWN_TICKS  = 60000000; // 6 s
        // Recovery target is latched once chosen (see FindBestRecoveryNode call
        // site) so the car pursues a STABLE node instead of chasing a moving
        // "nearest node" and orbiting it.
        private bool recoveryTargetLatched = false;
        // Recovery braking request (0..1), set by GetAlignmentRecoverySteer and
        // applied in ApplyCachedSteeringInputs independent of threat gating.
        private float recoveryBrakeRequest = 0f;
        // Latched U-turn direction (-1/0/+1). Near +/-180 deg heading error the
        // sign of headingDelta flips frame-to-frame as the angle wraps; latching
        // one direction keeps the car committed to a single U-turn.
        private int recoveryUturnDir = 0;
        // Iter-9 Patch E: dense-traffic-aware recovery pause. When a recovery
        // target is being pursued but the path is physically blocked by 2+
        // vehicles within 10 m and the car is creeping (<2 m/s for ~800 ms),
        // hold a brake and suppress the recovery steer until the surroundings
        // clear (no vehicle within 5 m for 500 ms). Prevents the F3527-style
        // contact-collision-while-trying-to-recover-through-traffic failure.
        private long recoveryPausedSinceTicks = 0;
        private long recoveryLastClearTicks = 0;
        private bool recoveryPauseLogged = false;
        private const int RECOVERY_PAUSE_RESUME_MS = 500;
        private const int RECOVERY_PAUSE_STALL_MS = 800;
        private const float RECOVERY_PAUSE_CLOSE_R = 5f;
        private const float RECOVERY_PAUSE_BLOCK_R = 10f;
        private const int RECOVERY_PAUSE_BLOCK_COUNT = 2;
        private const float RECOVERY_PAUSE_BRAKE = 0.6f;

        // ============================================
        // ROLLING HISTORY BUFFER — per-frame snapshot of the drive-assist
        // decision state. Lets gates require sustained evidence (e.g. "stayed
        // off the road for >250 ms") instead of acting on a single noisy frame.
        // Addresses three failure clusters observed in driveassist logs:
        //  C1 — single-frame avoid-steer slams (instant 0->0.8 felt as a jolt)
        //  C2 — mode oscillation (LaneKeeping <-> Aligning every ~60 frames)
        //  C3 — gridlock where a stale brake-threat held the car stopped
        // The polyline itself is NOT stored — it must rebuild each frame
        // (spatial derivative of vehicle pos). Only derived metrics are kept.
        // ============================================
        private struct DriveAssistSnapshot
        {
            public long  TimestampTicks;
            public long  FrameCount;
            public float DeltaTimeSec;

            public float SpeedMs;
            public GTA.Math.Vector3 Position;
            public float Heading;

            public bool  IsOnValidRoad;
            public float RoadHeadingDelta;
            public float LaneLateralError;
            public int   ClosestPolySegIdx;
            public int   PolylinePointCount;

            public DriveMode Mode;
            public int   LaneKeepFailureStreak;
            public bool  LaneKeepOkRaw;   // pre-hysteresis; gate queries use this

            public float CachedSteerCorrection;
            public float SmoothedSteerCorrection;
            public float SmoothedRoadCorrection;
            public int   CachedAvoidDirection;
            public float CachedBrakeMagnitude;
            public bool  EmergencyBrakeActive;

            public float SteerTTC;
            public float BrakeTTC;
            public GTA.Math.Vector3 CachedBrakeThreatPos;
            public long  CachedBrakeThreatStamp;
            public bool  HasSteerThreat;
            public bool  HasBrakeThreat;

            public float NavDistCenter, NavDistLeft, NavDistRight, NavDistBehind;
        }
        private const int HISTORY_CAPACITY  = 32;   // ~500 ms @ 60 fps; slack for spikes
        private const int HISTORY_WINDOW_MS = 200;  // default rolling-query window
        private DriveAssistSnapshot[] history = new DriveAssistSnapshot[HISTORY_CAPACITY];
        private int historyHead  = 0;   // index of next write
        private int historyCount = 0;   // saturates at HISTORY_CAPACITY

        // C2 — mode-transition hysteresis. Sustained-evidence gates in both
        // directions; cap the failure streak so it can't accumulate to 46+ as
        // the audit observed. Times in ms — frame-rate-independent.
        private long lastModeChangeTicks = 0;
        // Tuned 2026-05-25: mode flips were happening at ~530 ms intervals (the
        // old 500 ms hysteresis + ~30 ms of evidence slack). Widen hysteresis to
        // 800 ms and shorten the "all OK" confirm to 250 ms so the gap between
        // "can return to LaneKeeping" and "can flip back out" is wider than the
        // observed flip cadence.
        // Iter-10 Patch G (2026-05-26): driveassist-2026-05-26-000252 still
        // showed 398 mode-change events / 8 min = 49 flips/min even after
        // iter-9 widening. Bump MODE_CHANGE_HYSTERESIS to 1500 ms (halves
        // max flip rate) and bump LANEKEEP_CONFIRM to 700 ms so a brief
        // on-road flicker mid-recovery can't yank us back. ALIGN_CONFIRM
        // stays at 250 ms — the goal is to dampen flip-back-to-LaneKeeping,
        // not the entry into recovery (Patches A & B already make entry
        // instant for clear failures).
        private const int MODE_CHANGE_HYSTERESIS_MS   = 1500;
        private const int ALIGN_CONFIRM_MS            = 250;
        private const int LANEKEEP_CONFIRM_MS         = 700;
        private const int LANEKEEP_FAILURE_STREAK_CAP = 20;
        // Fraction of OK frames in the confirm window below which we consider
        // failure sustained. Strict "any OK frame = abort" caused false
        // negatives in noisy detection windows.
        private const float ALIGN_OK_FRACTION_MAX     = 0.20f;

        // C3 — stale brake-threat re-scan. If we've been stopped >1 s while the
        // cached brake threat is >1 s old AND emergencyBrakeActive is still set,
        // the data is almost certainly stale; force-clear and let the next scan
        // re-evaluate. 500 ms cooldown prevents re-trigger thrash.
        private long lastForcedRescanTicks = 0;
        private const int  STALE_THREAT_AGE_MS          = 1000;
        private const int  GRIDLOCK_STOPPED_MS          = 1000;
        private const float GRIDLOCK_SPEED_MS           = 0.3f;
        private const long FORCED_RESCAN_COOLDOWN_TICKS = 5000000; // 500 ms

        // ============================================
        // STUCK RECOVERY — 4-layer escalation
        // When Full-mode recovery commands the car but it is not physically
        // moving (wedged against an obstacle), escalate:
        //   Layer 1 — force the vehicle upright on all four wheels.
        //   Layer 2 — reverse out of the obstacle for up to 10 s.
        //   Layer 3 — hand to the auto-drive feature until back on a road node.
        //   Layer 4 — teleport to the nearest road.
        // ============================================
        private int  stuckLayer = 0;                // 0=not stuck,1=upright,2=reverse-out,3=auto-drive
        private long stuckSinceTicks = 0;           // when the car first stalled in a recovery mode; 0 = not stalled
        private long stuckLayerSinceTicks = 0;      // when the current layer was entered
        private GTA.Math.Vector3 stuckPosition = GTA.Math.Vector3.Zero; // where the car got stuck
        private bool stuckAutodriveEngaged = false; // true while Layer 3 has auto-drive running
        private const float STUCK_SPEED_THRESHOLD = 0.7f;    // m/s — below this in a recovery mode = not moving
        private const float STUCK_FREED_DISTANCE  = 8f;      // m moved from stuckPosition to count as freed
        private const long  STUCK_DETECT_TICKS  = 25000000;  // 2.5 s stalled before recovery starts
        private const long  STUCK_LAYER2_TICKS  = 100000000; // 10 s of reverse-out before escalating to auto-drive
        private const long  STUCK_LAYER3_TICKS  = 80000000;  // 8 s of auto-drive with no progress before teleport

        // ---- iter-13 Patch S: mode-agnostic "no planar progress" stall arm ----
        // The Sandy Shores trap (driveassist-2026-05-29-103153 failures #1-12)
        // oscillates LaneKeeping<->RecoveringToRoad every ~1.5 s. The recovery-
        // gated stuckSinceTicks zeroes on every LaneKeeping re-entry, so the 2.5 s
        // detector never fires. This second timer keys off PHYSICAL reality —
        // off-lane/skewed AND no planar (XY) movement — and only resets on real
        // motion, so the mode flip can't disarm it.
        private long noProgressSinceTicks = 0;
        private GTA.Math.Vector3 noProgressAnchorPos = GTA.Math.Vector3.Zero;
        private const long  NOPROGRESS_DETECT_TICKS = 30000000; // 3.0 s pinned before arming
        private const float NOPROGRESS_MOVE_M       = 3.0f;     // planar move that counts as progress
        private const float NOPROGRESS_SKEW_ANGLE   = 35f;      // only arms while genuinely off-lane

        // ---- iter-13 Patch T: active pivot (turn-in-place) maneuver ----
        // Stanley can't rotate a stationary car and blind users supply no
        // throttle, so a car teleported facing ~180 deg off-lane never turns.
        // The pivot drives a 3-point shuffle (forward+lock, reverse+counter-lock)
        // through the EXISTING recovery machinery (alignmentEngageReverse +
        // recovery forward-crawl) to physically rotate the nose toward the lane.
        private bool  pivotEscapeActive    = false;
        private bool  pivotPhaseForward    = true;
        private long  pivotPhaseSinceTicks = 0;
        private long  pivotStartTicks      = 0;
        private int   pivotTurnSign        = 0;     // +1 = rotate nose toward +heading-delta
        private const float PIVOT_SKEW_ANGLE  = 45f;       // engage when skew beyond this
        private const float PIVOT_DONE_ANGLE  = 20f;       // disengage when aligned within this
        private const float PIVOT_SPEED_GATE  = 3.0f;      // only pivot at low speed
        private const long  PIVOT_PHASE_TICKS = 12000000;  // 1.2 s per forward/reverse bite
        private const long  PIVOT_MAX_TICKS   = 80000000;  // 8 s hard cap, then fall through to autodrive

        // ---- iter-13 Patch V: lane-end speed governor ----
        private float prevRoadHeadingDeltaAbs   = 0f;      // last |roadHeadingDelta| for skew-rate
        private long  prevRoadHeadingDeltaTicks = 0;       // when prevRoadHeadingDeltaAbs was sampled
        private long  laneCollapseAtSpeedTicks  = 0;       // when the polyline last vanished at speed
        private const float LANE_END_SAFE_SPEED = 6f;      // m/s to slow to before a lane ends

        // ---- iter-13 Patch W: throttle-takeover announcement latch ----
        private bool  throttleTakeoverAnnounced = false;

        // ============================================
        // PATH-AWARE DRIVE ASSIST (path polyline + Stanley controller)
        // ============================================
        // Replaces the single noisy nearest-node sample used by the old pure-
        // pursuit lane-keep with a multi-point polyline composed from three
        // sources (in priority order):
        //   1. GPS route — when the player has a waypoint, sample the AI's
        //      planned route directly via GET_POS_ALONG_GPS_TYPE_ROUTE. This
        //      is the same route the in-game AI uses.
        //   2. Static node graph — MapDb.NodeGraph (67k+ nodes shipped in
        //      scripts/gta11y-nodes.json.gz) walked along the vehicle's
        //      heading. Has every node whether or not the streaming region
        //      currently has it loaded, so off-road / fresh-spawn cases work.
        //   3. Runtime nearest-node native — last-resort fallback.
        //
        // Stanley control over the resulting polyline replaces pure-pursuit;
        // it's stable at low lookahead and naturally damps the side-to-side
        // drift the user reported.
        private List<GTA.Math.Vector3> pathPolyline = new List<GTA.Math.Vector3>(8);
        private bool pathPolylineFromGps = false;
        // Previous polyline + hold counter for flip hysteresis (see
        // StabilizePolyline): the node search occasionally snaps to a parallel
        // or oncoming road, flipping the lane-keep heading and jerking the steer.
        private List<GTA.Math.Vector3> prevPathPolyline = new List<GTA.Math.Vector3>(8);
        private int polylineHoldCount = 0;
        private const int POLYLINE_MAX_HOLD = 6;
        private const float POLYLINE_FLIP_ANGLE = 70f;
        private long offroadModeStartTicks = 0; // ticks when we last left LaneKeeping; 0 = currently in LaneKeeping
        private const int PATH_POLYLINE_HOPS = 6;
        private const float PATH_POLYLINE_MAX_LOOKAHEAD = 80f;
        private const float PATH_GPS_SAMPLE_MIN = 5f;
        private const float PATH_GPS_SAMPLE_STEP = 12f;
        private const float STANLEY_K = 1.5f;             // cross-track gain
        private const float STANLEY_SOFT = 2.0f;          // m/s, denominator floor
        private const float NPC_PATH_CORRIDOR_M = 2.0f;   // entities outside this corridor are filtered
        private const float STATIC_NODE_SEARCH_RADIUS = 250f;
        // Max vertical gap (m) between a road node and the vehicle / the
        // previous polyline node. Rejects nodes on a stacked road deck — the
        // node search would otherwise snap between roads 40+ m apart in Z,
        // producing 150-220 deg heading-error jumps (debug log F8483/F8918).
        private const float PATH_NODE_MAX_Z_DELTA = 12f;
        private const long OFFROAD_TIMEOUT_TICKS = 30000000; // 3 seconds — force re-resolve if stuck
        // Native hashes (SHVDN 3.6 enum doesn't surface these; cast Hash directly).
        private const ulong HASH_GET_GPS_BLIP_ROUTE_FOUND  = 0x869DAACBBE9FA006UL;
        private const ulong HASH_GET_GPS_BLIP_ROUTE_LENGTH = 0xBBB45C3CF5C8AA85UL;
        private const ulong HASH_GET_POS_ALONG_GPS_ROUTE   = 0xF3162836C28F9DA5UL;

        // Vehicle-ahead following (lane keeping by matching the car in front)
        private Vehicle leadVehicle = null;            // The vehicle we're following
        private float leadVehicleLateralOffset = 0f;   // Lateral offset of lead vehicle from our forward path
        private float smoothedLeadFollowSteer = 0f;    // Smoothed steering correction for following lead vehicle
        private bool hasLeadVehicle = false;            // Whether a valid lead vehicle was found
        private long lastLeadVehicleSearchTicks = 0;    // Throttle lead vehicle searches

        // Waypoint-aware drive assist
        private GTA.Math.Vector3 cachedWaypointPos = GTA.Math.Vector3.Zero;  // Cached target position
        private float cachedWaypointHeading = 0f;      // Heading direction TO the waypoint (for road node preference)
        private bool hasActiveWaypoint = false;        // Whether there's an active waypoint/mission to guide toward
        private long waypointUpdateTicks = 0;          // Last time waypoint was updated

        // ============================================
        // FRAME-RATE INDEPENDENT TIMING
        // ============================================
        private long lastTickTime = 0;
        private float deltaTime = 0.016f; // Default ~60fps, updated each tick

        // ============================================
        // SHAPE CASTING CONFIGURATION
        // ============================================
        // Speed threshold (m/s) above which PerformShapeCast widens its capsule radius
        // for extra safety margin at highway speeds.
        private const float SHAPE_CAST_SPEED_THRESHOLD = 15f;

        // Drive assist: if the dot product of the center hit's surface normal and
        // the vehicle's reversed travel direction exceeds this, the surface faces
        // us nearly head-on (a flat wall) and a swerve cannot clear it -> brake.
        private const float WALL_NORMAL_FACE_DOT = 0.80f;

        // Lane cross-track error (m) past which haptic feedback buzzes the
        // controller to signal the vehicle is drifting toward a lane edge.
        private const float LANE_EDGE_RUMBLE_M = 0.9f;
        // Stanley cross-track error from the most recent ComputeStanleySteer call,
        // exposed so the haptic layer can detect lane-edge drift.
        private float lastLaneLateralError = 0f;
        // Edge-detect latches so rumble fires once per threat onset, not per tick.
        private bool rumbleBrakeArmed = false;
        private bool rumbleEdgeArmed = false;
        private bool rumbleWallArmed = false;

        // Waypoint guidance system
        private WaveOutEvent outWaypoint;
        private SignalGenerator waypointBeep;
        private bool waypointTrackingActive = false;
        private long waypointBeepTicks = 0;

        // Enemy detection system
        private WaveOutEvent outEnemy;
        private SignalGenerator enemyBeep;
        private long enemyCheckTicks = 0;
        private List<Ped> trackedEnemies = new List<Ped>();
        private long enemyBeepTicks = 0;

        // Butler beacon audio
        private WaveOutEvent outBeacon;
        private SignalGenerator beaconBeep;

        // ============================================
        // NEW FEATURES - Batch 1
        // ============================================

        // Pickup Detection System (uses pickup.wav)
        private WaveOutEvent outPickup;
        private AudioFileReader pickupSound;
        private long pickupCheckTicks = 0;
        private GTA.Math.Vector3 lastPickupPos = GTA.Math.Vector3.Zero;
        private long pickupBeepTicks = 0;
        private bool pickupTrackingActive = false;
        private string trackedPickupType = "";

        // Water/Hazard Detection System
        private WaveOutEvent outWater;
        private SignalGenerator waterRumble;
        private WaveOutEvent outDropoff;
        private SignalGenerator dropoffTone;
        private long waterCheckTicks = 0;
        private bool wasNearWater = false;

        // Vehicle Health Feedback (speech-based)
        private float lastVehicleHealth = -1f;
        private float lastVehicleEngineHealth = -1f;
        private float lastVehicleBodyHealth = -1f;
        private float lastVehiclePetrolTankHealth = -1f;
        private bool[] vehicleHealthWarnings = new bool[4]; // 75%, 50%, 25%, 10%
        private bool[] engineHealthWarnings = new bool[4];
        private bool[] bodyHealthWarnings = new bool[4];
        private bool[] petrolHealthWarnings = new bool[4];
        private long vehicleHealthCheckTicks = 0;

        // Sprint/Stamina Feedback
        private float lastStamina = 1f;
        private bool staminaWarningGiven = false;
        private long staminaCheckTicks = 0;

        // Cover Detection System (uses cover.wav)
        private WaveOutEvent outCover;
        private AudioFileReader coverSound;
        private long coverCheckTicks = 0;

        // Interactable Object Detection (uses interact.wav)
        private WaveOutEvent outInteract;
        private AudioFileReader interactSound;
        private long interactCheckTicks = 0;
        private Entity lastAnnouncedInteractable = null;

        // Traffic Awareness (speech warnings for fast vehicles)
        private long trafficCheckTicks = 0;
        private Vehicle lastWarnedVehicle = null;

        // Wanted Level Details
        private long wantedDetailsTicks = 0;

        // Mission/Blip Tracking
        private int trackedBlipHandle = -1;
        private int trackedBlipType = -1;
        private bool missionTrackingActive = false;
        private long missionBeepTicks = 0;
        private WaveOutEvent outMissionBeep;
        private SignalGenerator missionBeep;

        // Turn-by-Turn Navigation
        private long turnNavTicks = 0;
        private string lastTurnAnnouncement = "";
        private float lastTurnDistance = 0f;
        private GTA.Math.Vector3 lastPlayerPos = GTA.Math.Vector3.Zero;

        // Slope/Terrain Feedback
        private float lastGroundSlope = 0f;
        private long slopeCheckTicks = 0;

        // ============================================
        // NEW FEATURES - Batch 2
        // ============================================

        // Combat Feedback - Hit/Headshot/Kill detection
        private WaveOutEvent outHit;
        private WaveOutEvent outHeadshot;
        private WaveOutEvent outKill;
        private AudioFileReader hitSound;
        private AudioFileReader headshotSound;
        private AudioFileReader killSound;
        private long combatCheckTicks = 0;
        private Dictionary<int, int> pedHealthTracker = new Dictionary<int, int>();
        private int lastAmmoInClip = -1;
        private bool lowAmmoWarningGiven = false;

        // Vehicle Entry Detection
        private bool wasInVehicle = false;
        private Vehicle lastEnteredVehicle = null;
        private long passengerCheckTicks = 0;

        // Indoor/Outdoor Detection
        private bool wasIndoors = false;
        private long indoorCheckTicks = 0;

        // Swimming Depth Detection
        private bool wasSwimming = false;
        private long swimCheckTicks = 0;

        // Door/Ladder Detection Audio
        private WaveOutEvent outDoor;
        private WaveOutEvent outLadder;
        private AudioFileReader doorSound;
        private AudioFileReader ladderSound;
        private long doorLadderCheckTicks = 0;
        private Entity lastAnnouncedDoor = null;
        private Entity lastAnnouncedLadder = null;

        // Safe House Proximity
        private long safeHouseCheckTicks = 0;
        private bool nearSafeHouseAnnounced = false;

        // Service Proximity (Ammu-Nation, Hospital, etc.)
        private long serviceCheckTicks = 0;
        private int lastAnnouncedServiceBlip = -1;
        private GTA.Math.Vector3 lastAnnouncedServicePos = GTA.Math.Vector3.Zero;

        // Detection Radius Setting (0=10m, 1=25m, 2=50m, 3=100m, 4=125m, 5=150m, 6=200m, 7=250m, 8=300m, 9=400m, 10=500m, 11=750m, 12=1000m)
        private int detectionRadiusIndex = 1; // Default 25m
        private float[] detectionRadiusOptions = { 10f, 25f, 50f, 100f, 125f, 150f, 200f, 250f, 300f, 400f, 500f, 750f, 1000f };

        // ============================================
        // AIM AUTOLOCK SYSTEM
        // ============================================
        private Entity autolockTarget = null;
        private bool autolockActive = false;
        private int autolockPartIndex = 0;
        private long autolockPartCycleTicks = 0;
        private bool autolockPartCycleLeft = false;
        private bool autolockPartCycleRight = false;
        private Entity lastAnnouncedTarget = null;
        private long autolockReleaseTicks = 0;        // When LT was released
        private const long AUTOLOCK_GRACE_PERIOD = 3000000; // 300ms in ticks (100ns units)

        // Audio for part cycling
        private WaveOutEvent outPartCycle;
        private SignalGenerator partCycleBeep;

        // Ped body parts (bone ID, display name)
        private static readonly (int boneId, string name)[] PED_TARGET_PARTS = {
            (31086, "Head"),           // SKEL_Head
            (24818, "Torso"),          // SKEL_Spine3
            (61163, "Left Arm"),       // SKEL_L_UpperArm
            (40269, "Right Arm"),      // SKEL_R_UpperArm
            (58271, "Left Leg"),       // SKEL_L_Thigh
            (51826, "Right Leg")       // SKEL_R_Thigh
        };

        // Vehicle parts (bone name, display name)
        private static readonly (string bone, string name)[] VEHICLE_TARGET_PARTS = {
            ("engine", "Engine"),
            ("petrolcap", "Gas Tank"),
            ("wheel_lf", "Front Left Wheel"),
            ("wheel_rf", "Front Right Wheel"),
            ("wheel_lr", "Rear Left Wheel"),
            ("wheel_rr", "Rear Right Wheel")
        };

        // Double-tap detection for NumPad Decimal
        private long lastDecimalPressTicks = 0;

        // Blip Cycling System (NumPad Plus)
        private List<int> availableBlipHandles = new List<int>();
        private List<string> availableBlipNames = new List<string>();
        private List<int> availableBlipTypes = new List<int>();
        private int blipCycleIndex = -1;
        private long lastBlipScanTicks = 0;

        // Auto-Drive System - Flag-based with 32 toggleable flags
        private bool isAutodriving = false;
        private bool autodriveWanderMode = false; // true = wander, false = waypoint
        private GTA.Math.Vector3 autodriveDestination = GTA.Math.Vector3.Zero;
        private long autodriveCheckTicks = 0;
        private float autodriveStartDistance = 0f;
        private float autodriveSpeed = 20.1168f; // Speed in m/s (adjustable with arrow keys, 45 mph)
        private int autodriveFlagMenuIndex = 0; // Which flag is selected in menu
        private bool[] autodriveFlags = new bool[32]; // Individual flag states

        // ============================================
        // BODYGUARD / AI COMPANION SYSTEM
        // ============================================

        // Core state
        private bool bodyguardSystemEnabled = false;
        private List<Ped> bodyguards = new List<Ped>(); // Index 0 = primary guard (Butler)
        private int bodyguardGroupId = -1;              // GTA V ped group ID

        // Menu state
        private int bodyguardMenuIndex = 0;
        private List<string> bodyguardMenu = new List<string>();

        // Guard configuration
        private int guardModelIndex = 0;       // Index into GUARD_MODELS
        private int guardWeaponIndex = 0;      // Index into GUARD_WEAPONS
        private int guardCombatStyleIndex = 1; // 0=Aggressive, 1=Balanced, 2=Defensive
        private int guardFormationIndex = 0;   // Index into FORMATION_TYPES
        private float guardFormationSpacing = 2.0f;
        private int guardArmorIndex = 0;       // Index into ARMOR_LEVEL_NAMES
        private bool guardGodMode = false;
        private bool guardAutoRespawn = false;
        private string guardTaskMode = "follow"; // "follow", "hold", "waypoint", "attack"
        private bool guardAutoPatrol = true;

        // Guard health and status tracking
        private bool[,] guardHealthWarnings = new bool[7, 3]; // 7 guards x 3 thresholds (75%, 50%, 25%)
        private bool[] guardDeathAnnounced = new bool[7];
        private long[] guardRespawnTicks = new long[7];
        private long guardStatusCheckTicks = 0;
        private long guardPersistenceCheckTicks = 0;
        private long guardPeriodicStatusTicks = 0;
        private long guardCustomFormationTicks = 0;

        // Driver system
        private bool guardDriverActive = false;
        private bool wasInVehicleForGuard = false;

        // Butler natural vehicle entry (walking to car)
        private bool butlerWalkingToVehicle = false;
        private Vehicle butlerTargetVehicle = null;
        private long butlerWalkStartTicks = 0;

        // Convoy system
        private List<Vehicle> convoyVehicles = new List<Vehicle>();
        private bool convoyActive = false;
        private bool playerInWater = false;

        // Helicopter ground convoy (guards drive to waypoint while player flies)
        private bool heliGroundConvoyActive = false;
        private GTA.Math.Vector3 heliGroundConvoyTarget = GTA.Math.Vector3.Zero;
        private long heliGroundConvoyCheckTicks = 0;

        // Patrol system
        private long playerStationaryTicks = 0;
        private GTA.Math.Vector3 playerStationaryPos = GTA.Math.Vector3.Zero;
        private bool guardsPatrolling = false;

        // Callouts / Beacon / POI
        private bool guardCalloutsEnabled = true;
        private long lastCalloutTicks = 0;
        private bool butlerBeaconEnabled = false;
        private long lastBeaconTicks = 0;
        private bool butlerPOINarrationEnabled = true;
        private long lastPOITicks = 0;
        private HashSet<int> announcedPOIBlips = new HashSet<int>();

        // Extraction
        private bool extractionInProgress = false;

        // Per-guard weapons
        private Dictionary<string, WeaponHash> guardWeaponConfig = new Dictionary<string, WeaponHash>();
        private Dictionary<string, WeaponHash> WEAPON_NAME_MAP = new Dictionary<string, WeaponHash>();

        // Auto-engagement tracking
        private Dictionary<int, Ped> guardCurrentTarget = new Dictionary<int, Ped>();
        private bool butlerEvading = false;
        private long butlerEvadeCheckTicks = 0;
        private long threatsClearTicks = 0;

        // Proactive threat detection
        private bool proactiveThreatDetection = true;
        private bool armedPedAlert = true;
        private List<Ped> watchedPeds = new List<Ped>();
        private int lastAnnouncedEnemyCount = 0;
        private long proactiveScanTicks = 0;

        // Combat tactics
        private long combatTacticsTicks = 0;
        private Dictionary<int, int> guardAssignedProfile = new Dictionary<int, int>(); // guard handle -> profile index
        private Dictionary<int, int> guardToEnemyHandle = new Dictionary<int, int>(); // guard handle -> enemy handle
        private Dictionary<int, int> enemyAssignmentCount = new Dictionary<int, int>(); // enemy handle -> # guards assigned
        private Dictionary<int, GuardCombatState> guardCombatState = new Dictionary<int, GuardCombatState>();
        private Dictionary<int, long> guardLastRepositionTicks = new Dictionary<int, long>();

        // Static data arrays
        private static readonly (string name, PedHash hash)[] GUARD_MODELS = {
            ("Agent (Black Suit)", PedHash.FbiSuit01),
            ("Security Guard", PedHash.Security01SMM),
            ("Marine", PedHash.Marine01SMM),
            ("SWAT Operative", PedHash.Swat01SMY),
            ("Bouncer", PedHash.Bouncer01SMM),
            ("Merryweather Merc", PedHash.ArmGoon01GMM),
            ("Businessman", PedHash.Business01AMM),
            ("Businesswoman", PedHash.Business01AFY),
            ("Biker", PedHash.Lost01GMY),
            ("Redneck", PedHash.Hillbilly01AMM),
            ("Scientist", PedHash.Scientist01SMM),
            ("Paramedic", PedHash.Paramedic01SMM),
        };

        // Vehicle models matched to guard style for helicopter ground convoy
        private static readonly VehicleHash[] GUARD_CONVOY_VEHICLES = {
            VehicleHash.FBI,         // Agent (Black Suit) → FBI SUV
            VehicleHash.Granger,     // Security Guard → Granger SUV
            VehicleHash.Crusader,    // Marine → Crusader (military jeep)
            VehicleHash.Riot,        // SWAT Operative → Riot van
            VehicleHash.Granger,     // Bouncer → Granger SUV
            VehicleHash.Mesa3,       // Merryweather Merc → Mesa (Merryweather)
            VehicleHash.Oracle,      // Businessman → Oracle sedan
            VehicleHash.Oracle,      // Businesswoman → Oracle sedan
            VehicleHash.Baller,      // Biker → Baller SUV (bikes can't carry passengers)
            VehicleHash.BobcatXL,    // Redneck → Bobcat XL pickup
            VehicleHash.Dilettante,  // Scientist → Dilettante (civilian)
            VehicleHash.Ambulance,   // Paramedic → Ambulance
        };

        private static readonly float HELI_GROUND_CONVOY_SPEED = 22.352f; // 50 mph in m/s
        private static readonly int HELI_GROUND_CONVOY_STYLE = 786603;    // balanced/conservative

        private static readonly (string name, WeaponHash hash)[] GUARD_WEAPONS = {
            ("Pistol", WeaponHash.Pistol),
            ("AP Pistol", WeaponHash.APPistol),
            ("Combat Pistol", WeaponHash.CombatPistol),
            ("Heavy Pistol", WeaponHash.HeavyPistol),
            ("Micro SMG", WeaponHash.MicroSMG),
            ("SMG", WeaponHash.SMG),
            ("Combat PDW", WeaponHash.CombatPDW),
            ("Assault Rifle", WeaponHash.AssaultRifle),
            ("Carbine Rifle", WeaponHash.CarbineRifle),
            ("Special Carbine", WeaponHash.SpecialCarbine),
            ("Advanced Rifle", WeaponHash.AdvancedRifle),
            ("Pump Shotgun", WeaponHash.PumpShotgun),
            ("Assault Shotgun", WeaponHash.AssaultShotgun),
            ("RPG", WeaponHash.RPG),
            ("Minigun", WeaponHash.Minigun),
            ("Grenade Launcher", WeaponHash.GrenadeLauncher),
            ("Sniper Rifle", WeaponHash.SniperRifle),
            ("Heavy Sniper", WeaponHash.HeavySniper),
            ("Knife", WeaponHash.Knife),
            ("Baseball Bat", WeaponHash.Bat),
        };

        private static readonly (string name, int id)[] FORMATION_TYPES = {
            ("Default", 0),
            ("Circle around leader", 1),
            ("Alternate pairs", 2),
            ("Line abreast", 3),
            ("V-Wedge", -1),
            ("Diamond", -2),
            ("Front/Back Escort", -3),
        };

        private static readonly string[] COMBAT_STYLE_NAMES = { "Aggressive", "Balanced", "Defensive", "Sniper Overwatch", "Close Protection", "Flanker" };
        private static readonly string[] ARMOR_LEVEL_NAMES = { "None", "Light", "Medium", "Heavy" };
        private static readonly int[] ARMOR_LEVEL_VALUES = { 0, 50, 100, 200 };

        private static readonly float[] FORMATION_SPACING_OPTIONS = { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 3.5f, 4.0f, 4.5f, 5.0f };
        private int guardFormationSpacingIndex = 4; // default 3.0

        // Extraction spawn distance options (0 = instant spawn and warp)
        private static readonly string[] GROUND_EXTRACTION_DISTANCE_NAMES = { "Instant Warp", "10 M", "25 M", "50 M", "100 M", "200 M", "300 M", "500 M", "750 M", "1500 M" };
        private static readonly float[] GROUND_EXTRACTION_DISTANCES = { 0f, 10f, 25f, 50f, 100f, 200f, 300f, 500f, 750f, 1500f };
        private int groundExtractionDistanceIndex = 4; // Default 100 M
        private static readonly string[] HELI_EXTRACTION_DISTANCE_NAMES = { "Instant Warp", "50 M", "100 M", "200 M", "300 M", "500 M", "1000 M", "1500 M", "3000 M" };
        private static readonly float[] HELI_EXTRACTION_DISTANCES = { 0f, 50f, 100f, 200f, 300f, 500f, 1000f, 1500f, 3000f };
        private int heliExtractionDistanceIndex = 5; // Default 500 M
        private bool heliLandingPhase = false;
        private GTA.Math.Vector3 heliLandingTarget = GTA.Math.Vector3.Zero;
        private const float HELI_LANDING_THRESHOLD = 200f;  // Horizontal distance (m) to enter landing phase
        private long heliTaskReissueTicks = 0;               // Throttle heli task re-issue to avoid per-tick clearing
        private float heliLandingSearchRadius = 20f;            // Current search radius for iterative landing
        private int heliLandingSearchPointIndex = 0;            // Current point index within search ring
        private bool heliLandingTargetActive = false;           // Whether heli is actively attempting a landing candidate
        private long heliLandingTargetTicks = 0;                // When the current landing candidate was issued
        private float heliLandingStartAltitude = 0f;            // Heli altitude when landing candidate was issued
        private bool heliManualLanding = false;                    // Manual "Land" command vs extraction landing
        private bool postExtractionAutoEngage = false;             // Auto-engage wander after player enters post-extraction
        private bool postExtractionIsHeli = false;                 // Whether completed extraction was helicopter
        private bool parkingInProgress = false;                    // Butler is driving to a parking spot
        private GTA.Math.Vector3 parkingDestination = GTA.Math.Vector3.Zero;
        private long waypointMonitorTicks = 0;                     // Throttle waypoint monitoring during wander

        // Auto-navigation mode tracking: "drive", "fly", "walk"
        private string autonavMode = "drive";
        // Aircraft autopilot cruise altitude (meters above ground)
        private float autopilotAltitude = 200f;

        // Plane autopilot phase machine — drives takeoff → cruise → landing transitions.
        private enum PlanePhase { Takeoff, Cruise, Landing }
        private PlanePhase planePhase = PlanePhase.Cruise;
        // Index into RUNWAYS for the chosen landing destination, or -1 if no landing.
        private int planeLandingRunwayIndex = -1;

        // Tracked vehicle/pilot for the Request Plane Flight feature — cleaned up after
        // the player walks 200m from the landed plane.
        private Vehicle requestedFlightVehicle = null;
        private Ped requestedFlightPilot = null; // Null if Butler is the pilot.
        // Pilot toggle for Request Plane Flight menu — false = player drives, true = AI drives.
        private bool requestedFlightAIPilot = true;
        // Current selection index in the Request Plane Flight menu.
        private int planeFlightMenuIndex = 0;
        // Populated at startup with all (origin, destination) airport pairs where i != j.
        // Each entry is { originIndex, destIndex } into RUNWAYS. Length = 12.
        private List<int[]> flightRoutes = new List<int[]>();

        // GTA V runways. spawn / runwayStart / runwayEnd are taken from community
        // modding sources (FiveM-Localizer centerlines, XNLRealPlanes glide-slope
        // waypoints) and the in-repo AIRPORT FIELD location. In-game tune-up may
        // still be needed for the spawn heading; the spawn-site water guard in
        // RequestPlaneFlight will abort cleanly if a value is ever off the tarmac.
        private struct RunwayEntry
        {
            public string name;
            public GTA.Math.Vector3 spawn;       // Where a requested plane spawns (on the runway, ready to roll)
            public float spawnHeading;            // Heading aligned with the runway centerline
            public GTA.Math.Vector3 runwayStart;  // TASK_PLANE_LAND runway-start threshold
            public GTA.Math.Vector3 runwayEnd;    // TASK_PLANE_LAND runway-end threshold
            public RunwayEntry(string n, GTA.Math.Vector3 s, float sh,
                               GTA.Math.Vector3 rs, GTA.Math.Vector3 re)
            { name = n; spawn = s; spawnHeading = sh; runwayStart = rs; runwayEnd = re; }
        }
        private static readonly RunwayEntry[] RUNWAYS = {
            // LSIA — small private runway 03/21. Spawn at the verified-on-tarmac
            // AIRPORT FIELD point, facing NE (runway 03) so the longer half of the
            // strip is ahead. Runway endpoints are 03/21 threshold approximations.
            new RunwayEntry("LSIA",
                new GTA.Math.Vector3(-1336.0f, -3044.0f, 13.9f), 30f,
                new GTA.Math.Vector3(-1509.3f, -2510.3f, 13.0f),
                new GTA.Math.Vector3(-1730.0f, -2960.0f, 13.0f)),
            new RunwayEntry("Sandy Shores",
                new GTA.Math.Vector3(1747.0f, 3273.7f, 41.1f), 124f,
                new GTA.Math.Vector3(1319.3f, 3147.0f, 41.0f),
                new GTA.Math.Vector3(1748.0f, 3273.0f, 41.0f)),
            new RunwayEntry("McKenzie",
                new GTA.Math.Vector3(2121.7f, 4796.3f, 41.1f), 122f,
                new GTA.Math.Vector3(1980.0f, 4895.0f, 41.0f),
                new GTA.Math.Vector3(2230.0f, 4710.0f, 41.0f)),
            new RunwayEntry("Fort Zancudo",
                new GTA.Math.Vector3(-2047.4f, 3132.1f, 32.8f), 32f,
                new GTA.Math.Vector3(-2414.7f, 3093.5f, 32.0f),
                new GTA.Math.Vector3(-1646.0f, 3247.0f, 32.0f)),
        };

        // Driving style flag names (bit 0-31)
        private string[] autodriveFlagNames = {
            "Stop before vehicles",           // 0 - 1
			"Stop before peds",                // 1 - 2
			"Avoid vehicles",                  // 2 - 4
			"Avoid empty vehicles",            // 3 - 8
			"Avoid peds",                      // 4 - 16
			"Avoid objects",                   // 5 - 32
			"Unknown (bit 6)",                 // 6 - 64
			"Stop at traffic lights",         // 7 - 128
			"Use blinkers",                    // 8 - 256
			"Allow going wrong way",          // 9 - 512
			"Drive in reverse gear",          // 10 - 1024
			"Unknown (bit 11)",                // 11 - 2048
			"Unknown (bit 12)",                // 12 - 4096
			"Unknown (bit 13)",                // 13 - 8192
			"Unknown (bit 14)",                // 14 - 16384
			"Unknown (bit 15)",                // 15 - 32768
			"Unknown (bit 16)",                // 16 - 65536
			"Unknown (bit 17)",                // 17 - 131072
			"Take shortest path",              // 18 - 262144
			"Reckless / Allow overtaking",    // 19 - 524288
			"Unknown (bit 20)",                // 20 - 1048576
			"Unknown (bit 21)",                // 21 - 2097152
			"Ignore roads (local pathing)",   // 22 - 4194304
			"Unknown (bit 23)",                // 23 - 8388608
			"Ignore all pathing (straight)",  // 24 - 16777216
			"Unknown (bit 25)",                // 25 - 33554432
			"Unknown (bit 26)",                // 26 - 67108864
			"Unknown (bit 27)",                // 27 - 134217728
			"Unknown (bit 28)",                // 28 - 268435456
			"Avoid highways when possible",   // 29 - 536870912
			"Unknown (bit 30)",                // 30 - 1073741824
			"Unknown (bit 31)"                 // 31 - 2147483648 (sign bit)
		};

        // Helper function to calculate driving style integer from flags
        private int GetDrivingStyleFromFlags()
        {
            int result = 0;
            for (int i = 0; i < 31; i++) // Only bits 0-30 (bit 31 would make it negative)
            {
                if (autodriveFlags[i])
                    result |= (1 << i);
            }
            return result;
        }

        // Updates the autodrive task with new speed/flags while driving
        // Returns a unit vector pointing "behind" the player. If the player is in a moving
        // vehicle (>2 m/s), uses the negated velocity direction so the result is behind the
        // direction of travel (not the direction the car is facing). Otherwise falls back to
        // the negated facing vector. Used to keep convoy spawns and guard warps out of the
        // player's path of travel.
        private GTA.Math.Vector3 GetPlayerBackUnit()
        {
            Ped p = Game.Player.Character;
            if (p.IsInVehicle())
            {
                Vehicle pv = p.CurrentVehicle;
                if (pv != null && pv.Exists())
                {
                    GTA.Math.Vector3 vel = pv.Velocity;
                    float speed = vel.Length();
                    if (speed > 2f)
                        return -vel / speed;
                    return -pv.ForwardVector;
                }
            }
            return -p.ForwardVector;
        }

        // Speaks "Now on {road}" / "Entering {district}" when the player crosses
        // a classified road polygon or zone boundary while autodriving. Cheap
        // (MapDb caches per-position), so safe to call every tick — only Speaks
        // on actual transition. Pass the same Vector3 you use for autodrive
        // position checks.
        private void MaybeAnnounceLocationChange(GTA.Math.Vector3 pos)
        {
            string roadName = MapDb.GetRoadNameAt(pos);
            if (!string.IsNullOrEmpty(roadName) && roadName != lastAnnouncedRoadName)
            {
                if (driveLogger != null && driveLogger.IsRunning)
                    driveLogger.Write("[F" + driveLogFrameCount + "] EVENT location-change: road \""
                        + lastAnnouncedRoadName + "\" -> \"" + roadName + "\" at " + FmtV(pos));
                lastAnnouncedRoadName = roadName;
                Tolk.Speak("Now on " + roadName + ".");
            }

            string district = World.GetZoneLocalizedName(pos);
            if (!string.IsNullOrEmpty(district) && district != lastAnnouncedDistrict)
            {
                if (driveLogger != null && driveLogger.IsRunning)
                    driveLogger.Write("[F" + driveLogFrameCount + "] EVENT location-change: district \""
                        + lastAnnouncedDistrict + "\" -> \"" + district + "\" at " + FmtV(pos));
                // Skip the very first call (lastAnnouncedDistrict still empty)
                // so we don't blurt the starting zone the instant autodrive
                // engages — wait for the first genuine transition.
                if (!string.IsNullOrEmpty(lastAnnouncedDistrict))
                    Tolk.Speak("Entering " + district + ".");
                lastAnnouncedDistrict = district;
            }
        }

        // Picks a position behind the player suitable for a convoy/extraction
        // vehicle spawn. Steps backwards in increments until MapDb returns
        // anything OTHER than "alley" — alleys are the only classification we
        // want to skip explicitly. "Unknown" is fine (just means no data for
        // that area); the previous spawn behaviour treated everywhere as
        // unknown, so this is purely additive over the legacy logic.
        private GTA.Math.Vector3 FindGoodRearSpawn(GTA.Math.Vector3 playerPos, float initialDist)
        {
            GTA.Math.Vector3 backUnit = GetPlayerBackUnit();
            float[] tryDistances = { initialDist, initialDist + 25f, initialDist + 50f };
            GTA.Math.Vector3 lastTried = playerPos + backUnit * initialDist;
            foreach (float d in tryDistances)
            {
                GTA.Math.Vector3 candidate = playerPos + backUnit * d;
                lastTried = candidate;
                if (MapDb.GetRoadTypeAt(candidate) != "alley")
                    return candidate;
            }
            return lastTried;
        }

        private void UpdateAutodriveSpeed()
        {
            if (!isAutodriving) return;

            // Aircraft autopilot: reissue the appropriate mission instead of a ground task,
            // which would otherwise brick the autopilot mid-flight.
            if (autonavMode == "fly")
            {
                ReissueFlightMission();
                return;
            }

            Vehicle veh = Game.Player.Character.CurrentVehicle;
            if (veh == null) return;

            Ped driver = (guardDriverActive && bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive) ? bodyguards[0] : Game.Player.Character;
            int drivingStyle = GetDrivingStyleFromFlags();

            if (autodriveWanderMode)
            {
                Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER,
                    driver, veh,
                    autodriveSpeed, drivingStyle);
            }
            else
            {
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    driver, veh,
                    autodriveDestination.X, autodriveDestination.Y, autodriveDestination.Z,
                    autodriveSpeed, drivingStyle, 20f);
            }
        }

        // Mode-aware user-triggered refresh. Re-issues whatever task fits the current
        // autonavMode and (for fly) planePhase. Works for player-driver or butler-driver.
        private void RefreshAutodriveTask()
        {
            if (!isAutodriving)
            {
                Tolk.Speak("No active driving task to refresh.");
                return;
            }

            if (driveLogger != null && driveLogger.IsRunning)
                driveLogger.Write("[F" + driveLogFrameCount + "] EVENT autodrive-task-refresh:"
                    + " mode=" + autonavMode + " wander=" + autodriveWanderMode
                    + " dest=" + FmtV(autodriveDestination)
                    + " speed=" + autodriveSpeed.ToString("F2"));

            if (autonavMode == "fly")
            {
                ReissueFlightMission();
                Tolk.Speak("Flight task refreshed.");
                return;
            }

            if (autonavMode == "walk")
            {
                Ped p = Game.Player.Character;
                if (autodriveWanderMode)
                {
                    Function.Call(Hash.TASK_WANDER_STANDARD, p, 10f, 0);
                }
                else
                {
                    float walkSpeed = Math.Min(autodriveSpeed, 4f);
                    Function.Call(Hash.TASK_GO_TO_COORD_ANY_MEANS,
                        p,
                        autodriveDestination.X, autodriveDestination.Y, autodriveDestination.Z,
                        walkSpeed, 0, 0, 0, 0f);
                }
                Tolk.Speak("Walk task refreshed.");
                return;
            }

            // Ground vehicle: same path as UpdateAutodriveSpeed.
            UpdateAutodriveSpeed();
            Tolk.Speak("Driving task refreshed.");
        }

        // Returns the index of the runway in RUNWAYS closest to the given position, or -1.
        private int FindNearestRunwayIndex(GTA.Math.Vector3 pos)
        {
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < RUNWAYS.Length; i++)
            {
                GTA.Math.Vector3 mid = (RUNWAYS[i].runwayStart + RUNWAYS[i].runwayEnd) * 0.5f;
                float d = World.GetDistance(pos, mid);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        // Re-issues the active flight mission. Used by UpdateAutodriveSpeed and
        // RefreshAutodriveTask in fly mode, and by the plane phase machine on transitions.
        // Picks butler as pilot if active, else player. Branches on plane/heli and on
        // planePhase (Takeoff/Cruise/Landing).
        private void ReissueFlightMission()
        {
            Vehicle veh = Game.Player.Character.CurrentVehicle;
            if (veh == null || !veh.Exists()) return;
            int vehClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, veh);
            if (vehClass != 15 && vehClass != 16) return;
            bool isHeli = (vehClass == 15);

            Ped pilot = (guardDriverActive && bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive)
                ? bodyguards[0]
                : veh.GetPedOnSeat(VehicleSeat.Driver);
            if (pilot == null || !pilot.Exists()) pilot = Game.Player.Character;

            GTA.Math.Vector3 tgt = autodriveDestination;

            if (isHeli)
            {
                Function.Call(Hash.TASK_HELI_MISSION,
                    pilot, veh, 0, 0,
                    tgt.X, tgt.Y, tgt.Z,
                    4, autodriveSpeed, 20f, -1f,
                    (int)(tgt.Z + 100), (int)(tgt.Z - 50),
                    -1f, 0);
                return;
            }

            // Plane Landing phase: use the dedicated TASK_PLANE_LAND native with the
            // runway's start/end thresholds. (TASK_PLANE_MISSION has no land-plane
            // mode — mission type 8 is CTaskVehicleFleeAirborne, not land.)
            if (planePhase == PlanePhase.Landing && planeLandingRunwayIndex >= 0
                && planeLandingRunwayIndex < RUNWAYS.Length)
            {
                RunwayEntry rw = RUNWAYS[planeLandingRunwayIndex];
                Function.Call(Hash.TASK_PLANE_LAND, pilot, veh,
                    rw.runwayStart.X, rw.runwayStart.Y, rw.runwayStart.Z,
                    rw.runwayEnd.X,   rw.runwayEnd.Y,   rw.runwayEnd.Z);
                return;
            }

            // Plane Takeoff/Cruise: phase-aware GoTo target.
            GTA.Math.Vector3 planeTgt = tgt;
            if (planePhase == PlanePhase.Takeoff)
            {
                // Aim for an intermediate point 1500m ahead at autopilotAltitude so the
                // plane gains altitude before tracking the real waypoint.
                GTA.Math.Vector3 fwd = veh.ForwardVector;
                planeTgt = new GTA.Math.Vector3(
                    veh.Position.X + fwd.X * 1500f,
                    veh.Position.Y + fwd.Y * 1500f,
                    autopilotAltitude);
            }

            Function.Call(Hash.TASK_PLANE_MISSION,
                pilot, veh, 0, 0,
                planeTgt.X, planeTgt.Y, planeTgt.Z,
                4,                 // CTaskVehicleGoToPlane (GoTo)
                autodriveSpeed,
                20f,
                -1f,
                (int)(planeTgt.Z + 100),
                (int)(planeTgt.Z - 50),
                true);
        }

        // Builds the 12 directed routes between all 4 airports. Called once at startup.
        private void InitializeFlightRoutes()
        {
            flightRoutes.Clear();
            for (int i = 0; i < RUNWAYS.Length; i++)
            {
                for (int j = 0; j < RUNWAYS.Length; j++)
                {
                    if (i == j) continue;
                    flightRoutes.Add(new int[] { i, j });
                }
            }
        }

        // Label for a menu index: 0 = pilot toggle, 1..N = "From X to Y."
        private string GetFlightMenuText(int index)
        {
            if (index == 0)
                return "Pilot: " + (requestedFlightAIPilot ? "AI" : "Player");
            int routeIdx = index - 1;
            if (routeIdx < 0 || routeIdx >= flightRoutes.Count) return "";
            int[] r = flightRoutes[routeIdx];
            return "From " + RUNWAYS[r[0]].name + " to " + RUNWAYS[r[1]].name + ".";
        }

        private int GetFlightMenuCount() { return 1 + flightRoutes.Count; }

        // Initiates a flight: cleans up any prior requested flight, spawns a Velum at the
        // origin runway, warps player + pilot in, and engages the plane phase machine.
        private void RequestPlaneFlight(int routeIndex)
        {
            if (routeIndex < 0 || routeIndex >= flightRoutes.Count) return;
            int originIdx = flightRoutes[routeIndex][0];
            int destIdx = flightRoutes[routeIndex][1];
            RunwayEntry origin = RUNWAYS[originIdx];
            RunwayEntry dest = RUNWAYS[destIdx];

            // Clean up any previous requested flight before spawning a new one.
            CleanupRequestedFlight(true);

            // Eject the player from any current vehicle (synchronously).
            if (Game.Player.Character.IsInVehicle())
            {
                Game.Player.Character.Task.ClearAllImmediately();
                Function.Call(Hash.TASK_LEAVE_ANY_VEHICLE, Game.Player.Character, 0, 0);
            }

            // Spawn the Velum at the origin runway.
            Model planeModel = new Model(VehicleHash.Velum);
            planeModel.Request(5000);
            if (!planeModel.IsLoaded)
            {
                Tolk.Speak("Could not load plane model.");
                return;
            }

            // Pre-flight water guard: if the runway X/Y is over water (ground
            // below water surface, or no ground hit), the coords are wrong —
            // abort cleanly instead of dropping the plane in the sea.
            OutputArgument waterZArg = new OutputArgument();
            bool overWater = Function.Call<bool>(Hash.GET_WATER_HEIGHT,
                origin.spawn.X, origin.spawn.Y, origin.spawn.Z + 200f, waterZArg);
            OutputArgument groundZArg = new OutputArgument();
            bool gotGround = Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD,
                origin.spawn.X, origin.spawn.Y, origin.spawn.Z + 200f, groundZArg, false);
            if (overWater && (!gotGround || groundZArg.GetResult<float>() < waterZArg.GetResult<float>()))
            {
                Tolk.Speak("Runway coordinates are over water. Flight aborted.");
                planeModel.MarkAsNoLongerNeeded();
                return;
            }

            // Spawn slightly below the hardcoded runway Z and snap to ground so
            // the plane settles on tarmac instead of falling from the air.
            GTA.Math.Vector3 spawnPos = new GTA.Math.Vector3(
                origin.spawn.X, origin.spawn.Y, origin.spawn.Z - 1.0f);
            Vehicle plane = World.CreateVehicle(planeModel, spawnPos, origin.spawnHeading);
            planeModel.MarkAsNoLongerNeeded();
            if (plane == null)
            {
                Tolk.Speak("Could not spawn plane.");
                return;
            }
            Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, plane);
            plane.IsPersistent = true;
            plane.IsEngineRunning = true;
            requestedFlightVehicle = plane;

            // Seat the pilot.
            Ped pilot = null;
            if (requestedFlightAIPilot)
            {
                if (bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].Exists() && bodyguards[0].IsAlive)
                {
                    // Use Butler as pilot — do not store in requestedFlightPilot so cleanup
                    // leaves Butler alive (he's only dismissed via bodyguard menu).
                    pilot = bodyguards[0];
                    Function.Call(Hash.SET_PED_INTO_VEHICLE, pilot, plane, -1);
                    guardDriverActive = true;
                }
                else
                {
                    Model pilotModel = new Model(PedHash.Pilot01SMM);
                    pilotModel.Request(5000);
                    if (pilotModel.IsLoaded)
                    {
                        pilot = World.CreatePed(pilotModel, origin.spawn);
                        pilotModel.MarkAsNoLongerNeeded();
                        if (pilot != null)
                        {
                            pilot.IsPersistent = true;
                            pilot.BlockPermanentEvents = true;
                            Function.Call(Hash.SET_PED_INTO_VEHICLE, pilot, plane, -1);
                            requestedFlightPilot = pilot;
                        }
                    }
                }
                // Player rides in passenger seat.
                VehicleSeat pSeat = VehicleSeat.Passenger;
                if (!Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, plane, (int)pSeat))
                    pSeat = VehicleSeat.LeftRear;
                Game.Player.Character.SetIntoVehicle(plane, pSeat);
            }
            else
            {
                Game.Player.Character.SetIntoVehicle(plane, VehicleSeat.Driver);
                pilot = Game.Player.Character;
            }

            if (pilot == null)
            {
                Tolk.Speak("Could not assign pilot. Flight aborted.");
                CleanupRequestedFlight(true);
                return;
            }

            // Engage the plane phase machine. autodriveDestination is the cruise
            // target — the runway midpoint of the destination airport. The phase
            // machine flips to Landing on approach and ReissueFlightMission then
            // calls TASK_PLANE_LAND with the runway endpoints directly.
            GTA.Math.Vector3 destRunwayMid = (dest.runwayStart + dest.runwayEnd) * 0.5f;
            autonavMode = "fly";
            autodriveDestination = destRunwayMid;
            planeLandingRunwayIndex = destIdx;
            planePhase = PlanePhase.Takeoff;
            isAutodriving = true;
            autodriveWanderMode = false;
            autodriveCheckTicks = DateTime.Now.Ticks;
            GTA.Math.Vector3 startPos = Game.Player.Character.Position;
            autodriveStartDistance = World.GetDistance(startPos, destRunwayMid);
            ReissueFlightMission();

            Tolk.Speak("Flight requested. From " + origin.name + " to " + dest.name
                + ". Taking off. " + (int)autodriveStartDistance + " meters.");
        }

        // Per-tick cleanup: once the player is on foot and 200m+ from the landed requested
        // plane, despawn it and the NPC pilot. Butler is never despawned here.
        private long requestedFlightCleanupTicks = 0;
        private void TickRequestedFlightCleanup()
        {
            if (requestedFlightVehicle == null) return;
            if (DateTime.Now.Ticks - requestedFlightCleanupTicks < 20000000) return; // 2 sec
            requestedFlightCleanupTicks = DateTime.Now.Ticks;

            if (!requestedFlightVehicle.Exists())
            {
                CleanupRequestedFlight(false);
                return;
            }
            // Only despawn once player is on foot and walked away. While the player is in
            // any vehicle (including the plane), keep things alive.
            if (Game.Player.Character.IsInVehicle()) return;
            float d = World.GetDistance(Game.Player.Character.Position, requestedFlightVehicle.Position);
            if (d > 200f)
                CleanupRequestedFlight(true);
        }

        // Driver-agnostic parking arrival monitor. Fires when the parked vehicle is within
        // 8 m of the chosen parking spot, or when the player exits the vehicle mid-park.
        // Resets parkingInProgress on either condition so a follow-up park request works.
        private void TickParkingMonitor()
        {
            if (!parkingInProgress) return;

            // Player left the vehicle (death, manual exit) — clear stale state.
            if (!Game.Player.Character.IsInVehicle())
            {
                parkingInProgress = false;
                return;
            }

            Vehicle veh = Game.Player.Character.CurrentVehicle;
            if (veh == null || !veh.Exists())
            {
                parkingInProgress = false;
                return;
            }

            float parkDist = World.GetDistance(veh.Position, parkingDestination);
            if (parkDist < 8f)
            {
                Ped driver = veh.GetPedOnSeat(VehicleSeat.Driver);
                if (driver != null && driver.Exists())
                    Function.Call(Hash.CLEAR_PED_TASKS, driver);
                parkingInProgress = false;
                isAutodriving = false;
                string drivenBy = (guardDriverActive && bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive)
                    ? "Butler standing by."
                    : "You have control.";
                Tolk.Speak("Parked. " + drivenBy);
            }
        }

        // Removes the requested-flight vehicle and NPC pilot. Pass force=true to delete
        // immediately even if the vehicle is still alive.
        private void CleanupRequestedFlight(bool force)
        {
            if (requestedFlightPilot != null)
            {
                if (requestedFlightPilot.Exists() && (force || !requestedFlightPilot.IsInVehicle()))
                {
                    requestedFlightPilot.MarkAsNoLongerNeeded();
                    requestedFlightPilot.Delete();
                }
                requestedFlightPilot = null;
            }
            if (requestedFlightVehicle != null)
            {
                if (requestedFlightVehicle.Exists() && force)
                {
                    requestedFlightVehicle.IsPersistent = false;
                    requestedFlightVehicle.MarkAsNoLongerNeeded();
                    requestedFlightVehicle.Delete();
                }
                requestedFlightVehicle = null;
            }
        }

        private bool[] headings = new bool[8];
        private bool climbing = false;
        private bool shifting = false;

        private const double north = 0;
        private const double northnortheast = 22.5;
        private const double northeast = 45;
        private const double eastnortheast = 67.5;
        private const double east = 90;
        private const double eastsoutheast = 112.5;
        private const double southeast = 135;
        private const double southsoutheast = 157.5;
        private const double south = 180;
        private const double southsouthwest = 202.5;
        private const double southwest = 225;
        private const double westsouthwest = 247.5;
        private const double west = 270;
        private const double westnorthwest = 292.5;
        private const double northwest = 315;
        private const double northnorthwest = 337.5;

        public GTA11Y()
        {

            this.Tick += onTick;
            this.KeyUp += onKeyUp;
            this.KeyDown += onKeyDown;
            this.Aborted += onAborted;
            Tolk.Load();
            MapDb.Load();
            NodeGraph.Load();
            driveLogger = new DriveAssistLogger();
            Tolk.Speak("Mod Ready");

            currentWeapon = Game.Player.Character.Weapons.Current.Hash.ToString();
            string[] lines = System.IO.File.ReadAllLines("scripts/hashes.txt");
            string[] result;
            foreach (string line in lines)
            {
                result = line.Split('=');
                if (!hashes.ContainsKey(result[1]))
                    hashes.Add(result[1], result[0]);
            }

            locations.Add(new Location("MICHAEL'S HOUSE", new GTA.Math.Vector3(-852.4f, 160.0f, 65.6f)));
            locations.Add(new Location("FRANKLIN'S HOUSE", new GTA.Math.Vector3(7.9f, 548.1f, 175.5f)));
            locations.Add(new Location("TREVOR'S TRAILER", new GTA.Math.Vector3(1985.7f, 3812.2f, 32.2f)));
            locations.Add(new Location("AIRPORT ENTRANCE", new GTA.Math.Vector3(-1034.6f, -2733.6f, 13.8f)));
            locations.Add(new Location("AIRPORT FIELD", new GTA.Math.Vector3(-1336.0f, -3044.0f, 13.9f)));
            locations.Add(new Location("ELYSIAN ISLAND", new GTA.Math.Vector3(338.2f, -2715.9f, 38.5f)));
            locations.Add(new Location("JETSAM", new GTA.Math.Vector3(760.4f, -2943.2f, 5.8f)));
            locations.Add(new Location("StripClub", new GTA.Math.Vector3(96.17191f, -1290.668f, 29.26874f)));
            locations.Add(new Location("ELBURRO HEIGHTS", new GTA.Math.Vector3(1384.0f, -2057.1f, 52.0f)));
            locations.Add(new Location("FERRIS WHEEL", new GTA.Math.Vector3(-1670.7f, -1125.0f, 13.0f)));
            locations.Add(new Location("CHUMASH", new GTA.Math.Vector3(-3192.6f, 1100.0f, 20.2f)));
            locations.Add(new Location("Altruist Cult Camp", new GTA.Math.Vector3(-1170.841f, 4926.646f, 224.295f)));
            locations.Add(new Location("Hippy Camp", new GTA.Math.Vector3(2476.712f, 3789.645f, 41.226f)));
            locations.Add(new Location("Far North San Andreas", new GTA.Math.Vector3(24.775f, 7644.102f, 19.055f)));
            locations.Add(new Location("Fort Zancudo", new GTA.Math.Vector3(-2047.4f, 3132.1f, 32.8f)));
            locations.Add(new Location("Fort Zancudo ATC Entrance", new GTA.Math.Vector3(-2344.373f, 3267.498f, 32.811f)));
            locations.Add(new Location("Playboy Mansion", new GTA.Math.Vector3(-1475.234f, 167.088f, 55.841f)));
            locations.Add(new Location("WINDFARM", new GTA.Math.Vector3(2354.0f, 1830.3f, 101.1f)));
            locations.Add(new Location("MCKENZIE AIRFIELD", new GTA.Math.Vector3(2121.7f, 4796.3f, 41.1f)));
            locations.Add(new Location("DESERT AIRFIELD", new GTA.Math.Vector3(1747.0f, 3273.7f, 41.1f)));
            locations.Add(new Location("CHILLIAD", new GTA.Math.Vector3(425.4f, 5614.3f, 766.5f)));
            locations.Add(new Location("Police Station", new GTA.Math.Vector3(436.491f, -982.172f, 30.699f)));
            locations.Add(new Location("Casino", new GTA.Math.Vector3(925.329f, 46.152f, 80.908f)));
            locations.Add(new Location("Vinewood sign", new GTA.Math.Vector3(711.362f, 1198.134f, 348.526f)));
            locations.Add(new Location("Blaine County Savings Bank", new GTA.Math.Vector3(-109.299f, 6464.035f, 31.627f)));
            locations.Add(new Location("LS Government Facility", new GTA.Math.Vector3(2522.98f, -384.436f, 92.9928f)));
            locations.Add(new Location("CHILIAD MOUNTAIN STATE WILDERNESS", new GTA.Math.Vector3(2994.917f, 2774.16f, 42.33663f)));
            locations.Add(new Location("Beaker's Garage", new GTA.Math.Vector3(116.3748f, 6621.362f, 31.6078f)));

            foreach (VehicleHash v in Enum.GetValues(typeof(VehicleHash)))
            {
                string i = Game.GetLocalizedString(Function.Call<string>(Hash.GET_DISPLAY_NAME_FROM_VEHICLE_MODEL, v));
                spawns.Add(new VehicleSpawn(i, v));
            }
            spawns.Sort();


            mainMenu.Add("Teleport to location. ");
            mainMenu.Add("Spawn Vehicle. ");
            mainMenu.Add("Functions. ");
            mainMenu.Add("Auto-Drive. ");
            mainMenu.Add("Settings. ");
            mainMenu.Add("Bodyguard. ");
            mainMenu.Add("Status. ");
            mainMenu.Add("Request Plane Flight. ");
            InitializeBodyguardMenu();
            InitializeFlightRoutes();
            InitializeWeaponNameMap();

            funMenu.Add("Blow up all nearby vehicles");
            funMenu.Add("Make all nearby pedestrians attack each other.");
            funMenu.Add("instantly kill all nearby pedestrians.");
            funMenu.Add("Raise Wanted Level. ");
            funMenu.Add("Clear Wanted Level. ");
            funMenu.Add("Place waypoint at current location.");

            // Auto-Drive menu items (special handling - uses flags array)
            // driveMenu is not used for items - we use autodriveFlagNames instead
            // Initialize with sensible default flags (similar to "Normal" driving: 786603)
            // 786603 = bits 0,1,2,3,4,5,7,8,9,19 = stop before vehicles/peds, avoid vehicles/empty/peds/objects, stop at lights, blinkers, wrong way, reckless
            autodriveFlags[0] = true;  // Stop before vehicles
            autodriveFlags[1] = true;  // Stop before peds
            autodriveFlags[2] = true;  // Avoid vehicles
            autodriveFlags[3] = true;  // Avoid empty vehicles
            autodriveFlags[4] = true;  // Avoid peds
            autodriveFlags[5] = true;  // Avoid objects
            autodriveFlags[7] = true;  // Stop at traffic lights
            autodriveFlags[8] = true;  // Use blinkers
            autodriveFlags[9] = true;  // Allow going wrong way
            autodriveFlags[19] = true; // Reckless / Allow overtaking
            autodriveSpeed = 20.1168f;  // Default 45 mph


            tped = new AudioFileReader(@"scripts/tped.wav");
            tvehicle = new AudioFileReader(@"scripts/tvehicle.wav");
            tprop = new AudioFileReader(@"scripts/tprop.wav");
            out1 = new WaveOutEvent();
            out2 = new WaveOutEvent();
            out3 = new WaveOutEvent();
            out11 = new WaveOutEvent();
            out12 = new WaveOutEvent();
            out1.Init(tped);
            out2.Init(tvehicle);
            out3.Init(tprop);
            alt = new SignalGenerator();
            out11.Init(alt);
            pitch = new SignalGenerator();
            out12.Init(pitch);
            out13 = new WaveOutEvent();
            navBeep = new SignalGenerator();
            out13.Init(navBeep);

            // Initialize ped and vehicle beep outputs
            out14 = new WaveOutEvent();
            pedBeep = new SignalGenerator();
            out14.Init(pedBeep);
            out15 = new WaveOutEvent();
            vehicleBeep = new SignalGenerator();
            out15.Init(vehicleBeep);

            // Initialize multi-directional nav outputs with stereo panning
            // SignalGenerator must be MONO (1 channel) for PanningSampleProvider to work
            // Left channel output
            outNavLeft = new WaveOutEvent();
            navBeepLeft = new SignalGenerator(44100, 1) { Gain = 0.08 }; // 44100 Hz, MONO
            outNavLeft.Init(navBeepLeft);

            // Center channel output
            outNavCenter = new WaveOutEvent();
            navBeepCenter = new SignalGenerator(44100, 1) { Gain = 0.08 }; // 44100 Hz, MONO
            outNavCenter.Init(navBeepCenter);

            // Right channel output
            outNavRight = new WaveOutEvent();
            navBeepRight = new SignalGenerator(44100, 1) { Gain = 0.08 }; // 44100 Hz, MONO
            outNavRight.Init(navBeepRight);

            // Behind channel output (center pan, lower frequency)
            outNavBehind = new WaveOutEvent();
            navBeepBehind = new SignalGenerator(44100, 1) { Gain = 0.08 }; // 44100 Hz, MONO
            outNavBehind.Init(navBeepBehind);

            // Waypoint guidance audio (mono for panning)
            outWaypoint = new WaveOutEvent();
            waypointBeep = new SignalGenerator(44100, 1) { Gain = 0.1 };
            outWaypoint.Init(waypointBeep);

            // Enemy tracking audio (mono for panning)
            outEnemy = new WaveOutEvent();
            enemyBeep = new SignalGenerator(44100, 1) { Gain = 0.12 };
            outEnemy.Init(enemyBeep);

            // Butler beacon audio (mono source, panned to stereo on each tick)
            beaconBeep = new SignalGenerator(44100, 1) { Gain = 0.1 };
            outBeacon = null; // Created fresh on each beacon tick to avoid mono→stereo re-Init issues

            // ============================================
            // NEW FEATURES - Audio Initialization
            // ============================================

            // Pickup detection audio (uses pickup.wav)
            try
            {
                pickupSound = new AudioFileReader(@"scripts/pickup.wav");
                outPickup = new WaveOutEvent();
                outPickup.Init(pickupSound);
            }
            catch { } // File may not exist yet

            // Water hazard detection (low bass rumble 80-100Hz)
            outWater = new WaveOutEvent();
            waterRumble = new SignalGenerator(44100, 1) { Gain = 0.15, Frequency = 90, Type = SignalGeneratorType.Sin };
            outWater.Init(waterRumble);

            // Dropoff detection (descending sine wave)
            outDropoff = new WaveOutEvent();
            dropoffTone = new SignalGenerator(44100, 1) { Gain = 0.1 };
            outDropoff.Init(dropoffTone);

            // Cover detection audio (uses cover.wav)
            try
            {
                coverSound = new AudioFileReader(@"scripts/cover.wav");
                outCover = new WaveOutEvent();
                outCover.Init(coverSound);
            }
            catch { } // File may not exist yet

            // Interactable detection audio (uses interact.wav)
            try
            {
                interactSound = new AudioFileReader(@"scripts/interact.wav");
                outInteract = new WaveOutEvent();
                outInteract.Init(interactSound);
            }
            catch { } // File may not exist yet

            // Mission blip tracking audio (similar to waypoint but different tone)
            outMissionBeep = new WaveOutEvent();
            missionBeep = new SignalGenerator(44100, 1) { Gain = 0.1 };
            outMissionBeep.Init(missionBeep);

            // ============================================
            // BATCH 2 - Audio Initialization
            // ============================================

            // Combat hit/headshot/kill sounds
            try
            {
                hitSound = new AudioFileReader(@"scripts/hit.wav");
                outHit = new WaveOutEvent();
                outHit.Init(hitSound);
            }
            catch { } // File may not exist yet

            try
            {
                headshotSound = new AudioFileReader(@"scripts/headshot.wav");
                outHeadshot = new WaveOutEvent();
                outHeadshot.Init(headshotSound);
            }
            catch { } // File may not exist yet

            try
            {
                killSound = new AudioFileReader(@"scripts/kill.wav");
                outKill = new WaveOutEvent();
                outKill.Init(killSound);
            }
            catch { } // File may not exist yet

            // Door proximity sound
            try
            {
                doorSound = new AudioFileReader(@"scripts/door.wav");
                outDoor = new WaveOutEvent();
                outDoor.Init(doorSound);
            }
            catch { } // File may not exist yet

            // Ladder proximity sound
            try
            {
                ladderSound = new AudioFileReader(@"scripts/ladder.wav");
                outLadder = new WaveOutEvent();
                outLadder.Init(ladderSound);
            }
            catch { } // File may not exist yet

            // Aim Autolock audio
            outPartCycle = new WaveOutEvent();
            partCycleBeep = new SignalGenerator(44100, 1) { Gain = 0.1, Frequency = 800, Type = SignalGeneratorType.Square };

            // Steering assist audio
            outSteerAssist = new WaveOutEvent();
            steerAssistBeep = new SignalGenerator(44100, 1) { Gain = 0.1, Frequency = 500, Type = SignalGeneratorType.Sin };

            // Pre-impact brake warning — sawtooth at middle C, gain set per-beep in PlayBrakeWarning.
            outBrakeWarn = new WaveOutEvent();
            brakeWarnTone = new SignalGenerator(44100, 1) { Gain = 0.12, Frequency = BRAKE_WARN_FREQ_HZ, Type = SignalGeneratorType.SawTooth };

            setupSettings();
        }

        private void onTick(object sender, EventArgs e)
        {
            // Bodyguard system tick (skip during loading to avoid invalid entity access)
            if (bodyguardSystemEnabled && !Game.IsLoading)
                TickBodyguardSystem();

            // Status monitor tick
            TickStatusMonitor();

            // Requested-flight vehicle/pilot cleanup once player walks away from landed plane
            if (!Game.IsLoading)
                TickRequestedFlightCleanup();

            // Parking arrival monitor — runs regardless of bodyguard system or driver identity
            if (!Game.IsLoading)
                TickParkingMonitor();

            // Calculate delta time for frame-rate independent calculations
            long currentTime = DateTime.Now.Ticks;
            if (lastTickTime > 0)
            {
                // Convert ticks to seconds (1 tick = 100 nanoseconds)
                deltaTime = (currentTime - lastTickTime) / 10000000f;
                // Clamp to reasonable range (1fps to 500fps)
                deltaTime = Math.Max(0.002f, Math.Min(1.0f, deltaTime));
            }
            lastTickTime = currentTime;

            if (!Game.IsLoading)
            {
                if (Game.Player.Character.HeightAboveGround - z > 1f || Game.Player.Character.HeightAboveGround - z < -1f)
                {
                    z = Game.Player.Character.HeightAboveGround;
                    if (getSetting("altitudeIndicator") == 1)
                    {
                        out11.Stop();
                        alt.Gain = 0.1;
                        alt.Frequency = 120 + (z * 40);
                        alt.Type = SignalGeneratorType.Triangle;
                        out11.Init(alt.Take(TimeSpan.FromSeconds(0.075)));
                        out11.Play();
                    }
                }

                if (GTA.GameplayCamera.RelativePitch - p > 1f || GTA.GameplayCamera.RelativePitch - p < -1f)
                {
                    p = GTA.GameplayCamera.RelativePitch;
                    if (getSetting("targetPitchIndicator") == 1)
                    {
                        if (GTA.GameplayCamera.IsAimCamActive)
                        {
                            out12.Stop();
                            pitch.Gain = 0.08;
                            pitch.Frequency = 600 + (p * 6);
                            pitch.Type = SignalGeneratorType.Square;
                            out12.Init(pitch.Take(TimeSpan.FromSeconds(0.025)));
                            out12.Play();
                        }
                    }

                }

                if (wantedLevel != Game.Player.WantedLevel)
                {
                    wantedLevel = Game.Player.WantedLevel;
                    if (getSetting("neverWanted") == 1)
                    {

                    }
                    else
                    {
                        Tolk.Speak("Wanted level is now " + wantedLevel);
                    }

                }

                if (getSetting("radioOff") == 1)
                {
                    if (Game.Player.Character.CurrentVehicle != null)
                    {
                        Game.Player.Character.CurrentVehicle.IsRadioEnabled = false;
                    }
                }
                else
                {
                    if (Game.Player.Character.CurrentVehicle != null)
                    {
                        Game.Player.Character.CurrentVehicle.IsRadioEnabled = true;
                    }
                }

                //cheats

                if (getSetting("godMode") == 1)
                {
                    Game.Player.IsInvincible = true;
                    Game.Player.Character.CanBeDraggedOutOfVehicle = false;
                    Game.Player.Character.CanBeKnockedOffBike = false;
                    Game.Player.Character.CanBeShotInVehicle = false;
                    Game.Player.Character.CanFlyThroughWindscreen = false;
                    Game.Player.Character.DrownsInSinkingVehicle = false;

                }
                else
                {
                    Game.Player.IsInvincible = false;
                    Game.Player.Character.CanBeDraggedOutOfVehicle = true;
                    Game.Player.Character.CanBeKnockedOffBike = true;
                    Game.Player.Character.CanBeShotInVehicle = true;
                    Game.Player.Character.CanFlyThroughWindscreen = true;
                    Game.Player.Character.DrownsInSinkingVehicle = true;

                }

                if (getSetting("vehicleGodMode") == 1)
                {
                    if (Game.Player.Character.CurrentVehicle != null && Game.Player.Character.IsInVehicle())
                    {
                        Vehicle vehicle = Game.Player.Character.CurrentVehicle;
                        vehicle.IsInvincible = true;
                        vehicle.CanWheelsBreak = false;
                        vehicle.CanTiresBurst = false;
                        vehicle.CanBeVisiblyDamaged = false;
                        vehicle.IsBulletProof = true;
                        vehicle.IsCollisionProof = true;
                        vehicle.IsExplosionProof = true;
                        vehicle.IsMeleeProof = true;
                        vehicle.IsFireProof = true;
                    }
                    if (Game.Player.Character.LastVehicle != null && !Game.Player.Character.IsInVehicle())
                    {
                        Vehicle vehicle = Game.Player.Character.LastVehicle;
                        vehicle.CanWheelsBreak = true;
                        vehicle.CanTiresBurst = true;
                        vehicle.CanBeVisiblyDamaged = true;
                        vehicle.IsBulletProof = false;
                        vehicle.IsCollisionProof = false;
                        vehicle.IsExplosionProof = false;
                        vehicle.IsMeleeProof = false;
                        vehicle.IsFireProof = false;
                        vehicle.IsInvincible = false;
                    }
                }
                else
                {
                    if (Game.Player.Character.CurrentVehicle != null)
                    {
                        Vehicle vehicle = Game.Player.Character.CurrentVehicle;
                        vehicle.IsInvincible = false;
                        vehicle.CanWheelsBreak = true;
                        vehicle.CanTiresBurst = true;
                        vehicle.CanBeVisiblyDamaged = true;
                        vehicle.IsBulletProof = false;
                        vehicle.IsCollisionProof = false;
                        vehicle.IsExplosionProof = false;
                        vehicle.IsMeleeProof = false;
                        vehicle.IsFireProof = false;
                    }
                }

                if (getSetting("amphibiousMode") == 1
                    && Game.Player.Character.CurrentVehicle != null
                    && Game.Player.Character.IsInVehicle())
                {
                    Vehicle vehicle = Game.Player.Character.CurrentVehicle;
                    float submergedLevel = Function.Call<float>(Hash.GET_ENTITY_SUBMERGED_LEVEL, vehicle);
                    // Hysteresis: engage once mostly underwater, stay engaged (even resting on
                    // the seabed) until the vehicle is clear of the water again.
                    bool underwater = amphibiousProtectionActive
                        ? submergedLevel > 0.05f
                        : submergedLevel > 0.5f;

                    if (underwater)
                    {
                        // Disable the flood-damage systems at the source instead of patching
                        // health values one frame too late.
                        Function.Call(Hash.SET_VEHICLE_ENGINE_CAN_DEGRADE, vehicle, false);
                        Function.Call(Hash.SET_DISABLE_VEHICLE_PETROL_TANK_DAMAGE, vehicle, true);
                        Function.Call(Hash.SET_DISABLE_VEHICLE_PETROL_TANK_FIRES, vehicle, true);

                        // Proof against the seabed impact ("the boom") while keeping gravity on
                        // so the car can settle and drive along the ocean floor.
                        vehicle.IsCollisionProof = true;
                        vehicle.IsExplosionProof = true;
                        vehicle.IsFireProof = true;
                        vehicle.CanWheelsBreak = false;
                        vehicle.CanTiresBurst = false;

                        // Keep the engine alive; disableAutoStart stops the submerged-vehicle
                        // logic from cutting it back off.
                        Function.Call(Hash.SET_VEHICLE_ENGINE_ON, vehicle, true, true, true);

                        // Force the radio off: an active radio triggers the underwater
                        // electrical-short effect. The radioOff setting restores it on surfacing.
                        vehicle.IsRadioEnabled = false;

                        // Backstop clamps in case a damage tick lands before the flags settle.
                        vehicle.FuelLevel = 100f;
                        vehicle.OilLevel = 5f;
                        vehicle.EngineHealth = 1000f;
                        vehicle.PetrolTankHealth = 1000f;
                        vehicle.BodyHealth = 1000f;
                        Game.Player.Character.DrownsInSinkingVehicle = false;

                        // Right the car if it has rolled onto its side or roof; the normal
                        // self-righting logic does not run for a submerged land vehicle.
                        if (Function.Call<bool>(Hash.IS_ENTITY_UPSIDEDOWN, vehicle)
                            || Math.Abs(vehicle.Rotation.X) > 60f
                            || Math.Abs(vehicle.Rotation.Y) > 60f)
                        {
                            vehicle.Rotation = new GTA.Math.Vector3(0f, 0f, vehicle.Rotation.Z);
                        }

                        amphibiousProtectionActive = true;
                    }
                    else if (amphibiousProtectionActive)
                    {
                        RestoreAmphibiousProtections(vehicle);
                    }
                }
                else if (amphibiousProtectionActive)
                {
                    // The setting was switched off, or the player left the vehicle, while
                    // underwater protections were still applied.
                    Vehicle restoreTarget = Game.Player.Character.CurrentVehicle
                        ?? Game.Player.Character.LastVehicle;
                    if (restoreTarget != null)
                        RestoreAmphibiousProtections(restoreTarget);
                    else
                        amphibiousProtectionActive = false;
                }

                if (getSetting("policeIgnore") == 1)
                {
                    Game.Player.IgnoredByPolice = true;
                }
                else
                {
                    Game.Player.IgnoredByPolice = false;
                }

                if (getSetting("neverWanted") == 1)
                {
                    Game.Player.WantedLevel = 0;
                }

                // ============================================
                // AUTO-NAVIGATION MONITORING (drive, fly, walk)
                // ============================================
                if (isAutodriving)
                {
                    if (autonavMode == "walk")
                    {
                        // AUTO-WALK MONITORING
                        // Cancel if player entered a vehicle
                        if (Game.Player.Character.IsInVehicle())
                        {
                            isAutodriving = false;
                            autodriveWanderMode = false;
                            autonavMode = "drive";
                            Tolk.Speak("Auto-walk stopped. You entered a vehicle.");
                        }
                        else if (autodriveWanderMode)
                        {
                            // Wander mode - periodic location updates
                            if (DateTime.Now.Ticks - autodriveCheckTicks > 150000000) // 15 seconds
                            {
                                autodriveCheckTicks = DateTime.Now.Ticks;
                                string currentStreet = World.GetStreetName(Game.Player.Character.Position);
                                string currentZone = World.GetZoneLocalizedName(Game.Player.Character.Position);
                                Tolk.Speak("Walking through " + currentStreet + ", " + currentZone + ".", true);
                            }
                        }
                        else
                        {
                            // Waypoint mode - check arrival
                            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                            float currentDist = World.GetDistance(playerPos, autodriveDestination);

                            if (currentDist < 10f)
                            {
                                isAutodriving = false;
                                Game.Player.Character.Task.ClearAll();
                                autonavMode = "drive";
                                Tolk.Speak("Arrived at destination.");
                            }
                            else if (DateTime.Now.Ticks - autodriveCheckTicks > 100000000) // 10 seconds
                            {
                                autodriveCheckTicks = DateTime.Now.Ticks;
                                if (currentDist < autodriveStartDistance * 0.9f)
                                {
                                    Tolk.Speak((int)currentDist + " meters remaining.", true);
                                }
                            }
                        }
                    }
                    else if (autonavMode == "fly")
                    {
                        // AIRCRAFT AUTOPILOT MONITORING
                        if (!Game.Player.Character.IsInVehicle())
                        {
                            isAutodriving = false;
                            autodriveWanderMode = false;
                            autonavMode = "drive";
                            planePhase = PlanePhase.Cruise;
                            planeLandingRunwayIndex = -1;
                            Tolk.Speak("Autopilot disengaged. You exited the aircraft.");
                        }
                        else
                        {
                            Vehicle flyVeh = Game.Player.Character.CurrentVehicle;
                            int flyVehClass = flyVeh != null
                                ? Function.Call<int>(Hash.GET_VEHICLE_CLASS, flyVeh)
                                : -1;
                            bool isPlane = (flyVehClass == 16);

                            // PLANE PHASE MACHINE (Takeoff → Cruise → Landing). Runs for planes
                            // in either wander or waypoint mode; Landing only fires if a runway
                            // was resolved (waypoint flights, not circling).
                            if (isPlane)
                            {
                                float aboveGround = Game.Player.Character.HeightAboveGround;
                                GTA.Math.Vector3 planePos = Game.Player.Character.Position;
                                float planeHorizDist = (float)Math.Sqrt(
                                    Math.Pow(planePos.X - autodriveDestination.X, 2) +
                                    Math.Pow(planePos.Y - autodriveDestination.Y, 2));

                                if (planePhase == PlanePhase.Takeoff && aboveGround >= 60f)
                                {
                                    planePhase = PlanePhase.Cruise;
                                    ReissueFlightMission();
                                    Tolk.Speak("Climbed to cruise altitude. Heading to destination.", true);
                                }
                                else if (planePhase == PlanePhase.Cruise
                                         && !autodriveWanderMode
                                         && planeLandingRunwayIndex >= 0
                                         && planeHorizDist < 1500f)
                                {
                                    planePhase = PlanePhase.Landing;
                                    ReissueFlightMission();
                                    Tolk.Speak("Beginning landing approach at "
                                        + RUNWAYS[planeLandingRunwayIndex].name + ".", true);
                                }
                                else if (planePhase == PlanePhase.Landing
                                         && aboveGround < 2f && flyVeh != null && flyVeh.Speed < 5f)
                                {
                                    string airport = (planeLandingRunwayIndex >= 0)
                                        ? RUNWAYS[planeLandingRunwayIndex].name : "destination";
                                    if (flyVeh != null && flyVeh.Exists())
                                        flyVeh.IsEngineRunning = false;
                                    isAutodriving = false;
                                    autodriveWanderMode = false;
                                    autonavMode = "drive";
                                    planePhase = PlanePhase.Cruise;
                                    planeLandingRunwayIndex = -1;
                                    Tolk.Speak("Landed at " + airport + ". Autopilot disengaged.");
                                }
                            }

                            if (!isAutodriving) { /* phase machine disengaged */ }
                            else if (autodriveWanderMode)
                            {
                                // Hovering/circling - periodic altitude and location updates
                                if (DateTime.Now.Ticks - autodriveCheckTicks > 150000000)
                                {
                                    autodriveCheckTicks = DateTime.Now.Ticks;
                                    string currentZone = World.GetZoneLocalizedName(Game.Player.Character.Position);
                                    float altAboveGround = Game.Player.Character.HeightAboveGround;
                                    float speed = Game.Player.Character.CurrentVehicle != null ?
                                        Game.Player.Character.CurrentVehicle.Speed * 2.23694f : 0f;
                                    Tolk.Speak("Flying over " + currentZone + ". " + (int)altAboveGround + " meters altitude. " + (int)speed + " mph.", true);
                                }
                            }
                            else
                            {
                                // Waypoint mode - check arrival
                                GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                                float currentDist = World.GetDistance(playerPos, autodriveDestination);
                                float horizontalDist = (float)Math.Sqrt(
                                    Math.Pow(playerPos.X - autodriveDestination.X, 2) +
                                    Math.Pow(playerPos.Y - autodriveDestination.Y, 2));

                                // For planes with a landing runway resolved, the phase machine
                                // above handles disengagement. Otherwise fall back to the 75m
                                // horizontal disengage (heli or plane circling without runway).
                                bool runwayLandingActive = isPlane && planeLandingRunwayIndex >= 0;
                                if (horizontalDist < 75f && !runwayLandingActive)
                                {
                                    isAutodriving = false;
                                    Game.Player.Character.Task.ClearAll();
                                    autonavMode = "drive";
                                    Tolk.Speak("Arrived at destination. Autopilot disengaged.");
                                }
                                else if (DateTime.Now.Ticks - autodriveCheckTicks > 100000000)
                                {
                                    autodriveCheckTicks = DateTime.Now.Ticks;
                                    if (currentDist < autodriveStartDistance * 0.9f)
                                    {
                                        float speed = Game.Player.Character.CurrentVehicle != null ?
                                            Game.Player.Character.CurrentVehicle.Speed : 0f;
                                        float altAboveGround = Game.Player.Character.HeightAboveGround;

                                        if (speed > 1f)
                                        {
                                            int etaSeconds = (int)(horizontalDist / speed);
                                            if (etaSeconds > 60)
                                            {
                                                int mins = etaSeconds / 60;
                                                Tolk.Speak((int)horizontalDist + " meters remaining. Altitude: " + (int)altAboveGround + " meters. About " + mins + " minute" + (mins > 1 ? "s" : "") + ".", true);
                                            }
                                            else if (horizontalDist > 100f)
                                            {
                                                Tolk.Speak((int)horizontalDist + " meters remaining. Altitude: " + (int)altAboveGround + " meters.", true);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // GROUND VEHICLE AUTO-DRIVE MONITORING (existing behavior)
                        if (!Game.Player.Character.IsInVehicle())
                        {
                            isAutodriving = false;
                            autodriveWanderMode = false;
                            // Reset announcement memory so the next autodrive run
                            // starts fresh and doesn't suppress the first transition.
                            lastAnnouncedRoadName = "";
                            lastAnnouncedDistrict = "";
                            Tolk.Speak("Auto-drive stopped. You exited the vehicle.");
                        }
                        else if (autodriveWanderMode)
                        {
                            // Per-tick informational announcement (fires only on
                            // road/district transitions).
                            MaybeAnnounceLocationChange(Game.Player.Character.Position);

                            // WANDER MODE - just announce current location periodically
                            if (DateTime.Now.Ticks - autodriveCheckTicks > 150000000) // 15 seconds
                            {
                                autodriveCheckTicks = DateTime.Now.Ticks;

                                // Announce current street/zone
                                string currentStreet = World.GetStreetName(Game.Player.Character.Position);
                                string currentZone = World.GetZoneLocalizedName(Game.Player.Character.Position);
                                float speed = Game.Player.Character.CurrentVehicle != null ?
                                    Game.Player.Character.CurrentVehicle.Speed * 2.23694f : 0f;

                                Tolk.Speak("Wandering through " + currentStreet + ", " + currentZone + ". " + (int)speed + " mph.", true);
                            }
                        }
                        else
                        {
                            // WAYPOINT MODE - Check distance to destination
                            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

                            // Per-tick informational announcement (fires only on
                            // road/district transitions).
                            MaybeAnnounceLocationChange(playerPos);

                            float currentDist = World.GetDistance(playerPos, autodriveDestination);

                            // Arrival detection (within 25 meters)
                            if (currentDist < 25f)
                            {
                                isAutodriving = false;
                                Game.Player.Character.Task.ClearAll();
                                Tolk.Speak("Arrived at destination. You have control.");
                            }
                            // Progress announcements every 10 seconds
                            else if (DateTime.Now.Ticks - autodriveCheckTicks > 100000000) // 10 seconds
                            {
                                autodriveCheckTicks = DateTime.Now.Ticks;

                                // Only announce if we've made significant progress
                                if (currentDist < autodriveStartDistance * 0.9f) // At least 10% progress
                                {
                                    // Calculate ETA based on current speed
                                    float speed = Game.Player.Character.CurrentVehicle != null ?
                                        Game.Player.Character.CurrentVehicle.Speed : 0f;

                                    if (speed > 1f)
                                    {
                                        int etaSeconds = (int)(currentDist / speed);
                                        if (etaSeconds > 60)
                                        {
                                            int mins = etaSeconds / 60;
                                            Tolk.Speak((int)currentDist + " meters remaining. About " + mins + " minute" + (mins > 1 ? "s" : "") + ".", true);
                                        }
                                        else if (currentDist > 100f)
                                        {
                                            Tolk.Speak((int)currentDist + " meters remaining.", true);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (getSetting("infiniteAmmo") == 1)
                {
                    Game.Player.Character.Weapons.Current.InfiniteAmmoClip = true;
                    Game.Player.Character.Weapons.Current.InfiniteAmmo = true;
                }
                else
                {
                    Game.Player.Character.Weapons.Current.InfiniteAmmo = false;
                    Game.Player.Character.Weapons.Current.InfiniteAmmoClip = false;
                }

                if (getSetting("exsplosiveAmmo") == 1)
                    Game.Player.SetExplosiveAmmoThisFrame();
                if (getSetting("fireAmmo") == 1)
                    Game.Player.SetFireAmmoThisFrame();
                if (getSetting("explosiveMelee") == 1)
                    Game.Player.SetExplosiveMeleeThisFrame();
                if (getSetting("superJump") == 1)
                    Game.Player.SetSuperJumpThisFrame();
                if (getSetting("runFaster") == 1)
                    Game.Player.SetRunSpeedMultThisFrame(2f);
                if (getSetting("swimFaster") == 1)
                    Game.Player.SetSwimSpeedMultThisFrame(2f);

                if (Game.Player.Character.IsFalling || Game.Player.Character.IsGettingIntoVehicle || Game.Player.Character.IsGettingUp || Game.Player.Character.IsProne || Game.Player.Character.IsRagdoll)
                {
                }
                else
                {
                    double heading = Game.Player.Character.Heading;
                    if (headings[headingSlice(heading)] == false)
                    {
                        headings[headingSlice(heading)] = true;
                        for (int i = 0; i < headings.Length; i++)
                        {
                            if (i != headingSlice(heading))
                                headings[i] = false;

                        }
                        if (getSetting("announceHeadings") == 1)
                            Tolk.Speak(headingSliceName(heading), true);
                    }
                }

                TimeSpan t = World.CurrentTimeOfDay;
                if (t.Minutes == 0)
                {
                    if ((t.Hours == 3 || t.Hours == 6 || t.Hours == 9 || t.Hours == 12 || t.Hours == 15 || t.Hours == 18 || t.Hours == 21) && timeAnnounced == false)
                    {
                        timeAnnounced = true;
                        if (getSetting("announceTime") == 1)
                            Tolk.Speak("The time is now: " + t.Hours + ":00");
                    }
                }
                else
                {
                    timeAnnounced = false;
                }

                if (street != World.GetStreetName(Game.Player.Character.Position))
                {
                    street = World.GetStreetName(Game.Player.Character.Position);
                    if (getSetting("announceZones") == 1)
                        Tolk.Speak(street);
                }

                if (zone != World.GetZoneLocalizedName(Game.Player.Character.Position))
                {
                    zone = World.GetZoneLocalizedName(Game.Player.Character.Position);
                    if (getSetting("announceZones") == 1)
                        Tolk.Speak(zone);
                }

                if (Game.Player.Character.Weapons.Current.Hash.ToString() != currentWeapon)
                {
                    currentWeapon = Game.Player.Character.Weapons.Current.Hash.ToString();

                    // Get weapon info for announcement
                    Weapon wep = Game.Player.Character.Weapons.Current;
                    string weaponName = currentWeapon;

                    // Try to get a readable name from hashes
                    if (hashes.ContainsKey(wep.Hash.ToString()))
                        weaponName = hashes[wep.Hash.ToString()];

                    // Build ammo announcement string
                    string ammoInfo = "";
                    if (wep.Hash != WeaponHash.Unarmed && wep.Hash != WeaponHash.Knife && wep.Hash != WeaponHash.Nightstick &&
                        wep.Hash != WeaponHash.Hammer && wep.Hash != WeaponHash.Bat && wep.Hash != WeaponHash.Crowbar &&
                        wep.Hash != WeaponHash.GolfClub && wep.Hash != WeaponHash.Bottle && wep.Hash != WeaponHash.Dagger &&
                        wep.Hash != WeaponHash.Hatchet && wep.Hash != WeaponHash.KnuckleDuster && wep.Hash != WeaponHash.Machete &&
                        wep.Hash != WeaponHash.Flashlight && wep.Hash != WeaponHash.SwitchBlade && wep.Hash != WeaponHash.PoolCue &&
                        wep.Hash != WeaponHash.Wrench && wep.Hash != WeaponHash.BattleAxe && wep.Hash != WeaponHash.StoneHatchet)
                    {
                        // Get ammo using native - pass weapon hash as int, not uint
                        OutputArgument outAmmo = new OutputArgument();
                        bool success = Function.Call<bool>(Hash.GET_AMMO_IN_CLIP, Game.Player.Character, (int)wep.Hash, outAmmo);
                        int ammoInClip = success ? outAmmo.GetResult<int>() : 0;

                        // Fallback: if native returns 0 but we have ammo, estimate from total
                        int totalAmmo = Function.Call<int>(Hash.GET_AMMO_IN_PED_WEAPON, Game.Player.Character, (int)wep.Hash);
                        int maxClip = wep.MaxAmmoInClip;

                        if (ammoInClip == 0 && totalAmmo > 0 && maxClip > 0)
                        {
                            // Estimate: assume clip is full or has remainder
                            ammoInClip = Math.Min(totalAmmo, maxClip);
                        }

                        int reserveAmmo = Math.Max(0, totalAmmo - ammoInClip);
                        ammoInfo = ", " + ammoInClip + " in magazine, " + reserveAmmo + " reserve";
                        lastAmmoInClip = ammoInClip;
                        lowAmmoWarningGiven = false;
                    }

                    Tolk.Speak(weaponName + ammoInfo);
                }

                // Low ammo warning (check every frame when weapon is drawn)
                if (!lowAmmoWarningGiven && Game.Player.Character.Weapons.Current.Hash != WeaponHash.Unarmed)
                {
                    Weapon wep = Game.Player.Character.Weapons.Current;
                    int maxClip = wep.MaxAmmoInClip;

                    // Use native function for reliable ammo reading
                    OutputArgument outAmmo = new OutputArgument();
                    bool success = Function.Call<bool>(Hash.GET_AMMO_IN_CLIP, Game.Player.Character, (int)wep.Hash, outAmmo);
                    int currentClip = success ? outAmmo.GetResult<int>() : 0;

                    // Fallback if native fails
                    if (currentClip == 0 && maxClip > 0)
                    {
                        int totalAmmo = Function.Call<int>(Hash.GET_AMMO_IN_PED_WEAPON, Game.Player.Character, (int)wep.Hash);
                        if (totalAmmo > 0)
                            currentClip = Math.Min(totalAmmo, maxClip);
                    }

                    if (maxClip > 0 && lastAmmoInClip > 0)
                    {
                        // Check if dropped below 25% and we fired (ammo decreased)
                        float ammoPercent = (float)currentClip / maxClip;
                        if (ammoPercent < 0.25f && currentClip < lastAmmoInClip && currentClip > 0)
                        {
                            Tolk.Speak("Low ammo, " + currentClip + " rounds", true);
                            lowAmmoWarningGiven = true;
                        }
                    }
                    lastAmmoInClip = currentClip;
                }

                if (getSetting("speed") == 1 && DateTime.Now.Ticks - drivingTicks > 25000000 && Game.Player.Character.CurrentVehicle != null && Game.Player.Character.CurrentVehicle.Speed > 1)
                {
                    drivingTicks = DateTime.Now.Ticks;
                    // Convert game units to MPH: 1 unit = 3.6 km/h, then convert km/h to MPH
                    double speedMPH = Math.Round(Game.Player.Character.CurrentVehicle.Speed * 2.236856);
                    Tolk.Speak("" + speedMPH + " miles per hour");
                }


                if (DateTime.Now.Ticks - targetTicks > 2000000 && Game.Player.TargetedEntity != null && Game.Player.Character.Weapons.Current.Hash != WeaponHash.HomingLauncher)
                {
                    targetTicks = DateTime.Now.Ticks;
                    if (Game.Player.TargetedEntity.EntityType == EntityType.Ped && !Game.Player.TargetedEntity.IsDead)
                    {
                        out1.Stop();
                        tped.Position = 0;
                        out1.Play();
                    }

                    if (Game.Player.TargetedEntity.EntityType == EntityType.Vehicle && !Game.Player.TargetedEntity.IsDead)
                    {
                        out2.Stop();
                        tvehicle.Position = 0;
                        out2.Play();
                    }

                    if (Game.Player.TargetedEntity.EntityType == EntityType.Prop && (!Game.Player.TargetedEntity.IsExplosionProof || !Game.Player.TargetedEntity.IsBulletProof))
                    {
                        out3.Stop();
                        tprop.Position = 0;
                        out3.Play();
                    }
                }

                // ============================================
                // AIM AUTOLOCK SYSTEM
                // ============================================
                if (getSetting("aimAutolock") == 1)
                {
                    bool isAiming = GTA.GameplayCamera.IsAimCamActive;
                    Entity currentTarget = Game.Player.TargetedEntity;
                    bool isHomingLauncher = Game.Player.Character.Weapons.Current.Hash == WeaponHash.HomingLauncher;

                    if (isAiming && !isHomingLauncher)
                    {
                        // Check if we're re-aiming within grace period with existing target
                        bool reacquiringTarget = !autolockActive && autolockTarget != null &&
                            autolockTarget.Exists() && !autolockTarget.IsDead &&
                            (DateTime.Now.Ticks - autolockReleaseTicks) < AUTOLOCK_GRACE_PERIOD;

                        if (reacquiringTarget)
                        {
                            // Re-activate lock on the same target, snap camera to it
                            autolockActive = true;
                            // Instant snap to target position (use higher lerp factor)
                            UpdateAutolockAim(true); // Pass true for instant snap
                        }
                        // New target acquired - only update if game has a valid target
                        else if (currentTarget != null && !currentTarget.IsDead && currentTarget != autolockTarget)
                        {
                            autolockTarget = currentTarget;
                            autolockPartIndex = 0;
                            autolockActive = true;

                            // Find the first visible part (default parts like engine/torso should always be visible)
                            // This ensures we don't start locked onto an obscured part
                            int maxParts = currentTarget.EntityType == EntityType.Ped
                                ? PED_TARGET_PARTS.Length
                                : VEHICLE_TARGET_PARTS.Length;
                            for (int i = 0; i < maxParts; i++)
                            {
                                if (IsPartVisible(i))
                                {
                                    autolockPartIndex = i;
                                    break;
                                }
                            }

                            if (currentTarget != lastAnnouncedTarget)
                            {
                                lastAnnouncedTarget = currentTarget;
                                AnnounceAutolockTarget(currentTarget);
                            }
                        }

                        // Active tracking - maintain lock even if game's TargetedEntity becomes null
                        // This is critical for vehicles which lose soft-lock more easily than peds
                        if (autolockActive && autolockTarget != null && autolockTarget.Exists() && !autolockTarget.IsDead)
                        {
                            // Disable right stick camera controls to prevent player from moving aim off target
                            // Only disable look controls - do NOT disable aim (25) as it causes toggle issues
                            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 1, true);   // Look Left/Right
                            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 2, true);   // Look Up/Down
                            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 220, true); // Script Right Axis X
                            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 221, true); // Script Right Axis Y

                            UpdateAutolockAim(false);
                            HandlePartCycling();
                        }
                        else if (autolockTarget != null && (!autolockTarget.Exists() || autolockTarget.IsDead))
                        {
                            // Target no longer valid (destroyed/dead), clear lock
                            autolockActive = false;
                            autolockTarget = null;
                        }
                    }
                    else
                    {
                        // LT released - start grace period but keep target reference
                        if (autolockActive)
                        {
                            autolockActive = false;
                            autolockReleaseTicks = DateTime.Now.Ticks;
                            // Don't clear autolockTarget - keep it for grace period
                        }
                        // Clear target only after grace period expires
                        else if (autolockTarget != null &&
                            (DateTime.Now.Ticks - autolockReleaseTicks) >= AUTOLOCK_GRACE_PERIOD)
                        {
                            autolockTarget = null;
                        }
                    }
                }

                // Navigation Assist - Multi-directional obstacle detection with stereo panning
                // Works on foot and in vehicles with speed-based parameters
                // Uses HYBRID approach: proximity for peds/vehicles + raycast for world geometry
                // Detects in 4 zones: LEFT, CENTER, RIGHT, BEHIND (behind only when moving backwards)
                // BEHIND uses center pan + one octave lower frequency
                if (getSetting("navigationAssist") != 1)
                {
                    // Nav assist is off — clear shared globals so drive assist doesn't
                    // consume stale obstacle data from the last time nav assist ran.
                    if (navAssistTypeCenter != "none" || navAssistTypeLeft != "none"
                        || navAssistTypeRight != "none" || navAssistTypeBehind != "none")
                    {
                        navAssistDistLeft = navAssistDistCenter = navAssistDistRight = navAssistDistBehind = 999f;
                        navAssistTypeLeft = navAssistTypeCenter = navAssistTypeRight = navAssistTypeBehind = "none";
                        navAssistVehicleLeft = navAssistVehicleCenter = navAssistVehicleRight = navAssistVehicleBehind = null;
                        navAssistPedLeft = navAssistPedCenter = navAssistPedRight = null;
                        navAssistNormalCenter = GTA.Math.Vector3.Zero;
                    }
                }
                if (getSetting("navigationAssist") == 1)
                {
                    bool inVehicle = Game.Player.Character.IsInVehicle();
                    float vehicleSpeed = 0f;
                    bool isMovingBackwards = false;

                    if (inVehicle && Game.Player.Character.CurrentVehicle != null)
                    {
                        vehicleSpeed = Game.Player.Character.CurrentVehicle.Speed;
                        // Check if vehicle is reversing
                        GTA.Math.Vector3 vehVelocity = Game.Player.Character.CurrentVehicle.Velocity;
                        GTA.Math.Vector3 vehForward = Game.Player.Character.CurrentVehicle.ForwardVector;
                        float dotVel = GTA.Math.Vector3.Dot(vehForward, GTA.Math.Vector3.Normalize(vehVelocity));
                        isMovingBackwards = (dotVel < -0.3f && vehicleSpeed > 1f);
                    }
                    else
                    {
                        // Check if player is walking backwards on foot
                        GTA.Math.Vector3 playerVelocity = Game.Player.Character.Velocity;
                        GTA.Math.Vector3 playerForward = Game.Player.Character.ForwardVector;
                        if (playerVelocity.Length() > 0.5f)
                        {
                            float dotVel = GTA.Math.Vector3.Dot(playerForward, GTA.Math.Vector3.Normalize(playerVelocity));
                            isMovingBackwards = (dotVel < -0.3f);
                        }
                    }

                    // FASTER intervals - especially important for vehicles
                    // On foot: 100ms (was 80ms)
                    // In vehicle: 60ms base, down to 40ms at high speed
                    long baseInterval = 1000000; // 100ms in ticks
                    if (inVehicle)
                    {
                        float speedFactor = Math.Min(vehicleSpeed / 40f, 1f);
                        baseInterval = (long)(600000 - (speedFactor * 200000)); // 60ms to 40ms
                    }

                    if (DateTime.Now.Ticks - navAssistTicks > baseInterval)
                    {
                        navAssistTicks = DateTime.Now.Ticks;
                        raycastCounter++;

                        // Detection range. On-foot keeps the tight 5m; in-vehicle
                        // scales by the meta brake-distance formula for the
                        // current vehicle class (iter-8). A truck (Max=120m)
                        // gets longer lookahead than a sports car (Max=120m
                        // but Min=8m — ramps faster). 1.3x margin so brake
                        // has time to ramp up. Old `vehicleSpeed * 3.0f`
                        // formula was class-agnostic and identical for every
                        // vehicle.
                        float maxRange = 5f;
                        if (inVehicle && Game.Player.Character.CurrentVehicle != null)
                        {
                            var navAi = VehicleAIHandlingRegistry.GetForVehicle(
                                Game.Player.Character.CurrentVehicle);
                            float la = navAi.BrakeLookaheadForSpeed(vehicleSpeed) * 1.3f;
                            maxRange = Math.Min(120f, Math.Max(10f, la));
                        }

                        GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                        GTA.Math.Vector3 forwardVec;
                        GTA.Math.Vector3 rightVec;
                        float playerHeading;

                        if (inVehicle && Game.Player.Character.CurrentVehicle != null)
                        {
                            forwardVec = Game.Player.Character.CurrentVehicle.ForwardVector;
                            rightVec = Game.Player.Character.CurrentVehicle.RightVector;
                            playerHeading = Game.Player.Character.CurrentVehicle.Heading;
                            playerPos = Game.Player.Character.CurrentVehicle.Position;
                        }
                        else
                        {
                            forwardVec = Game.Player.Character.ForwardVector;
                            // Calculate right vector from forward (rotate 90 degrees)
                            rightVec = new GTA.Math.Vector3(forwardVec.Y, -forwardVec.X, 0);
                            playerHeading = Game.Player.Character.Heading;
                        }

                        // ============================================
                        // MULTI-DIRECTIONAL DETECTION SYSTEM
                        // Zones: LEFT (-45° to -135°), CENTER (-45° to +45°), RIGHT (+45° to +135°), BEHIND (when moving backwards)
                        // ============================================

                        // Results per zone
                        float distLeft = 999f, distCenter = 999f, distRight = 999f, distBehind = 999f;
                        string typeLeft = "none", typeCenter = "none", typeRight = "none", typeBehind = "none";
                        string nameLeft = "", nameCenter = "", nameRight = "", nameBehind = "";

                        // Entity references for drive assist (allows proper TTC calculation with actual velocities)
                        Vehicle vehLeft = null, vehCenter = null, vehRight = null, vehBehind = null;
                        Ped pedLeft = null, pedCenter = null, pedRight = null;

                        // Minimum distance to avoid self-detection
                        float minDist = inVehicle ? 1.5f : 0.5f;

                        // Check if drive assist is enabled - enables rear detection even when going forward
                        bool driveAssistEnabled = inVehicle && getSetting("steeringAssist") > 0;

                        // --- SCAN NEARBY PEDS ---
                        Ped[] nearbyPeds = World.GetNearbyPeds(playerPos, maxRange + 2f);
                        foreach (Ped ped in nearbyPeds)
                        {
                            // Skip dead peds, the player, and peds inside vehicles
                            // (in-vehicle peds get double-counted with the containing
                            // vehicle's scan, producing spurious steerTTC<0.1s threats
                            // in chase scenarios — see analysis cluster 9/10 F9390).
                            if (ped == Game.Player.Character || ped.IsDead || ped.IsInVehicle()) continue;

                            GTA.Math.Vector3 toEntity = ped.Position - playerPos;
                            float dist = toEntity.Length();
                            if (dist < minDist || dist > maxRange) continue;

                            // Calculate angle to entity
                            GTA.Math.Vector3 toEntityNorm = GTA.Math.Vector3.Normalize(toEntity);
                            float dotForward = GTA.Math.Vector3.Dot(forwardVec, toEntityNorm);
                            float dotRight = GTA.Math.Vector3.Dot(rightVec, toEntityNorm);

                            // Determine zone based on angle
                            // CENTER: mostly forward (dotForward > 0.5, i.e. within ~60° of forward)
                            // LEFT: to the left (dotRight < -0.3 and not behind)
                            // RIGHT: to the right (dotRight > 0.3 and not behind)
                            // BEHIND: behind us (dotForward < -0.5) - only when moving backwards

                            if (dotForward > 0.5f)
                            {
                                // CENTER zone
                                if (dist < distCenter) { distCenter = dist; typeCenter = "ped"; nameCenter = "Pedestrian"; pedCenter = ped; vehCenter = null; }
                            }
                            else if (dotForward < -0.5f && (isMovingBackwards || driveAssistEnabled))
                            {
                                // BEHIND zone (when moving backwards OR drive assist needs spatial awareness)
                                if (dist < distBehind) { distBehind = dist; typeBehind = "ped"; nameBehind = "Pedestrian"; }
                            }
                            else if (dotForward > -0.2f) // Not behind us
                            {
                                if (dotRight < -0.3f && dist < distLeft)
                                {
                                    distLeft = dist; typeLeft = "ped"; nameLeft = "Pedestrian"; pedLeft = ped; vehLeft = null;
                                }
                                else if (dotRight > 0.3f && dist < distRight)
                                {
                                    distRight = dist; typeRight = "ped"; nameRight = "Pedestrian"; pedRight = ped; vehRight = null;
                                }
                            }
                        }

                        // --- SCAN NEARBY VEHICLES ---
                        Vehicle[] nearbyVehicles = World.GetNearbyVehicles(playerPos, maxRange + 2f);
                        foreach (Vehicle veh in nearbyVehicles)
                        {
                            if (inVehicle && veh == Game.Player.Character.CurrentVehicle) continue;

                            GTA.Math.Vector3 toEntity = veh.Position - playerPos;
                            float dist = toEntity.Length();
                            if (dist < minDist || dist > maxRange) continue;

                            GTA.Math.Vector3 toEntityNorm = GTA.Math.Vector3.Normalize(toEntity);
                            float dotForward = GTA.Math.Vector3.Dot(forwardVec, toEntityNorm);
                            float dotRight = GTA.Math.Vector3.Dot(rightVec, toEntityNorm);

                            if (dotForward > 0.5f)
                            {
                                if (dist < distCenter) { distCenter = dist; typeCenter = "vehicle"; nameCenter = veh.LocalizedName; vehCenter = veh; pedCenter = null; }
                            }
                            else if (dotForward < -0.5f && (isMovingBackwards || driveAssistEnabled))
                            {
                                // BEHIND zone (when moving backwards OR drive assist needs spatial awareness)
                                if (dist < distBehind) { distBehind = dist; typeBehind = "vehicle"; nameBehind = veh.LocalizedName; vehBehind = veh; }
                            }
                            else if (dotForward > -0.2f)
                            {
                                if (dotRight < -0.3f && dist < distLeft)
                                {
                                    distLeft = dist; typeLeft = "vehicle"; nameLeft = veh.LocalizedName; vehLeft = veh; pedLeft = null;
                                }
                                else if (dotRight > 0.3f && dist < distRight)
                                {
                                    distRight = dist; typeRight = "vehicle"; nameRight = veh.LocalizedName; vehRight = veh; pedRight = null;
                                }
                            }
                        }

                        // --- RAYCAST FOR WORLD GEOMETRY (3 directions) ---
                        float rayHeight = inVehicle ? 0.5f : 1.0f;
                        GTA.Math.Vector3 startPos = playerPos + new GTA.Math.Vector3(0, 0, rayHeight);

                        // Check if shape casting is enabled
                        bool useShapeCast = getSetting("shapeCasting") == 1;

                        // Surface normal of the center shape-cast hit (drive-assist
                        // wall-vs-angled-surface decision). Zero = no usable normal.
                        GTA.Math.Vector3 centerNormal = GTA.Math.Vector3.Zero;

                        if (useShapeCast)
                        {
                            // --- SHAPE CASTING MODE: Multi-ray cone pattern for better coverage ---
                            GTA.Math.Vector3 hitPos;
                            GTA.Math.Vector3 hitNorm;

                            // Center zone - shape cast forward
                            float centerDist = PerformShapeCast(startPos, forwardVec, rightVec, maxRange,
                                IntersectFlags.Map, Game.Player.Character, out hitPos, out hitNorm, vehicleSpeed);
                            if (centerDist > 0 && centerDist >= minDist && centerDist < distCenter)
                            {
                                distCenter = centerDist; typeCenter = "world"; nameCenter = "Obstacle";
                                centerNormal = hitNorm;
                            }

                            // Left zone - shape cast left-diagonal (45 degrees)
                            GTA.Math.Vector3 leftDir = GTA.Math.Vector3.Normalize(forwardVec - rightVec);
                            float leftDist = PerformShapeCast(startPos, leftDir, rightVec, maxRange,
                                IntersectFlags.Map, Game.Player.Character, out hitPos, out hitNorm, vehicleSpeed);
                            if (leftDist > 0 && leftDist >= minDist && leftDist < distLeft)
                            {
                                distLeft = leftDist; typeLeft = "world"; nameLeft = "Obstacle";
                            }

                            // Right zone - shape cast right-diagonal (45 degrees)
                            GTA.Math.Vector3 rightDir = GTA.Math.Vector3.Normalize(forwardVec + rightVec);
                            float rightDist = PerformShapeCast(startPos, rightDir, rightVec, maxRange,
                                IntersectFlags.Map, Game.Player.Character, out hitPos, out hitNorm, vehicleSpeed);
                            if (rightDist > 0 && rightDist >= minDist && rightDist < distRight)
                            {
                                distRight = rightDist; typeRight = "world"; nameRight = "Obstacle";
                            }
                        }
                        else
                        {
                            // --- STANDARD MODE: Single ray per direction ---
                            // Center ray (forward)
                            RaycastResult rayCenter = World.Raycast(startPos, startPos + (forwardVec * maxRange), IntersectFlags.Map, Game.Player.Character);
                            if (rayCenter.DidHit)
                            {
                                float d = World.GetDistance(startPos, rayCenter.HitPosition);
                                if (d >= minDist && d < distCenter) { distCenter = d; typeCenter = "world"; nameCenter = "Wall/Building"; }
                            }

                            // Left ray (45 degrees left of forward)
                            GTA.Math.Vector3 leftDir = GTA.Math.Vector3.Normalize(forwardVec - rightVec);
                            RaycastResult rayLeft = World.Raycast(startPos, startPos + (leftDir * maxRange), IntersectFlags.Map, Game.Player.Character);
                            if (rayLeft.DidHit)
                            {
                                float d = World.GetDistance(startPos, rayLeft.HitPosition);
                                if (d >= minDist && d < distLeft) { distLeft = d; typeLeft = "world"; nameLeft = "Wall/Building"; }
                            }

                            // Right ray (45 degrees right of forward)
                            GTA.Math.Vector3 rightDir = GTA.Math.Vector3.Normalize(forwardVec + rightVec);
                            RaycastResult rayRight = World.Raycast(startPos, startPos + (rightDir * maxRange), IntersectFlags.Map, Game.Player.Character);
                            if (rayRight.DidHit)
                            {
                                float d = World.GetDistance(startPos, rayRight.HitPosition);
                                if (d >= minDist && d < distRight) { distRight = d; typeRight = "world"; nameRight = "Wall/Building"; }
                            }
                        }

                        // Side rays (90 degrees) - only when in vehicle for tight spaces
                        if (inVehicle)
                        {
                            float sideRange = Math.Min(maxRange, 8f); // Side detection max 8m
                            GTA.Math.Vector3 pureLeft = new GTA.Math.Vector3(-rightVec.X, -rightVec.Y, 0);

                            if (useShapeCast)
                            {
                                // Shape cast for side detection
                                GTA.Math.Vector3 hitPos;
                                GTA.Math.Vector3 hitNorm;
                                float sideLeftDist = PerformShapeCast(startPos, pureLeft, rightVec, sideRange,
                                    IntersectFlags.Map, Game.Player.Character, out hitPos, out hitNorm, vehicleSpeed);
                                if (sideLeftDist > 0 && sideLeftDist >= minDist && sideLeftDist < distLeft)
                                {
                                    distLeft = sideLeftDist; typeLeft = "world"; nameLeft = "Side Obstacle";
                                }

                                float sideRightDist = PerformShapeCast(startPos, rightVec, rightVec, sideRange,
                                    IntersectFlags.Map, Game.Player.Character, out hitPos, out hitNorm, vehicleSpeed);
                                if (sideRightDist > 0 && sideRightDist >= minDist && sideRightDist < distRight)
                                {
                                    distRight = sideRightDist; typeRight = "world"; nameRight = "Side Obstacle";
                                }
                            }
                            else
                            {
                                // Pure left ray
                                RaycastResult raySideLeft = World.Raycast(startPos, startPos + (pureLeft * sideRange), IntersectFlags.Map, Game.Player.Character);
                                if (raySideLeft.DidHit)
                                {
                                    float d = World.GetDistance(startPos, raySideLeft.HitPosition);
                                    if (d >= minDist && d < distLeft) { distLeft = d; typeLeft = "world"; nameLeft = "Side Wall"; }
                                }

                                // Pure right ray
                                RaycastResult raySideRight = World.Raycast(startPos, startPos + (rightVec * sideRange), IntersectFlags.Map, Game.Player.Character);
                                if (raySideRight.DidHit)
                                {
                                    float d = World.GetDistance(startPos, raySideRight.HitPosition);
                                    if (d >= minDist && d < distRight) { distRight = d; typeRight = "world"; nameRight = "Side Wall"; }
                                }
                            }
                        }

                        // Behind ray - active when moving backwards OR when drive assist is enabled (for spatial awareness)
                        // Drive assist uses rear detection for handbrake turns and angle correction
                        if (isMovingBackwards || driveAssistEnabled)
                        {
                            GTA.Math.Vector3 behindDir = new GTA.Math.Vector3(-forwardVec.X, -forwardVec.Y, 0);

                            if (useShapeCast)
                            {
                                GTA.Math.Vector3 hitPos;
                                GTA.Math.Vector3 hitNorm;
                                float behindDist = PerformShapeCast(startPos, behindDir, rightVec, maxRange,
                                    IntersectFlags.Map, Game.Player.Character, out hitPos, out hitNorm, vehicleSpeed);
                                if (behindDist > 0 && behindDist >= minDist && behindDist < distBehind)
                                {
                                    distBehind = behindDist; typeBehind = "world"; nameBehind = "Obstacle Behind";
                                }
                            }
                            else
                            {
                                RaycastResult rayBehind = World.Raycast(startPos, startPos + (behindDir * maxRange), IntersectFlags.Map, Game.Player.Character);
                                if (rayBehind.DidHit)
                                {
                                    float d = World.GetDistance(startPos, rayBehind.HitPosition);
                                    if (d >= minDist && d < distBehind) { distBehind = d; typeBehind = "world"; nameBehind = "Wall Behind"; }
                                }
                            }
                        }

                        // ============================================
                        // BEEP LOGIC - Reduce spam when stationary
                        // ============================================

                        bool beepLeft = false, beepCenter = false, beepRight = false, beepBehind = false;

                        // LEFT zone
                        if (distLeft < maxRange)
                        {
                            if (Math.Abs(distLeft - lastDistLeft) < 0.15f)
                            {
                                sameCountLeft++;
                                if (sameCountLeft >= 3 || (inVehicle && vehicleSpeed > 2f)) { beepLeft = true; sameCountLeft = 0; }
                            }
                            else { beepLeft = true; sameCountLeft = 0; }
                            lastDistLeft = distLeft;
                        }
                        else { lastDistLeft = -1f; sameCountLeft = 0; }

                        // CENTER zone
                        if (distCenter < maxRange)
                        {
                            if (Math.Abs(distCenter - lastDistCenter) < 0.15f)
                            {
                                sameCountCenter++;
                                if (sameCountCenter >= 3 || (inVehicle && vehicleSpeed > 2f)) { beepCenter = true; sameCountCenter = 0; }
                            }
                            else { beepCenter = true; sameCountCenter = 0; }
                            lastDistCenter = distCenter;
                        }
                        else { lastDistCenter = -1f; sameCountCenter = 0; }

                        // RIGHT zone
                        if (distRight < maxRange)
                        {
                            if (Math.Abs(distRight - lastDistRight) < 0.15f)
                            {
                                sameCountRight++;
                                if (sameCountRight >= 3 || (inVehicle && vehicleSpeed > 2f)) { beepRight = true; sameCountRight = 0; }
                            }
                            else { beepRight = true; sameCountRight = 0; }
                            lastDistRight = distRight;
                        }
                        else { lastDistRight = -1f; sameCountRight = 0; }

                        // BEHIND zone (only when moving backwards)
                        if (isMovingBackwards && distBehind < maxRange)
                        {
                            if (Math.Abs(distBehind - lastDistBehind) < 0.15f)
                            {
                                sameCountBehind++;
                                if (sameCountBehind >= 3 || (inVehicle && vehicleSpeed > 2f)) { beepBehind = true; sameCountBehind = 0; }
                            }
                            else { beepBehind = true; sameCountBehind = 0; }
                            lastDistBehind = distBehind;
                        }
                        else { lastDistBehind = -1f; sameCountBehind = 0; }

                        // ============================================
                        // PLAY BEEPS WITH STEREO PANNING
                        // Different waveforms for different entity types
                        // Frequency based on distance (closer = higher pitch)
                        // Only play beeps if navAssistBeeps setting is enabled
                        // ============================================
                        bool playBeeps = getSetting("navAssistBeeps") == 1;

                        // LEFT beep (panned left)
                        if (beepLeft && playBeeps)
                        {
                            float normDist = Math.Max(0f, distLeft) / maxRange;
                            outNavLeft.Stop();
                            navBeepLeft.Gain = 0.08;
                            navBeepLeft.Frequency = GetFrequencyForType(typeLeft, normDist);
                            navBeepLeft.Type = GetWaveformForType(typeLeft);
                            // Create panned output (left speaker only)
                            var leftSample = navBeepLeft.Take(TimeSpan.FromSeconds(0.06));
                            var leftPanned = new PanningSampleProvider(leftSample) { Pan = -1f };
                            outNavLeft.Init(leftPanned);
                            outNavLeft.Play();
                        }

                        // CENTER beep (both speakers)
                        if (beepCenter && playBeeps)
                        {
                            float normDist = Math.Max(0f, distCenter) / maxRange;
                            outNavCenter.Stop();
                            navBeepCenter.Gain = 0.08;
                            navBeepCenter.Frequency = GetFrequencyForType(typeCenter, normDist);
                            navBeepCenter.Type = GetWaveformForType(typeCenter);
                            // Center = both speakers (pan = 0)
                            var centerSample = navBeepCenter.Take(TimeSpan.FromSeconds(0.06));
                            var centerPanned = new PanningSampleProvider(centerSample) { Pan = 0f };
                            outNavCenter.Init(centerPanned);
                            outNavCenter.Play();
                        }

                        // RIGHT beep (panned right)
                        if (beepRight && playBeeps)
                        {
                            float normDist = Math.Max(0f, distRight) / maxRange;
                            outNavRight.Stop();
                            navBeepRight.Gain = 0.08;
                            navBeepRight.Frequency = GetFrequencyForType(typeRight, normDist);
                            navBeepRight.Type = GetWaveformForType(typeRight);
                            // Right speaker only
                            var rightSample = navBeepRight.Take(TimeSpan.FromSeconds(0.06));
                            var rightPanned = new PanningSampleProvider(rightSample) { Pan = 1f };
                            outNavRight.Init(rightPanned);
                            outNavRight.Play();
                        }

                        // BEHIND beep (center pan, one octave lower frequency)
                        if (beepBehind && playBeeps)
                        {
                            float normDist = Math.Max(0f, distBehind) / maxRange;
                            outNavBehind.Stop();
                            navBeepBehind.Gain = 0.08;
                            // One octave lower = divide frequency by 2
                            navBeepBehind.Frequency = GetFrequencyForType(typeBehind, normDist) / 2f;
                            navBeepBehind.Type = GetWaveformForType(typeBehind);
                            // Center pan for behind
                            var behindSample = navBeepBehind.Take(TimeSpan.FromSeconds(0.06));
                            var behindPanned = new PanningSampleProvider(behindSample) { Pan = 0f };
                            outNavBehind.Init(behindPanned);
                            outNavBehind.Play();
                        }

                        // Store nav assist distances for drive assist integration
                        navAssistDistLeft = distLeft;
                        navAssistDistCenter = distCenter;
                        navAssistDistRight = distRight;
                        navAssistDistBehind = distBehind;
                        navAssistTypeLeft = typeLeft;
                        navAssistTypeCenter = typeCenter;
                        navAssistTypeRight = typeRight;
                        navAssistTypeBehind = typeBehind;
                        navAssistNormalCenter = centerNormal;

                        // Store entity references for drive assist to use actual velocities
                        navAssistVehicleLeft = vehLeft;
                        navAssistVehicleCenter = vehCenter;
                        navAssistVehicleRight = vehRight;
                        navAssistVehicleBehind = vehBehind;
                        navAssistPedLeft = pedLeft;
                        navAssistPedCenter = pedCenter;
                        navAssistPedRight = pedRight;
                    }
                }

                // ============================================
                // SMART STEERING ASSISTS SYSTEM
                // Modes: 0=Off, 1=Assistive (nudges), 2=Full (takes over)
                // IMPORTANT: Detection runs periodically, but inputs MUST be applied every tick
                // ============================================
                int steeringAssistMode = getSetting("steeringAssist");
                if (steeringAssistMode > 0 && Game.Player.Character.IsInVehicle() && !isAutodriving)
                {
                    Vehicle playerVeh = Game.Player.Character.CurrentVehicle;
                    if (playerVeh != null)
                    {
                        float vehicleSpeed = playerVeh.Speed;
                        cachedIsFullMode = (steeringAssistMode == 2);

                        // Only active when moving (>2 m/s) — EXCEPT while a
                        // recovery/alignment is in progress. A wrong-way or
                        // off-road car often has to be braked to a full stop
                        // and U-turned; if the assist shut off below 2 m/s it
                        // could never finish the maneuver (the car sat stuck).
                        // Iter-10 Patch F: also stay alive while emergency
                        // brake is latched OR a meaningful ramped brake is
                        // still applied — otherwise an impact that decelerates
                        // the car below 2 m/s mid-emergency snap-zeroes
                        // rampedBrakeInput in the else-branch below and
                        // releases the brake AT the moment of collision.
                        // driveassist-2026-05-26-000252 AUTO-COLLISION #2 at
                        // F2616 and #15 at F7830 are this exact pattern.
                        if (vehicleSpeed > 2f
                            || currentDriveMode != DriveMode.LaneKeeping
                            || emergencyBrakeActive
                            || rampedBrakeInput > 0.1f)
                        {
                            // DETECTION: Process at 30-50ms intervals based on speed
                            float speedFactor = Math.Min(vehicleSpeed / 30f, 1f);
                            long assistInterval = (long)(500000 - (speedFactor * 200000));

                            if (DateTime.Now.Ticks - steeringAssistTicks > assistInterval)
                            {
                                steeringAssistTicks = DateTime.Now.Ticks;
                                ProcessSteeringAssist(playerVeh, cachedIsFullMode);
                            }

                            // INPUT APPLICATION: Must happen EVERY tick for inputs to work!
                            if (steeringAssistActive)
                            {
                                ApplyCachedSteeringInputs(playerVeh);
                            }
                        }
                        else if (steeringAssistActive)
                        {
                            steeringAssistActive = false;
                            smoothedSteerCorrection = 0f;
                            cachedSteerCorrection = 0f;
                            cachedBrakeMagnitude = 0f;
                            previousFrameSteer = 0f;
                            // v3 state cleanup so a re-enable starts fresh.
                            brakeArmed = false;
                            rampedBrakeInput = 0f;
                            ppGoalInitialized = false;
                            try { outBrakeWarn.Stop(); } catch { }
                        }
                    }
                }

                // STUCK-RECOVERY LAYERS 3-4: while the recovery auto-drive is
                // engaged, the !isAutodriving gate above disables the assist —
                // so the monitor runs here instead, handing control back once
                // the car is on a road or teleporting if it cannot progress.
                if (stuckAutodriveEngaged)
                    MonitorStuckAutodrive();

                // ============================================
                // DRIVE ASSIST DEBUG LOGGING
                // Toggled by the driveAssistDebugLog setting. Writes per-frame
                // telemetry for the whole drive-assist feature on a background
                // thread (see DriveAssistLogger) so disk I/O never blocks onTick.
                // ============================================
                bool driveLogEnabled = getSetting("driveAssistDebugLog") == 1;
                if (driveLogEnabled && !driveLogWasEnabled)
                {
                    driveLogger.Start();
                    if (driveLogger.IsRunning)
                    {
                        // Iter-9 Patch F: startup banner + iter-version sentinel.
                        // Banner lines (# prefix) are written exactly once per
                        // log-session so post-hoc analysis tools can instantly
                        // identify which build's patches are active without
                        // grepping for every event line. Bump iter-version when
                        // a new iteration's patches land in the working tree.
                        //
                        // Iter-10 Patch O (2026-05-27): bump iter9 -> iter10.
                        // The original string was never updated when iter-10's
                        // seven patches (A-G) landed, so two iter-10 logs were
                        // misread as iter-9 during analysis. Future iterations
                        // MUST update this string when their patches land.
                        //
                        // Iter-11 Patch P (2026-05-27): bump iter10 -> iter11
                        // and list the iter-11 patch letters (N L J I K H M P).
                        //
                        // Iter-12 Patch R (2026-05-27): bump iter11 -> iter12
                        // and add the iter-12 patch list (Q:street-surface,
                        // R:iter-banner). Standing convention from Patch O / P:
                        // every iteration's last commit bumps this string.
                        driveLogger.Write("# GTA11Y drive-assist log");
                        driveLogger.Write("# Build: iter13 (iter-9: F:enriched G:auto-coll H:reject-why "
                            + "A:lat-brake B:close-spd C:teleport-hook D:elev-static E:dense-pause; "
                            + "iter-10: A:streak-eject B:severe-skew C:offroad-hyst D:skew-curve-brake "
                            + "E:post-tp-hold F:brake-preserve G:thrash-damp O:iter-banner; "
                            + "iter-11: N:auto-coll-tune L:recovery-timeout J:cache-validity-scale "
                            + "I:drove-past-3frame K:brake-mag-blend H:brake-steer-decouple "
                            + "M:asymmetric-ramp P:iter-banner; "
                            + "iter-12: Q:street-surface R:iter-banner; "
                            + "iter-13: S:noprogress-arm S2:relock-fix T:pivot-escape "
                            + "U:recovery-heading-weight V:lane-end-governor W:takeover-ux X:iter-banner)");
                        driveLogger.Write("# vehicleaihandling: " + VehicleAIHandlingRegistry.LoadedFrom);
                        driveLogger.Write("# Started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        driveLogger.Write("[F" + driveLogFrameCount + "] EVENT iter-version: iter13");
                        Tolk.Speak("Drive assist debug logging started", true);
                    }
                }
                else if (!driveLogEnabled && driveLogWasEnabled)
                {
                    driveLogger.Stop();
                    Tolk.Speak("Drive assist debug logging stopped", true);
                }
                driveLogWasEnabled = driveLogEnabled;

                if (driveLogger.IsRunning)
                {
                    Vehicle logVeh = Game.Player.Character.IsInVehicle()
                        ? Game.Player.Character.CurrentVehicle : null;
                    if (logVeh != null || isAutodriving)
                        LogDriveAssistFrame(logVeh);

                    // Iter-9 Patch G: auto-collision detection. Compare chassis
                    // health frame-to-frame; a drop >= AUTO_COLLISION_HEALTH_DROP
                    // while the car is moving and on the ground is treated as a
                    // collision and emits the same snapshot shape as the F1
                    // player marker. The cooldown prevents multi-frame contact
                    // from flooding the log with duplicates.
                    if (logVeh != null && !logVeh.IsInAir)
                    {
                        float curH = logVeh.HealthFloat;
                        bool sameVeh = logVeh.Handle == autoCollisionLastVehHandle;
                        if (sameVeh && autoCollisionLastHealth > 0f)
                        {
                            float drop = autoCollisionLastHealth - curH;
                            long sinceLast = DateTime.Now.Ticks - autoCollisionLastTicks;
                            // Iter-11 Patch N: also tighten speed gate
                            // 1f -> 3f. Below 3 m/s the energy of any impact
                            // is sub-collision (parking-lot tap, low-curb
                            // bump). Both knobs combine: only events that
                            // both crossed the 10f health threshold AND
                            // happened at meaningful speed count.
                            if (drop >= AUTO_COLLISION_HEALTH_DROP
                                && logVeh.Speed > 3f
                                && sinceLast > AUTO_COLLISION_COOLDOWN_TICKS)
                            {
                                LogCollisionSnapshot("AUTO-COLLISION",
                                    "healthDelta=-" + drop.ToString("F1"));
                                autoCollisionLastTicks = DateTime.Now.Ticks;
                            }
                        }
                        autoCollisionLastHealth = curH;
                        autoCollisionLastVehHandle = logVeh.Handle;
                    }
                    else
                    {
                        // On foot or aircraft: reset baseline so re-entering a
                        // vehicle doesn't fire a spurious AUTO-COLLISION from
                        // the old health value.
                        autoCollisionLastHealth = -1f;
                        autoCollisionLastVehHandle = 0;
                    }
                }

                // ============================================
                // WAYPOINT TRACKING SYSTEM
                // When active, plays directional beeps toward waypoint
                // Interval based on distance (closer = faster beeps)
                // Panned left/right based on direction
                // ============================================
                if (waypointTrackingActive)
                {
                    // Check if waypoint still exists
                    bool waypointExists = Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE);

                    if (!waypointExists)
                    {
                        waypointTrackingActive = false;
                        Tolk.Speak("Waypoint cleared", true);
                    }
                    else
                    {
                        // Get waypoint position using native function
                        int waypointBlipHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8); // 8 = waypoint blip type
                        if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, waypointBlipHandle))
                        {
                            GTA.Math.Vector3 waypointPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, waypointBlipHandle);
                            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

                            // Calculate distance (2D, ignoring height)
                            float dx = waypointPos.X - playerPos.X;
                            float dy = waypointPos.Y - playerPos.Y;
                            float distance = (float)Math.Sqrt(dx * dx + dy * dy);

                            // Calculate direction to waypoint relative to player heading
                            GTA.Math.Vector3 forwardVec = Game.Player.Character.ForwardVector;
                            GTA.Math.Vector3 rightVec = new GTA.Math.Vector3(forwardVec.Y, -forwardVec.X, 0);
                            GTA.Math.Vector3 toWaypoint = GTA.Math.Vector3.Normalize(new GTA.Math.Vector3(dx, dy, 0));

                            float dotForward = GTA.Math.Vector3.Dot(forwardVec, toWaypoint);
                            float dotRight = GTA.Math.Vector3.Dot(rightVec, toWaypoint);

                            // Calculate pan (-1 = left, 0 = center, 1 = right)
                            float pan = Math.Max(-1f, Math.Min(1f, dotRight));

                            // Calculate beep interval based on distance
                            // Close (< 50m): 200ms, Far (> 500m): 1000ms
                            float distFactor = Math.Min(distance / 500f, 1f);
                            long beepInterval = (long)(2000000 + (distFactor * 8000000)); // 200ms to 1000ms in ticks

                            if (DateTime.Now.Ticks - waypointBeepTicks > beepInterval)
                            {
                                waypointBeepTicks = DateTime.Now.Ticks;

                                // Check if waypoint is behind (dotForward < -0.5)
                                bool waypointBehind = (dotForward < -0.5f);

                                // Pitch based on whether we're facing toward or away
                                // Facing toward (dotForward > 0): higher pitch (good)
                                // Facing away (dotForward < 0): lower pitch (turn around)
                                // One octave lower if waypoint is behind
                                float baseFreq = 400 + (dotForward * 200); // 200-600 Hz
                                float freq = waypointBehind ? baseFreq / 2f : baseFreq; // 100-300 Hz if behind

                                // Pan: center for behind, left/right for sides
                                float actualPan = waypointBehind ? 0f : pan;

                                outWaypoint.Stop();
                                waypointBeep.Gain = 0.1;
                                waypointBeep.Frequency = freq;
                                waypointBeep.Type = SignalGeneratorType.Sin; // Smooth tone for waypoint

                                var wpSample = waypointBeep.Take(TimeSpan.FromSeconds(0.1));
                                var wpPanned = new PanningSampleProvider(wpSample) { Pan = actualPan };
                                outWaypoint.Init(wpPanned);
                                outWaypoint.Play();
                            }

                            // Check if arrived (within 10 meters)
                            if (distance < 10f)
                            {
                                waypointTrackingActive = false;
                                Tolk.Speak("Arrived at waypoint", true);
                            }
                        }
                    }
                }

                // ============================================
                // MISSION BLIP TRACKING SYSTEM
                // Same principle as waypoint but for mission markers
                // ============================================
                if (missionTrackingActive && trackedBlipHandle != -1)
                {
                    // Check if blip still exists
                    if (!Function.Call<bool>(Hash.DOES_BLIP_EXIST, trackedBlipHandle))
                    {
                        missionTrackingActive = false;
                        trackedBlipHandle = -1;
                        Tolk.Speak("Marker cleared", true);
                    }
                    else
                    {
                        GTA.Math.Vector3 blipPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, trackedBlipHandle);
                        GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

                        float dx = blipPos.X - playerPos.X;
                        float dy = blipPos.Y - playerPos.Y;
                        float distance = (float)Math.Sqrt(dx * dx + dy * dy);

                        // Calculate direction relative to player heading
                        GTA.Math.Vector3 forwardVec = Game.Player.Character.ForwardVector;
                        GTA.Math.Vector3 rightVec = new GTA.Math.Vector3(forwardVec.Y, -forwardVec.X, 0);
                        GTA.Math.Vector3 toBlip = GTA.Math.Vector3.Normalize(new GTA.Math.Vector3(dx, dy, 0));

                        float dotForward = GTA.Math.Vector3.Dot(forwardVec, toBlip);
                        float dotRight = GTA.Math.Vector3.Dot(rightVec, toBlip);

                        // Calculate beep interval based on distance
                        float distFactor = Math.Min(distance / 500f, 1f);
                        long beepInterval = (long)(2000000 + (distFactor * 8000000));

                        if (DateTime.Now.Ticks - missionBeepTicks > beepInterval)
                        {
                            missionBeepTicks = DateTime.Now.Ticks;

                            bool behind = (dotForward < -0.5f);
                            float pan = behind ? 0f : Math.Max(-1f, Math.Min(1f, dotRight));

                            // Use different tone than waypoint (Triangle wave, slightly different pitch)
                            float baseFreq = 350 + (dotForward * 150); // 200-500 Hz
                            float freq = behind ? baseFreq / 2f : baseFreq;

                            outMissionBeep.Stop();
                            missionBeep.Gain = 0.1;
                            missionBeep.Frequency = freq;
                            missionBeep.Type = SignalGeneratorType.Triangle; // Different from waypoint

                            var missionSample = missionBeep.Take(TimeSpan.FromSeconds(0.1));
                            var missionPanned = new PanningSampleProvider(missionSample) { Pan = pan };
                            outMissionBeep.Init(missionPanned);
                            outMissionBeep.Play();
                        }

                        // Check if arrived
                        if (distance < 15f)
                        {
                            missionTrackingActive = false;
                            trackedBlipHandle = -1;
                            Tolk.Speak("Arrived at marker", true);
                        }
                    }
                }

                // ============================================
                // TURN-BY-TURN NAVIGATION SYSTEM
                // Announces turns when driving toward waypoint
                // ============================================
                if (getSetting("turnByTurnNavigation") == 1 && (waypointTrackingActive || missionTrackingActive) && Game.Player.Character.IsInVehicle())
                {
                    Vehicle veh = Game.Player.Character.CurrentVehicle;
                    if (veh != null && veh.Speed > 2f) // Only when moving
                    {
                        // Check based on vehicle speed (faster = more frequent checks)
                        long turnInterval = (long)(10000000 - Math.Min(veh.Speed / 40f, 1f) * 5000000); // 1000ms to 500ms

                        if (DateTime.Now.Ticks - turnNavTicks > turnInterval)
                        {
                            turnNavTicks = DateTime.Now.Ticks;

                            // Get target position (waypoint or mission blip)
                            GTA.Math.Vector3 targetPos = GTA.Math.Vector3.Zero;
                            if (waypointTrackingActive)
                            {
                                int wpHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
                                if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, wpHandle))
                                    targetPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, wpHandle);
                            }
                            else if (missionTrackingActive && trackedBlipHandle != -1)
                            {
                                targetPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, trackedBlipHandle);
                            }

                            if (targetPos != GTA.Math.Vector3.Zero)
                            {
                                GTA.Math.Vector3 playerPos = veh.Position;
                                GTA.Math.Vector3 forwardVec = veh.ForwardVector;
                                GTA.Math.Vector3 rightVec = veh.RightVector;

                                // Calculate direction to target directly
                                // (GPS pathfinding is complex, so we use direct line-of-sight direction)

                                // Calculate direction to target
                                GTA.Math.Vector3 toTarget = targetPos - playerPos;
                                GTA.Math.Vector3 toTargetNorm = GTA.Math.Vector3.Normalize(new GTA.Math.Vector3(toTarget.X, toTarget.Y, 0));

                                float dotForward = GTA.Math.Vector3.Dot(forwardVec, toTargetNorm);
                                float dotRight = GTA.Math.Vector3.Dot(rightVec, toTargetNorm);

                                float distance = toTarget.Length();

                                // Calculate time to arrive (rough estimate based on current speed)
                                float speedMps = veh.Speed; // m/s
                                float timeToArrival = speedMps > 0 ? distance / speedMps : float.MaxValue;

                                // Determine turn direction and urgency
                                string turnAnnouncement = "";

                                // If target is significantly to the left or right
                                if (Math.Abs(dotRight) > 0.3f && dotForward < 0.7f)
                                {
                                    // Calculate approximate distance to turn
                                    float turnDistance = distance * Math.Abs(dotForward);

                                    if (dotRight > 0.5f)
                                    {
                                        if (turnDistance < 50f)
                                            turnAnnouncement = "Turn right now";
                                        else if (turnDistance < 100f)
                                            turnAnnouncement = "Turn right in " + ((int)(turnDistance / 10) * 10) + " meters";
                                        else if (turnDistance < 200f)
                                            turnAnnouncement = "Turn right ahead";
                                    }
                                    else if (dotRight < -0.5f)
                                    {
                                        if (turnDistance < 50f)
                                            turnAnnouncement = "Turn left now";
                                        else if (turnDistance < 100f)
                                            turnAnnouncement = "Turn left in " + ((int)(turnDistance / 10) * 10) + " meters";
                                        else if (turnDistance < 200f)
                                            turnAnnouncement = "Turn left ahead";
                                    }
                                    else if (dotRight > 0.3f)
                                    {
                                        if (turnDistance < 100f)
                                            turnAnnouncement = "Bear right";
                                    }
                                    else if (dotRight < -0.3f)
                                    {
                                        if (turnDistance < 100f)
                                            turnAnnouncement = "Bear left";
                                    }
                                }
                                else if (dotForward < -0.3f)
                                {
                                    // Going wrong way
                                    turnAnnouncement = "Turn around";
                                }
                                else if (dotForward > 0.9f && distance < 100f)
                                {
                                    turnAnnouncement = "Continue straight, " + (int)distance + " meters";
                                }

                                // Only announce if different from last announcement
                                if (!string.IsNullOrEmpty(turnAnnouncement) && turnAnnouncement != lastTurnAnnouncement)
                                {
                                    lastTurnAnnouncement = turnAnnouncement;
                                    lastTurnDistance = distance;
                                    Tolk.Speak(turnAnnouncement, true);
                                }
                                // Reset if we've moved significantly
                                else if (Math.Abs(distance - lastTurnDistance) > 50f)
                                {
                                    lastTurnAnnouncement = "";
                                }
                            }
                        }
                    }
                }

                // ============================================
                // ENEMY DETECTION SYSTEM
                // Proactive detection runs in TickProactiveThreatScan() when bodyguard system is active.
                // Fallback: basic detection when bodyguard system is off.
                // Directional beeps are handled below.
                // ============================================

                if (!bodyguardSystemEnabled)
                {
                    // Basic enemy detection when bodyguard system is off
                    if (DateTime.Now.Ticks - enemyCheckTicks > 20000000) // 2 seconds
                    {
                        enemyCheckTicks = DateTime.Now.Ticks;
                        trackedEnemies.Clear();

                        Ped[] nearbyPeds = World.GetNearbyPeds(Game.Player.Character.Position, 100f);
                        foreach (Ped ped in nearbyPeds)
                        {
                            if (ped == Game.Player.Character || ped.IsDead) continue;
                            if (ped.IsInCombatAgainst(Game.Player.Character))
                                trackedEnemies.Add(ped);
                        }

                        if (trackedEnemies.Count > 0 && trackedEnemies.Count != lastAnnouncedEnemyCount)
                        {
                            Tolk.Speak(trackedEnemies.Count + " hostile" + (trackedEnemies.Count > 1 ? "s" : "") + " detected", true);
                        }
                        lastAnnouncedEnemyCount = trackedEnemies.Count;
                    }
                }

                // Play beeps for tracked enemies (every 300ms per closest enemy)
                if (trackedEnemies.Count > 0 && DateTime.Now.Ticks - enemyBeepTicks > 3000000) // 300ms
                {
                    enemyBeepTicks = DateTime.Now.Ticks;

                    // Remove dead enemies from tracking
                    trackedEnemies.RemoveAll(p => p == null || p.IsDead || !p.Exists());

                    if (trackedEnemies.Count > 0)
                    {
                        // Find closest enemy
                        Ped closestEnemy = null;
                        float closestDist = float.MaxValue;

                        foreach (Ped enemy in trackedEnemies)
                        {
                            if (enemy == null || !enemy.Exists()) continue;
                            float dist = World.GetDistance(Game.Player.Character.Position, enemy.Position);
                            if (dist < closestDist)
                            {
                                closestDist = dist;
                                closestEnemy = enemy;
                            }
                        }

                        if (closestEnemy != null)
                        {
                            // Calculate direction
                            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                            GTA.Math.Vector3 enemyPos = closestEnemy.Position;
                            GTA.Math.Vector3 toEnemy = enemyPos - playerPos;
                            GTA.Math.Vector3 toEnemyNorm = GTA.Math.Vector3.Normalize(toEnemy);

                            GTA.Math.Vector3 forwardVec = Game.Player.Character.ForwardVector;
                            GTA.Math.Vector3 rightVec = new GTA.Math.Vector3(forwardVec.Y, -forwardVec.X, 0);

                            float dotForward = GTA.Math.Vector3.Dot(forwardVec, toEnemyNorm);
                            float dotRight = GTA.Math.Vector3.Dot(rightVec, toEnemyNorm);

                            // Check if enemy is behind (dotForward < -0.5)
                            bool enemyBehind = (dotForward < -0.5f);

                            // Pan: center for behind, left/right for sides
                            float pan = enemyBehind ? 0f : Math.Max(-1f, Math.Min(1f, dotRight));

                            // Frequency based on distance (closer = higher pitch = more urgent)
                            // One octave lower for enemies behind (divide by 2)
                            float distFactor = Math.Min(closestDist / 50f, 1f);
                            float baseFreq = 800 - (distFactor * 400); // 400-800 Hz
                            float freq = enemyBehind ? baseFreq / 2f : baseFreq; // 200-400 Hz if behind

                            outEnemy.Stop();
                            enemyBeep.Gain = 0.12;
                            enemyBeep.Frequency = freq;
                            enemyBeep.Type = SignalGeneratorType.SawTooth; // Harsh tone for enemies

                            var enemySample = enemyBeep.Take(TimeSpan.FromSeconds(0.08));
                            var enemyPanned = new PanningSampleProvider(enemySample) { Pan = pan };
                            outEnemy.Init(enemyPanned);
                            outEnemy.Play();
                        }
                    }
                }

                // ============================================
                // PICKUP DETECTION SYSTEM
                // Detects health packs, armor, weapons within 25m
                // Uses pickup.wav with directional panning similar to nav assist
                // ============================================
                if (getSetting("pickupDetection") == 1 && pickupSound != null)
                {
                    // Check every 750ms for pickups
                    if (DateTime.Now.Ticks - pickupCheckTicks > 7500000)
                    {
                        pickupCheckTicks = DateTime.Now.Ticks;

                        GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                        GTA.Math.Vector3 closestPickupPos = GTA.Math.Vector3.Zero;
                        float closestDist = float.MaxValue;
                        string foundPickupType = "";

                        // Common pickup type hashes in GTA V
                        // Health: PICKUP_HEALTH, PICKUP_HEALTH_SNACK, etc.
                        // Armor: PICKUP_ARMOUR_STANDARD, PICKUP_ARMOUR_STANDARD_MP
                        // Weapons: Various PICKUP_WEAPON_* types
                        // Money: PICKUP_MONEY_CASE, PICKUP_MONEY_VARIABLE, etc.
                        uint[] pickupTypes = {
                                0x8F707C18, // PICKUP_HEALTH
								0x483577E8, // PICKUP_HEALTH_SNACK
								0x4BFB42D1, // PICKUP_ARMOUR_STANDARD
								0xCE6B5C74, // PICKUP_MONEY_CASE
								0xCB83E6B1, // PICKUP_MONEY_VARIABLE
								0xE175C698, // PICKUP_MONEY_MED_BAG
								0x1E9A99F8, // PICKUP_MONEY_PAPER_BAG
								0x3F038C30, // PICKUP_MONEY_PURSE
								0xDE78F17E, // PICKUP_MONEY_SECURITY_CASE
								0xBFCBBF17, // PICKUP_WEAPON_PISTOL
								0x1B06D571, // PICKUP_WEAPON_COMBATPISTOL
								0x5EF9FEC4, // PICKUP_WEAPON_SMG
								0xB1415C0E, // PICKUP_WEAPON_MICROSMG
								0x7F7497E5, // PICKUP_WEAPON_ASSAULTRIFLE
								0xF33C83B0, // PICKUP_WEAPON_SHOTGUN
								0x23C1D0A3, // PICKUP_WEAPON_SNIPERRIFLE
								0xC637F23B, // PICKUP_WEAPON_RPG
								0x9C6F0B5C, // PICKUP_WEAPON_GRENADE
								0x2C014CA6, // PICKUP_WEAPON_KNIFE
								0x6C5B941A, // PICKUP_WEAPON_MINIGUN
								0x693583AD, // PICKUP_AMMO_PISTOL
								0x14AAA644, // PICKUP_AMMO_SMG
								0xE4E2B027  // PICKUP_AMMO_RIFLE
							};

                        string[] pickupNames = {
                                "Health", "Health Snack", "Armor",
                                "Money", "Money", "Money Bag", "Money Bag", "Purse", "Security Case",
                                "Pistol", "Combat Pistol", "SMG", "Micro SMG", "Assault Rifle",
                                "Shotgun", "Sniper Rifle", "RPG", "Grenade", "Knife", "Minigun",
                                "Pistol Ammo", "SMG Ammo", "Rifle Ammo"
                            };

                        // Check for each pickup type
                        for (int i = 0; i < pickupTypes.Length; i++)
                        {
                            // Use GET_PICKUP_COORDS to find pickups of this type within range
                            GTA.Math.Vector3 pickupPos = Function.Call<GTA.Math.Vector3>(Hash.GET_PICKUP_COORDS, (int)pickupTypes[i]);

                            if (pickupPos != GTA.Math.Vector3.Zero)
                            {
                                float dist = World.GetDistance(playerPos, pickupPos);
                                if (dist < 25f && dist < closestDist)
                                {
                                    closestDist = dist;
                                    closestPickupPos = pickupPos;
                                    foundPickupType = pickupNames[i];
                                }
                            }
                        }

                        // Also try checking all nearby objects with IS_ANY_PICKUP_OBJECT_NEAR_POINT
                        if (closestPickupPos == GTA.Math.Vector3.Zero)
                        {
                            // Scan nearby props and check if they're pickups
                            Prop[] nearbyProps = World.GetNearbyProps(playerPos, 25f);
                            foreach (Prop prop in nearbyProps)
                            {
                                if (prop == null || !prop.Exists()) continue;

                                // Check if prop model contains pickup-related strings
                                if (hashes.ContainsKey(prop.Model.NativeValue.ToString()))
                                {
                                    string modelName = hashes[prop.Model.NativeValue.ToString()].ToLower();
                                    if (modelName.Contains("pickup") || modelName.Contains("health") ||
                                        modelName.Contains("armour") || modelName.Contains("armor") ||
                                        modelName.Contains("money") || modelName.Contains("briefcase"))
                                    {
                                        float dist = World.GetDistance(playerPos, prop.Position);
                                        if (dist < closestDist)
                                        {
                                            closestDist = dist;
                                            closestPickupPos = prop.Position;
                                            foundPickupType = "Item";
                                        }
                                    }
                                }
                            }
                        }

                        // If pickup found, play directional sound
                        if (closestPickupPos != GTA.Math.Vector3.Zero)
                        {
                            // Check if it's a new pickup (different position)
                            if (World.GetDistance(closestPickupPos, lastPickupPos) > 2f)
                            {
                                // Announce new pickup
                                Tolk.Speak(foundPickupType + ", " + (int)closestDist + " meters", true);
                                lastPickupPos = closestPickupPos;
                                trackedPickupType = foundPickupType;
                                pickupTrackingActive = true;
                            }
                        }
                        else
                        {
                            pickupTrackingActive = false;
                        }
                    }

                    // Play directional beep toward tracked pickup (like nav assist)
                    if (pickupTrackingActive && lastPickupPos != GTA.Math.Vector3.Zero)
                    {
                        // Beep every 400ms
                        if (DateTime.Now.Ticks - pickupBeepTicks > 4000000)
                        {
                            pickupBeepTicks = DateTime.Now.Ticks;

                            float dist = World.GetDistance(Game.Player.Character.Position, lastPickupPos);

                            // Check if still in range
                            if (dist > 25f)
                            {
                                pickupTrackingActive = false;
                                lastPickupPos = GTA.Math.Vector3.Zero;
                            }
                            else if (dist < 2f)
                            {
                                // Arrived at pickup
                                pickupTrackingActive = false;
                                lastPickupPos = GTA.Math.Vector3.Zero;
                                Tolk.Speak("Pickup nearby", true);
                            }
                            else
                            {
                                // Calculate pan direction
                                GTA.Math.Vector3 toPickup = lastPickupPos - Game.Player.Character.Position;
                                GTA.Math.Vector3 toPickupNorm = GTA.Math.Vector3.Normalize(toPickup);
                                GTA.Math.Vector3 forwardVec = Game.Player.Character.ForwardVector;
                                GTA.Math.Vector3 rightVec = new GTA.Math.Vector3(forwardVec.Y, -forwardVec.X, 0);

                                float dotForward = GTA.Math.Vector3.Dot(forwardVec, toPickupNorm);
                                float dotRight = GTA.Math.Vector3.Dot(rightVec, toPickupNorm);
                                bool behind = (dotForward < -0.5f);
                                float pan = behind ? 0f : Math.Max(-1f, Math.Min(1f, dotRight));

                                // Play panned pickup sound
                                outPickup.Stop();
                                pickupSound.Position = 0;
                                var pickupSample = pickupSound.ToSampleProvider();
                                if (pickupSample.WaveFormat.Channels == 1)
                                {
                                    var pickupPanned = new PanningSampleProvider(pickupSample) { Pan = pan };
                                    outPickup.Init(pickupPanned);
                                }
                                else
                                {
                                    outPickup.Init(pickupSound);
                                }
                                outPickup.Play();
                            }
                        }
                    }
                }

                // ============================================
                // WATER/HAZARD DETECTION SYSTEM
                // Low bass rumble (80-100Hz) for water
                // Smooth descending tone for dropoffs
                // ============================================
                if (getSetting("waterHazardDetection") == 1)
                {
                    bool inVeh = Game.Player.Character.IsInVehicle();
                    float vehSpeed = inVeh && Game.Player.Character.CurrentVehicle != null ? Game.Player.Character.CurrentVehicle.Speed : 0f;

                    // Interval: 1s on foot, 750ms slow, faster as speed increases
                    long waterInterval = 10000000; // 1 second default
                    if (inVeh)
                    {
                        if (vehSpeed < 10f) waterInterval = 7500000; // 750ms slow
                        else waterInterval = (long)(7500000 - Math.Min(vehSpeed / 50f, 1f) * 5000000); // down to 250ms
                    }

                    if (DateTime.Now.Ticks - waterCheckTicks > waterInterval)
                    {
                        waterCheckTicks = DateTime.Now.Ticks;

                        GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                        GTA.Math.Vector3 forwardVec = inVeh && Game.Player.Character.CurrentVehicle != null
                            ? Game.Player.Character.CurrentVehicle.ForwardVector
                            : Game.Player.Character.ForwardVector;

                        // Check ahead for water (10-30m depending on speed)
                        float checkDist = inVeh ? Math.Min(10f + vehSpeed * 0.5f, 30f) : 10f;
                        GTA.Math.Vector3 checkPos = playerPos + (forwardVec * checkDist);

                        // Get ground height at check position
                        float groundZ = World.GetGroundHeight(checkPos);

                        // Check if position is over water
                        bool isWater = (groundZ < 1f && checkPos.Z > groundZ + 5f); // Water if no ground found below

                        if (isWater && !wasNearWater)
                        {
                            // Play low bass rumble
                            outWater.Stop();
                            waterRumble.Frequency = 90; // 80-100 Hz
                            waterRumble.Gain = 0.2;
                            waterRumble.Type = SignalGeneratorType.Sin;
                            var waterSample = waterRumble.Take(TimeSpan.FromSeconds(0.5));
                            outWater.Init(waterSample);
                            outWater.Play();
                            wasNearWater = true;
                        }
                        else if (!isWater)
                        {
                            wasNearWater = false;
                        }

                        // Check for dropoffs (large height difference)
                        float currentGround = World.GetGroundHeight(playerPos);

                        // FIX: Increased threshold to 5m to avoid false positives from road variations
                        // In vehicles at speed, use slightly lower threshold (4m) since we need more warning time
                        float dropThreshold = (inVeh && vehSpeed > 15f) ? 4f : 5f;

                        if (groundZ < currentGround - dropThreshold)
                        {
                            // FIX: Check for wall between player and dropoff using raycast
                            // If there's a wall, the dropoff is behind it and not a real hazard
                            float rayHeight = inVeh ? 0.5f : 1.0f;
                            GTA.Math.Vector3 rayStart = playerPos + new GTA.Math.Vector3(0, 0, rayHeight);
                            GTA.Math.Vector3 rayEnd = checkPos + new GTA.Math.Vector3(0, 0, rayHeight);

                            RaycastResult wallCheck = World.Raycast(rayStart, rayEnd, IntersectFlags.Map, Game.Player.Character);

                            // Only warn if NO wall between us and the dropoff
                            // Allow some tolerance - wall must be within 80% of check distance to block warning
                            bool wallBlocking = false;
                            if (wallCheck.DidHit)
                            {
                                float wallDist = World.GetDistance(rayStart, wallCheck.HitPosition);
                                if (wallDist < checkDist * 0.8f)
                                {
                                    wallBlocking = true;
                                }
                            }

                            if (!wallBlocking)
                            {
                                // Calculate panning based on direction to drop
                                GTA.Math.Vector3 toCheck = checkPos - playerPos;
                                GTA.Math.Vector3 toCheckNorm = GTA.Math.Vector3.Normalize(new GTA.Math.Vector3(toCheck.X, toCheck.Y, 0));
                                GTA.Math.Vector3 rightVec = inVeh && Game.Player.Character.CurrentVehicle != null
                                    ? Game.Player.Character.CurrentVehicle.RightVector
                                    : new GTA.Math.Vector3(forwardVec.Y, -forwardVec.X, 0);
                                float dropPan = Math.Max(-1f, Math.Min(1f, GTA.Math.Vector3.Dot(rightVec, toCheckNorm)));

                                // Play panned descending tone
                                outDropoff.Stop();
                                dropoffTone.Frequency = 600;
                                dropoffTone.Gain = 0.12;
                                dropoffTone.Type = SignalGeneratorType.Sin;
                                var dropSample = dropoffTone.Take(TimeSpan.FromSeconds(0.15));
                                var dropPanned = new PanningSampleProvider(dropSample) { Pan = dropPan };
                                outDropoff.Init(dropPanned);
                                outDropoff.Play();

                                // Speak warning for larger drops (increased threshold to 15m for speech)
                                float dropHeight = currentGround - groundZ;
                                if (dropHeight > 15f)
                                {
                                    Tolk.Speak("Drop ahead", true);
                                }
                            }
                        }
                    }
                }

                // ============================================
                // VEHICLE HEALTH FEEDBACK (Speech)
                // Announces damage at 75%, 50%, 25%, 10%
                // ============================================
                if (getSetting("vehicleHealthFeedback") == 1 && Game.Player.Character.IsInVehicle())
                {
                    if (DateTime.Now.Ticks - vehicleHealthCheckTicks > 5000000) // 500ms
                    {
                        vehicleHealthCheckTicks = DateTime.Now.Ticks;
                        Vehicle veh = Game.Player.Character.CurrentVehicle;

                        if (veh != null)
                        {
                            // Get health values (0-1000 scale typically)
                            float bodyHealth = veh.BodyHealth / 10f; // Convert to percentage
                            float engineHealth = veh.EngineHealth / 10f;
                            float petrolHealth = veh.PetrolTankHealth / 10f;

                            // Reset warnings when entering new vehicle
                            if (veh.BodyHealth > lastVehicleBodyHealth + 100)
                            {
                                for (int i = 0; i < 4; i++) { bodyHealthWarnings[i] = false; engineHealthWarnings[i] = false; petrolHealthWarnings[i] = false; }
                            }

                            // Check body health thresholds
                            if (bodyHealth <= 75 && bodyHealth > 50 && !bodyHealthWarnings[0]) { Tolk.Speak("Body 75 percent"); bodyHealthWarnings[0] = true; }
                            else if (bodyHealth <= 50 && bodyHealth > 25 && !bodyHealthWarnings[1]) { Tolk.Speak("Body 50 percent"); bodyHealthWarnings[1] = true; }
                            else if (bodyHealth <= 25 && bodyHealth > 10 && !bodyHealthWarnings[2]) { Tolk.Speak("Body critical, 25 percent"); bodyHealthWarnings[2] = true; }
                            else if (bodyHealth <= 10 && !bodyHealthWarnings[3]) { Tolk.Speak("Body failing, 10 percent!"); bodyHealthWarnings[3] = true; }

                            // Check engine health
                            if (engineHealth <= 75 && engineHealth > 50 && !engineHealthWarnings[0]) { Tolk.Speak("Engine 75 percent"); engineHealthWarnings[0] = true; }
                            else if (engineHealth <= 50 && engineHealth > 25 && !engineHealthWarnings[1]) { Tolk.Speak("Engine 50 percent"); engineHealthWarnings[1] = true; }
                            else if (engineHealth <= 25 && engineHealth > 10 && !engineHealthWarnings[2]) { Tolk.Speak("Engine critical, 25 percent"); engineHealthWarnings[2] = true; }
                            else if (engineHealth <= 10 && !engineHealthWarnings[3]) { Tolk.Speak("Engine failing, 10 percent!"); engineHealthWarnings[3] = true; }

                            // Check petrol tank
                            if (petrolHealth <= 50 && petrolHealth > 25 && !petrolHealthWarnings[1]) { Tolk.Speak("Fuel tank damaged"); petrolHealthWarnings[1] = true; }
                            else if (petrolHealth <= 25 && petrolHealth > 10 && !petrolHealthWarnings[2]) { Tolk.Speak("Fuel tank leaking!"); petrolHealthWarnings[2] = true; }
                            else if (petrolHealth <= 10 && !petrolHealthWarnings[3]) { Tolk.Speak("Fuel tank critical!"); petrolHealthWarnings[3] = true; }

                            lastVehicleBodyHealth = veh.BodyHealth;
                            lastVehicleEngineHealth = veh.EngineHealth;
                            lastVehiclePetrolTankHealth = veh.PetrolTankHealth;
                        }
                    }
                }
                else if (!Game.Player.Character.IsInVehicle())
                {
                    // Reset warnings when exiting vehicle
                    for (int i = 0; i < 4; i++) { bodyHealthWarnings[i] = false; engineHealthWarnings[i] = false; petrolHealthWarnings[i] = false; }
                }

                // ============================================
                // STAMINA/SPRINT FEEDBACK
                // Spoken warning when stamina low
                // ============================================
                if (getSetting("staminaFeedback") == 1 && !Game.Player.Character.IsInVehicle())
                {
                    if (DateTime.Now.Ticks - staminaCheckTicks > 5000000) // 500ms
                    {
                        staminaCheckTicks = DateTime.Now.Ticks;

                        // Get stamina (0-100)
                        float stamina = Function.Call<float>(Hash.GET_PLAYER_SPRINT_STAMINA_REMAINING, Game.Player);

                        if (stamina < 20f && !staminaWarningGiven && Game.Player.Character.IsSprinting)
                        {
                            Tolk.Speak("Low stamina", true);
                            staminaWarningGiven = true;
                        }
                        else if (stamina > 50f)
                        {
                            staminaWarningGiven = false;
                        }
                    }
                }

                // ============================================
                // TRAFFIC AWARENESS
                // Spoken warnings for fast-approaching vehicles
                // ============================================
                if (getSetting("trafficAwareness") == 1 && Game.Player.Character.IsInVehicle())
                {
                    if (DateTime.Now.Ticks - trafficCheckTicks > 5000000) // 500ms
                    {
                        trafficCheckTicks = DateTime.Now.Ticks;

                        Vehicle playerVeh = Game.Player.Character.CurrentVehicle;
                        if (playerVeh != null)
                        {
                            Vehicle[] nearbyVehs = World.GetNearbyVehicles(playerVeh.Position, 50f);

                            foreach (Vehicle veh in nearbyVehs)
                            {
                                if (veh == playerVeh || veh == lastWarnedVehicle) continue;

                                // Check if vehicle is approaching fast
                                float vehSpeed = veh.Speed * 2.236856f; // Convert to MPH
                                if (vehSpeed < 30f) continue; // Only warn for fast vehicles

                                // Check if approaching from sides or behind
                                GTA.Math.Vector3 toVeh = veh.Position - playerVeh.Position;
                                GTA.Math.Vector3 toVehNorm = GTA.Math.Vector3.Normalize(toVeh);
                                GTA.Math.Vector3 vehVelocityNorm = GTA.Math.Vector3.Normalize(veh.Velocity);

                                // Check if vehicle is heading toward us
                                float dotApproach = GTA.Math.Vector3.Dot(vehVelocityNorm, GTA.Math.Vector3.Normalize(-toVeh));
                                if (dotApproach < 0.5f) continue; // Not heading toward us

                                // Calculate direction
                                GTA.Math.Vector3 rightVec = playerVeh.RightVector;
                                GTA.Math.Vector3 forwardVec = playerVeh.ForwardVector;

                                float dotRight = GTA.Math.Vector3.Dot(rightVec, toVehNorm);
                                float dotForward = GTA.Math.Vector3.Dot(forwardVec, toVehNorm);

                                string direction = "";
                                if (dotForward < -0.5f) direction = "behind";
                                else if (dotRight > 0.5f) direction = "right";
                                else if (dotRight < -0.5f) direction = "left";
                                else continue; // In front, less urgent

                                Tolk.Speak("Vehicle approaching from " + direction, true);
                                lastWarnedVehicle = veh;
                                break;
                            }
                        }
                    }
                }

                // ============================================
                // WANTED LEVEL DETAILS
                // Police positions, helicopter, cop count
                // ============================================
                if (getSetting("wantedLevelDetails") == 1 && Game.Player.WantedLevel > 0)
                {
                    if (DateTime.Now.Ticks - wantedDetailsTicks > 50000000) // 5 seconds
                    {
                        wantedDetailsTicks = DateTime.Now.Ticks;

                        // Count nearby cops
                        Ped[] nearbyPeds = World.GetNearbyPeds(Game.Player.Character.Position, 100f);
                        int copCount = 0;

                        foreach (Ped ped in nearbyPeds)
                        {
                            if (ped == null || ped.IsDead) continue;
                            if (Function.Call<int>(Hash.GET_PED_TYPE, ped) == 6) copCount++;
                        }

                        // Check for police helicopter
                        bool heliPresent = false;
                        Vehicle[] nearbyVehs = World.GetNearbyVehicles(Game.Player.Character.Position, 200f);
                        foreach (Vehicle veh in nearbyVehs)
                        {
                            if (veh != null && (veh.Model.Hash == (int)VehicleHash.Polmav))
                            {
                                heliPresent = true;
                                break;
                            }
                        }

                        string msg = copCount + " cops nearby";
                        if (heliPresent) msg += ", helicopter overhead";
                        Tolk.Speak(msg);
                    }
                }

                // ============================================
                // SLOPE/TERRAIN FEEDBACK
                // Spoken notification for drastic slope changes
                // ============================================
                if (getSetting("slopeTerrainFeedback") == 1 && !Game.Player.Character.IsInVehicle())
                {
                    if (DateTime.Now.Ticks - slopeCheckTicks > 10000000) // 1 second
                    {
                        slopeCheckTicks = DateTime.Now.Ticks;

                        // Get ground normal to determine slope
                        GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                        GTA.Math.Vector3 forwardVec = Game.Player.Character.ForwardVector;

                        // Check height difference ahead vs behind
                        float heightAhead = World.GetGroundHeight(playerPos + (forwardVec * 3f));
                        float heightBehind = World.GetGroundHeight(playerPos - (forwardVec * 3f));
                        float currentHeight = World.GetGroundHeight(playerPos);

                        // Calculate slope (height diff over distance)
                        float slope = (heightAhead - currentHeight) / 3f;
                        float slopeAngle = (float)Math.Atan(slope) * 57.2958f; // Convert to degrees

                        // Only announce drastic changes (> 15 degrees difference)
                        if (Math.Abs(slopeAngle - lastGroundSlope) > 15f)
                        {
                            if (slopeAngle > 20f) Tolk.Speak("Steep uphill", true);
                            else if (slopeAngle < -20f) Tolk.Speak("Steep downhill", true);
                            else if (Math.Abs(slopeAngle) < 5f && Math.Abs(lastGroundSlope) > 15f) Tolk.Speak("Level ground", true);
                        }

                        lastGroundSlope = slopeAngle;
                    }
                }

                // ============================================
                // INTERACTABLE OBJECT DETECTION
                // Spoken alert + directional audio for stores, ATMs, etc.
                // ============================================
                if (getSetting("interactableDetection") == 1)
                {
                    if (DateTime.Now.Ticks - interactCheckTicks > 10000000) // 1 second
                    {
                        interactCheckTicks = DateTime.Now.Ticks;

                        // Check for nearby interactable blips (stores, missions, etc.)
                        // Blip types: 52 = shop, 207 = barber, 73 = mod shop, etc.
                        int[] interactBlips = { 52, 207, 73, 277, 108, 110, 136, 137, 280 };

                        foreach (int blipType in interactBlips)
                        {
                            int blipHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, blipType);
                            while (Function.Call<bool>(Hash.DOES_BLIP_EXIST, blipHandle))
                            {
                                GTA.Math.Vector3 blipPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blipHandle);
                                float dist = World.GetDistance(Game.Player.Character.Position, blipPos);

                                if (dist < 20f)
                                {
                                    // Determine type name
                                    string typeName = "Interactable";
                                    if (blipType == 52) typeName = "Shop";
                                    else if (blipType == 207) typeName = "Barber";
                                    else if (blipType == 73) typeName = "Mod Shop";
                                    else if (blipType == 277) typeName = "Clothes Store";
                                    else if (blipType == 108) typeName = "Tattoo Parlor";
                                    else if (blipType == 110) typeName = "Cinema";
                                    else if (blipType == 136 || blipType == 137) typeName = "Mission";

                                    Tolk.Speak(typeName + ", " + (int)dist + " meters", true);

                                    // Play directional sound if available
                                    if (interactSound != null)
                                    {
                                        GTA.Math.Vector3 toBlip = blipPos - Game.Player.Character.Position;
                                        GTA.Math.Vector3 toBlipNorm = GTA.Math.Vector3.Normalize(toBlip);
                                        GTA.Math.Vector3 forwardVec = Game.Player.Character.ForwardVector;
                                        GTA.Math.Vector3 rightVec = new GTA.Math.Vector3(forwardVec.Y, -forwardVec.X, 0);
                                        float pan = Math.Max(-1f, Math.Min(1f, GTA.Math.Vector3.Dot(rightVec, toBlipNorm)));

                                        outInteract.Stop();
                                        interactSound.Position = 0;
                                        outInteract.Init(interactSound);
                                        outInteract.Play();
                                    }
                                    break;
                                }

                                blipHandle = Function.Call<int>(Hash.GET_NEXT_BLIP_INFO_ID, blipType);
                            }
                        }
                    }
                }

                // ============================================
                // COVER DETECTION (Combat)
                // Uses cover.wav with directional panning
                // ============================================
                if (getSetting("coverDetection") == 1 && coverSound != null && trackedEnemies.Count > 0)
                {
                    if (DateTime.Now.Ticks - coverCheckTicks > 20000000) // 2 seconds
                    {
                        coverCheckTicks = DateTime.Now.Ticks;

                        // Find nearby cover points using raycasts
                        GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                        GTA.Math.Vector3 forwardVec = Game.Player.Character.ForwardVector;
                        GTA.Math.Vector3 rightVec = new GTA.Math.Vector3(forwardVec.Y, -forwardVec.X, 0);

                        // Check 8 directions for cover
                        GTA.Math.Vector3[] directions = {
                                forwardVec, -forwardVec, rightVec, -rightVec,
                                GTA.Math.Vector3.Normalize(forwardVec + rightVec),
                                GTA.Math.Vector3.Normalize(forwardVec - rightVec),
                                GTA.Math.Vector3.Normalize(-forwardVec + rightVec),
                                GTA.Math.Vector3.Normalize(-forwardVec - rightVec)
                            };

                        GTA.Math.Vector3 bestCover = GTA.Math.Vector3.Zero;
                        float bestCoverDist = float.MaxValue;

                        foreach (GTA.Math.Vector3 dir in directions)
                        {
                            RaycastResult ray = World.Raycast(playerPos + new GTA.Math.Vector3(0, 0, 0.5f),
                                playerPos + new GTA.Math.Vector3(0, 0, 0.5f) + (dir * 10f),
                                IntersectFlags.Map, Game.Player.Character);

                            if (ray.DidHit)
                            {
                                float dist = World.GetDistance(playerPos, ray.HitPosition);
                                if (dist > 2f && dist < bestCoverDist)
                                {
                                    bestCoverDist = dist;
                                    bestCover = ray.HitPosition;
                                }
                            }
                        }

                        if (bestCover != GTA.Math.Vector3.Zero && bestCoverDist < 8f)
                        {
                            // Calculate pan direction
                            GTA.Math.Vector3 toCover = bestCover - playerPos;
                            GTA.Math.Vector3 toCoverNorm = GTA.Math.Vector3.Normalize(toCover);
                            float pan = Math.Max(-1f, Math.Min(1f, GTA.Math.Vector3.Dot(rightVec, toCoverNorm)));

                            outCover.Stop();
                            coverSound.Position = 0;
                            outCover.Init(coverSound);
                            outCover.Play();
                        }
                    }
                }

                // ============================================
                // BATCH 2 FEATURES - Combat Feedback
                // ============================================

                // Combat Hit/Headshot/Kill Detection (50ms interval)
                if (DateTime.Now.Ticks - combatCheckTicks > 500000) // 50ms
                {
                    combatCheckTicks = DateTime.Now.Ticks;

                    // Get all nearby peds
                    Ped[] nearbyPeds = World.GetNearbyPeds(Game.Player.Character.Position, 50f);
                    List<int> currentPedHandles = new List<int>();

                    foreach (Ped ped in nearbyPeds)
                    {
                        if (ped == null || ped == Game.Player.Character) continue;

                        int handle = ped.Handle;
                        currentPedHandles.Add(handle);
                        int currentHealth = ped.Health;

                        // Check if we're tracking this ped
                        if (pedHealthTracker.ContainsKey(handle))
                        {
                            int previousHealth = pedHealthTracker[handle];

                            // Check if health decreased (we hit them)
                            if (currentHealth < previousHealth)
                            {
                                // Verify we damaged them
                                if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, ped, Game.Player.Character, true))
                                {
                                    if (currentHealth <= 0)
                                    {
                                        // Kill!
                                        if (killSound != null && outKill != null)
                                        {
                                            outKill.Stop();
                                            killSound.Position = 0;
                                            outKill.Init(killSound);
                                            outKill.Play();
                                        }
                                    }
                                    else
                                    {
                                        // Check for headshot using native function to get last damaged bone
                                        OutputArgument boneArg = new OutputArgument();
                                        Function.Call(Hash.GET_PED_LAST_DAMAGE_BONE, ped, boneArg);
                                        int lastBone = boneArg.GetResult<int>();
                                        // Head bone IDs: 31086 (SKEL_Head), 12844 (IK_Head)
                                        bool isHeadshot = (lastBone == 31086 || lastBone == 12844);

                                        if (isHeadshot && headshotSound != null && outHeadshot != null)
                                        {
                                            outHeadshot.Stop();
                                            headshotSound.Position = 0;
                                            outHeadshot.Init(headshotSound);
                                            outHeadshot.Play();
                                        }
                                        else if (hitSound != null && outHit != null)
                                        {
                                            outHit.Stop();
                                            hitSound.Position = 0;
                                            outHit.Init(hitSound);
                                            outHit.Play();
                                        }
                                    }
                                }
                            }

                            // Update health
                            pedHealthTracker[handle] = currentHealth;
                        }
                        else
                        {
                            // Start tracking this ped
                            pedHealthTracker[handle] = currentHealth;
                        }
                    }

                    // Clean up dead/distant peds from tracker
                    List<int> toRemove = new List<int>();
                    foreach (int handle in pedHealthTracker.Keys)
                    {
                        if (!currentPedHandles.Contains(handle))
                            toRemove.Add(handle);
                    }
                    foreach (int handle in toRemove)
                    {
                        pedHealthTracker.Remove(handle);
                    }
                }

                // ============================================
                // BATCH 2 FEATURES - Vehicle Entry Detection
                // ============================================
                bool currentlyInVehicle = Game.Player.Character.IsInVehicle();
                if (currentlyInVehicle && !wasInVehicle)
                {
                    // Just entered a vehicle
                    Vehicle veh = Game.Player.Character.CurrentVehicle;
                    if (veh != null && veh != lastEnteredVehicle)
                    {
                        string vehName = veh.LocalizedName;
                        if (string.IsNullOrEmpty(vehName) || vehName == "NULL")
                            vehName = veh.DisplayName;
                        Tolk.Speak("Entering " + vehName, true);
                        lastEnteredVehicle = veh;
                    }
                }
                wasInVehicle = currentlyInVehicle;

                // Passenger Count Detection (2s interval, on foot only)
                if (!currentlyInVehicle && DateTime.Now.Ticks - passengerCheckTicks > 20000000) // 2 seconds
                {
                    passengerCheckTicks = DateTime.Now.Ticks;

                    Vehicle[] nearbyVehs = World.GetNearbyVehicles(Game.Player.Character.Position, 5f);
                    Vehicle closestOccupied = null;
                    float closestDist = float.MaxValue;
                    int occupantCount = 0;

                    foreach (Vehicle veh in nearbyVehs)
                    {
                        if (veh == null) continue;

                        // Count occupants
                        int count = 0;
                        // Check driver seat (-1) and passenger seats (0 to PassengerCapacity-1)
                        if (veh.Driver != null && veh.Driver.IsAlive) count++;
                        for (int seat = 0; seat < veh.PassengerCapacity; seat++)
                        {
                            Ped passenger = veh.GetPedOnSeat((VehicleSeat)seat);
                            if (passenger != null && passenger.IsAlive) count++;
                        }

                        if (count > 0)
                        {
                            float dist = World.GetDistance(Game.Player.Character.Position, veh.Position);
                            if (dist < closestDist)
                            {
                                closestDist = dist;
                                closestOccupied = veh;
                                occupantCount = count;
                            }
                        }
                    }

                    if (closestOccupied != null && occupantCount > 0)
                    {
                        string vehName = closestOccupied.LocalizedName;
                        if (string.IsNullOrEmpty(vehName) || vehName == "NULL")
                            vehName = closestOccupied.DisplayName;
                        Tolk.Speak(vehName + " with " + occupantCount + " occupant" + (occupantCount > 1 ? "s" : ""), true);
                    }
                }

                // ============================================
                // BATCH 2 FEATURES - Indoor/Outdoor Detection
                // ============================================
                if (DateTime.Now.Ticks - indoorCheckTicks > 10000000) // 1 second
                {
                    indoorCheckTicks = DateTime.Now.Ticks;

                    int interior = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, Game.Player.Character);
                    bool isIndoors = (interior != 0);

                    if (isIndoors && !wasIndoors)
                    {
                        Tolk.Speak("Indoors", true);
                    }
                    else if (!isIndoors && wasIndoors)
                    {
                        Tolk.Speak("Outdoors", true);
                    }
                    wasIndoors = isIndoors;
                }

                // ============================================
                // BATCH 2 FEATURES - Swimming Depth Detection
                // ============================================
                if (DateTime.Now.Ticks - swimCheckTicks > 20000000) // 2 seconds
                {
                    swimCheckTicks = DateTime.Now.Ticks;

                    bool isSwimming = Game.Player.Character.IsSwimming || Game.Player.Character.IsSwimmingUnderWater;

                    if (isSwimming && Game.Player.Character.IsSwimmingUnderWater)
                    {
                        // Calculate depth
                        GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                        float waterSurfaceZ = 0f;
                        bool gotWaterHeight = Function.Call<bool>(Hash.GET_WATER_HEIGHT, playerPos.X, playerPos.Y, playerPos.Z + 50f, (OutputArgument)(new OutputArgument()));

                        // Use output argument to get water height
                        OutputArgument waterHeightArg = new OutputArgument();
                        if (Function.Call<bool>(Hash.GET_WATER_HEIGHT, playerPos.X, playerPos.Y, playerPos.Z + 50f, waterHeightArg))
                        {
                            waterSurfaceZ = waterHeightArg.GetResult<float>();
                            float depth = waterSurfaceZ - playerPos.Z;
                            if (depth > 0)
                            {
                                int depthMeters = (int)Math.Round(depth);
                                if (!wasSwimming || depthMeters > 1)
                                {
                                    Tolk.Speak(depthMeters + " meters deep", true);
                                }
                            }
                        }
                    }
                    wasSwimming = isSwimming;
                }

                // ============================================
                // BATCH 2 FEATURES - Door/Ladder Audio Detection
                // ============================================
                if (DateTime.Now.Ticks - doorLadderCheckTicks > 7500000) // 750ms
                {
                    doorLadderCheckTicks = DateTime.Now.Ticks;
                    float scanRadius = Math.Min(GetDetectionRadius(), 15f);

                    Prop[] props = World.GetNearbyProps(Game.Player.Character.Position, scanRadius);
                    GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                    GTA.Math.Vector3 forwardVec = Game.Player.Character.ForwardVector;
                    GTA.Math.Vector3 rightVec = new GTA.Math.Vector3(forwardVec.Y, -forwardVec.X, 0);

                    foreach (Prop prop in props)
                    {
                        if (prop == null || !prop.IsVisible) continue;

                        float dist = World.GetDistance(playerPos, prop.Position);
                        if (dist > 5f) continue; // Only within 5m for audio

                        // Check if it's a door or ladder
                        string modelName = "";
                        if (hashes.ContainsKey(prop.Model.NativeValue.ToString()))
                            modelName = hashes[prop.Model.NativeValue.ToString()].ToLower();

                        bool isDoor = modelName.Contains("door") || modelName.Contains("gate");
                        bool isLadder = modelName.Contains("ladder");

                        if (isDoor && prop != lastAnnouncedDoor && doorSound != null && outDoor != null)
                        {
                            // Calculate pan
                            GTA.Math.Vector3 toObj = prop.Position - playerPos;
                            GTA.Math.Vector3 toObjNorm = GTA.Math.Vector3.Normalize(toObj);
                            float pan = Math.Max(-1f, Math.Min(1f, GTA.Math.Vector3.Dot(rightVec, toObjNorm)));

                            outDoor.Stop();
                            doorSound.Position = 0;
                            // Apply panning if mono
                            if (doorSound.WaveFormat.Channels == 1)
                            {
                                var panned = new PanningSampleProvider(doorSound.ToSampleProvider()) { Pan = pan };
                                outDoor.Init(panned);
                            }
                            else
                            {
                                outDoor.Init(doorSound);
                            }
                            outDoor.Play();
                            lastAnnouncedDoor = prop;
                        }
                        else if (isLadder && prop != lastAnnouncedLadder && ladderSound != null && outLadder != null)
                        {
                            // Calculate pan
                            GTA.Math.Vector3 toObj = prop.Position - playerPos;
                            GTA.Math.Vector3 toObjNorm = GTA.Math.Vector3.Normalize(toObj);
                            float pan = Math.Max(-1f, Math.Min(1f, GTA.Math.Vector3.Dot(rightVec, toObjNorm)));

                            outLadder.Stop();
                            ladderSound.Position = 0;
                            if (ladderSound.WaveFormat.Channels == 1)
                            {
                                var panned = new PanningSampleProvider(ladderSound.ToSampleProvider()) { Pan = pan };
                                outLadder.Init(panned);
                            }
                            else
                            {
                                outLadder.Init(ladderSound);
                            }
                            outLadder.Play();
                            lastAnnouncedLadder = prop;
                        }
                    }

                    // Reset announcements if we've moved away
                    if (lastAnnouncedDoor != null && World.GetDistance(playerPos, lastAnnouncedDoor.Position) > 8f)
                        lastAnnouncedDoor = null;
                    if (lastAnnouncedLadder != null && World.GetDistance(playerPos, lastAnnouncedLadder.Position) > 8f)
                        lastAnnouncedLadder = null;
                }

                // ============================================
                // BATCH 2 FEATURES - Safe House Proximity
                // ============================================
                if (DateTime.Now.Ticks - safeHouseCheckTicks > 50000000) // 5 seconds
                {
                    safeHouseCheckTicks = DateTime.Now.Ticks;

                    // Determine current character
                    PedHash playerHash = (PedHash)Game.Player.Character.Model.Hash;
                    GTA.Math.Vector3 safeHousePos = GTA.Math.Vector3.Zero;
                    string safeHouseName = "";

                    if (playerHash == PedHash.Michael)
                    {
                        safeHousePos = new GTA.Math.Vector3(-852.4f, 160.0f, 65.6f);
                        safeHouseName = "Michael's safe house";
                    }
                    else if (playerHash == PedHash.Franklin)
                    {
                        safeHousePos = new GTA.Math.Vector3(7.9f, 548.1f, 175.5f);
                        safeHouseName = "Franklin's safe house";
                    }
                    else if (playerHash == PedHash.Trevor)
                    {
                        safeHousePos = new GTA.Math.Vector3(1985.7f, 3812.2f, 32.2f);
                        safeHouseName = "Trevor's safe house";
                    }

                    if (safeHousePos != GTA.Math.Vector3.Zero)
                    {
                        float dist = World.GetDistance(Game.Player.Character.Position, safeHousePos);

                        if (dist < 50f && !nearSafeHouseAnnounced)
                        {
                            Tolk.Speak(safeHouseName + " nearby", true);
                            nearSafeHouseAnnounced = true;
                        }
                        else if (dist > 100f)
                        {
                            nearSafeHouseAnnounced = false;
                        }
                    }
                }

                // ============================================
                // BATCH 2 FEATURES - Service Proximity
                // ============================================
                if (getSetting("serviceProximity") == 1 && DateTime.Now.Ticks - serviceCheckTicks > 30000000) // 3 seconds
                {
                    serviceCheckTicks = DateTime.Now.Ticks;

                    // Service blip types: 110=Ammu-Nation, 61=Hospital, 408=Clothing, 73=Mod Shop, 207=Barber, 362=Tattoo, 89=ATM
                    int[] serviceTypes = { 110, 61, 408, 73, 207, 362, 89 };
                    string[] serviceNames = { "Ammu-Nation", "Hospital", "Clothing Store", "Mod Shop", "Barber", "Tattoo Parlor", "ATM" };

                    float userRadius = GetDetectionRadius();
                    GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

                    // Reset announcement if player moved far from last announced service
                    if (lastAnnouncedServiceBlip != -1 && lastAnnouncedServicePos != GTA.Math.Vector3.Zero)
                    {
                        float distToLast = World.GetDistance(playerPos, lastAnnouncedServicePos);
                        if (distToLast > userRadius * 1.5f)
                        {
                            lastAnnouncedServiceBlip = -1;
                            lastAnnouncedServicePos = GTA.Math.Vector3.Zero;
                        }
                    }

                    for (int i = 0; i < serviceTypes.Length; i++)
                    {
                        int blipHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, serviceTypes[i]);
                        while (Function.Call<bool>(Hash.DOES_BLIP_EXIST, blipHandle))
                        {
                            GTA.Math.Vector3 blipPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blipHandle);
                            float dist = World.GetDistance(playerPos, blipPos);

                            if (dist < userRadius && blipHandle != lastAnnouncedServiceBlip)
                            {
                                // Calculate direction
                                float dx = blipPos.X - playerPos.X;
                                float dy = blipPos.Y - playerPos.Y;
                                double angle = Math.Atan2(dx, dy) * (180.0 / Math.PI);
                                if (angle < 0) angle += 360;

                                string direction = getDir((float)angle);
                                Tolk.Speak(serviceNames[i] + ", " + (int)dist + " meters " + direction, true);
                                lastAnnouncedServiceBlip = blipHandle;
                                lastAnnouncedServicePos = blipPos;
                                break;
                            }

                            blipHandle = Function.Call<int>(Hash.GET_NEXT_BLIP_INFO_ID, serviceTypes[i]);
                        }
                    }
                }

            }

        }

        private void onKeyDown(object sender, KeyEventArgs e)
        {

            if (e.Control)
            {
                shifting = true;
            }

            /*
			if (e.KeyCode == Keys.NumPad7 && !shifting && !keyState[7])
			{
				keyState[7] = true;
				Vehicle[] vehicles = World.GetNearbyVehicles(Game.Player.Character.Position, 500);
				foreach (Vehicle vehicle in vehicles)
				{
					if (vehicle.IsDead == false)
					{
						vehicle.Explode();

					}
				}


			}
			*/

            if (e.KeyCode == Keys.NumPad2 && shifting && !keyState[2])
            {
                keyState[2] = true;

                if (!keys_disabled)
                {
                    keys_disabled = true;
                    Tolk.Speak("Accessibility keys deactivated.");
                }
                else if (keys_disabled)
                {
                    keys_disabled = false;
                    Tolk.Speak("Accessibility keys activated.");
                }
            }

            if (!keys_disabled)
            {
                // F1 — player-indicated failure marker for drive-assist debugging.
                // A play-tester presses this to stamp the current failure moment
                // into the drive-assist debug log.
                if (e.KeyCode == Keys.F1 && !keyState[18])
                {
                    keyState[18] = true;
                    LogPlayerIndicatedFailure();
                }

                if (e.KeyCode == Keys.NumPad4 && !keyState[4])
                {
                    keyState[4] = true;
                    float detectRadius = GetDetectionRadius();
                    Vehicle[] vehicles = World.GetNearbyVehicles(Game.Player.Character.Position, detectRadius);
                    string status;
                    List<Result> results = new List<Result>();
                    bool valid = false;
                    foreach (Vehicle vehicle in vehicles)
                    {
                        valid = false;

                        if (getSetting("onscreen") == 0 && vehicle.IsVisible && !vehicle.IsDead)
                            valid = true;

                        if (getSetting("onscreen") == 1 && vehicle.IsVisible && !vehicle.IsDead && vehicle.IsOnScreen)
                            valid = true;

                        if (valid)
                        {
                            if (vehicle.IsStopped)
                            {
                                status = "a stationary";
                            }
                            else
                            {
                                status = "a moving";
                            }
                            if (Game.Player.Character.CurrentVehicle != vehicle)
                            {
                                string name = (status + " " + vehicle.LocalizedName);
                                double xyDistance = Math.Round(World.GetDistance(Game.Player.Character.Position, vehicle.Position) - Math.Abs(Game.Player.Character.Position.Z - vehicle.Position.Z), 1);
                                double zDistance = Math.Round(vehicle.Position.Z - Game.Player.Character.Position.Z, 1);
                                string direction = getDir(calculate_x_y_angle(Game.Player.Character.Position.X, Game.Player.Character.Position.Y, vehicle.Position.X, vehicle.Position.Y, 0));
                                Result result = new Result(name, xyDistance, zDistance, direction);
                                results.Add(result);
                            }

                        }
                    }

                    Tolk.Speak(listToString(results, "Nearest Vehicles: "));

                }


                if (e.KeyCode == Keys.Decimal && !keyState[10])
                {
                    keyState[10] = true;

                    // Double-tap detection: 250ms window
                    long currentTicks = DateTime.Now.Ticks;
                    bool isDoubleTap = (currentTicks - lastDecimalPressTicks) < 2500000; // 250ms in ticks
                    lastDecimalPressTicks = currentTicks;

                    if (isDoubleTap)
                    {
                        // DOUBLE TAP: Toggle waypoint/mission tracking
                        // Check if waypoint exists
                        bool waypointExists = Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE);

                        if (waypointExists)
                        {
                            // Toggle waypoint tracking
                            if (waypointTrackingActive)
                            {
                                waypointTrackingActive = false;
                                Tolk.Speak("Waypoint tracking stopped", true);
                            }
                            else
                            {
                                // Start tracking and announce distance/direction
                                int waypointBlipHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8); // 8 = waypoint blip type
                                if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, waypointBlipHandle))
                                {
                                    GTA.Math.Vector3 waypointPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, waypointBlipHandle);
                                    GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

                                    // Calculate distance
                                    float dx = waypointPos.X - playerPos.X;
                                    float dy = waypointPos.Y - playerPos.Y;
                                    float distance = (float)Math.Sqrt(dx * dx + dy * dy);

                                    // Calculate direction to waypoint
                                    double angleToWaypoint = Math.Atan2(dx, dy) * (180.0 / Math.PI);
                                    if (angleToWaypoint < 0) angleToWaypoint += 360;

                                    string direction = getDir((float)angleToWaypoint);
                                    int distMeters = (int)Math.Round(distance);

                                    waypointTrackingActive = true;
                                    missionTrackingActive = false; // Disable mission tracking
                                    waypointBeepTicks = 0; // Reset to play beep immediately
                                    Tolk.Speak("Waypoint tracking. " + distMeters + " meters " + direction, true);
                                }
                            }
                        }
                        else
                        {
                            // No waypoint - scan for mission/objective blips
                            // If already tracking, toggle off
                            if (missionTrackingActive)
                            {
                                missionTrackingActive = false;
                                trackedBlipHandle = -1;
                                Tolk.Speak("Mission tracking stopped", true);
                            }
                            else
                            {
                                // Scan for available blips
                                int[] missionBlipTypes = { 1, 2, 66, 143, 225, 304, 305, 407, 417, 478 };
                                string[] blipTypeNames = { "Destination", "Vehicle", "Mission", "Gang Attack",
                                    "Stranger", "Objective", "Crew", "Boss", "Crew Member", "Activity" };

                                GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                                float closestDist = float.MaxValue;
                                int closestBlipHandle = -1;
                                string closestBlipName = "";
                                int foundCount = 0;
                                List<string> foundBlips = new List<string>();

                                for (int i = 0; i < missionBlipTypes.Length; i++)
                                {
                                    int blipHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, missionBlipTypes[i]);
                                    while (Function.Call<bool>(Hash.DOES_BLIP_EXIST, blipHandle))
                                    {
                                        GTA.Math.Vector3 blipPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blipHandle);
                                        float dist = World.GetDistance(playerPos, blipPos);

                                        foundCount++;
                                        if (!foundBlips.Contains(blipTypeNames[i]))
                                            foundBlips.Add(blipTypeNames[i]);

                                        if (dist < closestDist)
                                        {
                                            closestDist = dist;
                                            closestBlipHandle = blipHandle;
                                            closestBlipName = blipTypeNames[i];
                                            trackedBlipType = missionBlipTypes[i];
                                        }

                                        blipHandle = Function.Call<int>(Hash.GET_NEXT_BLIP_INFO_ID, missionBlipTypes[i]);
                                    }
                                }

                                if (closestBlipHandle != -1)
                                {
                                    // Start tracking nearest blip
                                    trackedBlipHandle = closestBlipHandle;
                                    missionTrackingActive = true;
                                    missionBeepTicks = 0;

                                    // Calculate direction
                                    GTA.Math.Vector3 blipPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, closestBlipHandle);
                                    float dx = blipPos.X - playerPos.X;
                                    float dy = blipPos.Y - playerPos.Y;
                                    double angle = Math.Atan2(dx, dy) * (180.0 / Math.PI);
                                    if (angle < 0) angle += 360;

                                    string foundList = foundBlips.Count > 1 ? String.Join(", ", foundBlips.Take(3)) : closestBlipName;
                                    Tolk.Speak("Tracking " + closestBlipName + ". " + (int)closestDist + " meters " + getDir((float)angle) +
                                        (foundCount > 1 ? ". " + foundCount + " markers found: " + foundList : ""), true);
                                }
                                else
                                {
                                    // No markers found
                                    Tolk.Speak("No markers found", true);
                                }
                            }
                        }
                    }
                    else
                    {
                        // SINGLE TAP: Just announce current heading
                        Tolk.Speak("Facing " + getDir(Game.Player.Character.Heading), true);
                    }
                }

                // ============================================
                // BLIP CYCLING (NumPad Plus)
                // Cycles through ALL mission markers on map
                // Auto-places waypoint on selected marker
                // ============================================
                if (e.KeyCode == Keys.Add && !keyState[11])
                {
                    keyState[11] = true;

                    // Blip types to scan: mission markers, objectives, destinations, etc.
                    int[] missionBlipTypes = { 1, 2, 66, 143, 225, 304, 305, 407, 417, 478 };
                    string[] blipTypeNames = { "Destination", "Vehicle", "Mission", "Gang Attack",
                        "Stranger", "Objective", "Crew", "Boss", "Crew Member", "Activity" };

                    // Refresh blip list every 2 seconds to catch new markers
                    long currentTicks = DateTime.Now.Ticks;
                    if (currentTicks - lastBlipScanTicks > 20000000) // 2 seconds
                    {
                        lastBlipScanTicks = currentTicks;
                        availableBlipHandles.Clear();
                        availableBlipNames.Clear();
                        availableBlipTypes.Clear();
                        blipCycleIndex = -1; // Reset index when rescanning

                        GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

                        // Build sorted list of all available blips
                        List<Tuple<int, string, int, float>> allBlips = new List<Tuple<int, string, int, float>>();

                        for (int i = 0; i < missionBlipTypes.Length; i++)
                        {
                            int blipHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, missionBlipTypes[i]);
                            while (Function.Call<bool>(Hash.DOES_BLIP_EXIST, blipHandle))
                            {
                                GTA.Math.Vector3 blipPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blipHandle);
                                float dist = World.GetDistance(playerPos, blipPos);
                                allBlips.Add(new Tuple<int, string, int, float>(blipHandle, blipTypeNames[i], missionBlipTypes[i], dist));

                                blipHandle = Function.Call<int>(Hash.GET_NEXT_BLIP_INFO_ID, missionBlipTypes[i]);
                            }
                        }

                        // Sort by distance (closest first)
                        allBlips.Sort((a, b) => a.Item4.CompareTo(b.Item4));

                        // Store in parallel lists
                        foreach (var blip in allBlips)
                        {
                            availableBlipHandles.Add(blip.Item1);
                            availableBlipNames.Add(blip.Item2);
                            availableBlipTypes.Add(blip.Item3);
                        }
                    }

                    if (availableBlipHandles.Count == 0)
                    {
                        Tolk.Speak("No markers found", true);
                    }
                    else
                    {
                        // Cycle to next marker
                        blipCycleIndex++;
                        if (blipCycleIndex >= availableBlipHandles.Count)
                            blipCycleIndex = 0; // Wrap around

                        int selectedHandle = availableBlipHandles[blipCycleIndex];
                        string selectedName = availableBlipNames[blipCycleIndex];
                        int selectedType = availableBlipTypes[blipCycleIndex];

                        // Verify blip still exists
                        if (!Function.Call<bool>(Hash.DOES_BLIP_EXIST, selectedHandle))
                        {
                            // Force rescan on next press
                            lastBlipScanTicks = 0;
                            Tolk.Speak("Marker no longer available, rescanning", true);
                        }
                        else
                        {
                            GTA.Math.Vector3 blipPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, selectedHandle);
                            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

                            // Calculate distance and direction
                            float dx = blipPos.X - playerPos.X;
                            float dy = blipPos.Y - playerPos.Y;
                            float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                            double angle = Math.Atan2(dx, dy) * (180.0 / Math.PI);
                            if (angle < 0) angle += 360;

                            string direction = getDir((float)angle);

                            // Place waypoint at blip location
                            Function.Call(Hash.SET_NEW_WAYPOINT, blipPos.X, blipPos.Y);

                            // Start tracking this marker
                            trackedBlipHandle = selectedHandle;
                            trackedBlipType = selectedType;
                            missionTrackingActive = true;
                            waypointTrackingActive = true; // Also enable waypoint tracking since we set a waypoint
                            missionBeepTicks = 0;
                            waypointBeepTicks = 0;

                            // Announce: marker X of Y, type, distance, direction
                            string announcement = "Marker " + (blipCycleIndex + 1) + " of " + availableBlipHandles.Count +
                                ". " + selectedName + ", " + (int)distance + " meters " + direction + ". Waypoint set.";
                            Tolk.Speak(announcement, true);
                        }
                    }
                }

                // ============================================
                // BLIP CYCLING REVERSE (NumPad Minus)
                // Cycles backwards through mission markers
                // ============================================
                if (e.KeyCode == Keys.Subtract && !keyState[12])
                {
                    keyState[12] = true;

                    // Same blip scanning logic as NumPad Plus
                    int[] missionBlipTypes = { 1, 2, 66, 143, 225, 304, 305, 407, 417, 478 };
                    string[] blipTypeNames = { "Destination", "Vehicle", "Mission", "Gang Attack",
                        "Stranger", "Objective", "Crew", "Boss", "Crew Member", "Activity" };

                    // Refresh blip list every 2 seconds
                    long currentTicks = DateTime.Now.Ticks;
                    if (currentTicks - lastBlipScanTicks > 20000000)
                    {
                        lastBlipScanTicks = currentTicks;
                        availableBlipHandles.Clear();
                        availableBlipNames.Clear();
                        availableBlipTypes.Clear();
                        blipCycleIndex = -1;

                        GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                        List<Tuple<int, string, int, float>> allBlips = new List<Tuple<int, string, int, float>>();

                        for (int i = 0; i < missionBlipTypes.Length; i++)
                        {
                            int blipHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, missionBlipTypes[i]);
                            while (Function.Call<bool>(Hash.DOES_BLIP_EXIST, blipHandle))
                            {
                                GTA.Math.Vector3 blipPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blipHandle);
                                float dist = World.GetDistance(playerPos, blipPos);
                                allBlips.Add(new Tuple<int, string, int, float>(blipHandle, blipTypeNames[i], missionBlipTypes[i], dist));
                                blipHandle = Function.Call<int>(Hash.GET_NEXT_BLIP_INFO_ID, missionBlipTypes[i]);
                            }
                        }

                        allBlips.Sort((a, b) => a.Item4.CompareTo(b.Item4));

                        foreach (var blip in allBlips)
                        {
                            availableBlipHandles.Add(blip.Item1);
                            availableBlipNames.Add(blip.Item2);
                            availableBlipTypes.Add(blip.Item3);
                        }
                    }

                    if (availableBlipHandles.Count == 0)
                    {
                        Tolk.Speak("No markers found", true);
                    }
                    else
                    {
                        // Cycle BACKWARDS
                        blipCycleIndex--;
                        if (blipCycleIndex < 0)
                            blipCycleIndex = availableBlipHandles.Count - 1;

                        int selectedHandle = availableBlipHandles[blipCycleIndex];
                        string selectedName = availableBlipNames[blipCycleIndex];
                        int selectedType = availableBlipTypes[blipCycleIndex];

                        if (!Function.Call<bool>(Hash.DOES_BLIP_EXIST, selectedHandle))
                        {
                            lastBlipScanTicks = 0;
                            Tolk.Speak("Marker no longer available, rescanning", true);
                        }
                        else
                        {
                            GTA.Math.Vector3 blipPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, selectedHandle);
                            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

                            float dx = blipPos.X - playerPos.X;
                            float dy = blipPos.Y - playerPos.Y;
                            float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                            double angle = Math.Atan2(dx, dy) * (180.0 / Math.PI);
                            if (angle < 0) angle += 360;

                            string direction = getDir((float)angle);

                            Function.Call(Hash.SET_NEW_WAYPOINT, blipPos.X, blipPos.Y);

                            trackedBlipHandle = selectedHandle;
                            trackedBlipType = selectedType;
                            missionTrackingActive = true;
                            waypointTrackingActive = true;
                            missionBeepTicks = 0;
                            waypointBeepTicks = 0;

                            string announcement = "Marker " + (blipCycleIndex + 1) + " of " + availableBlipHandles.Count +
                                ". " + selectedName + ", " + (int)distance + " meters " + direction + ". Waypoint set.";
                            Tolk.Speak(announcement, true);
                        }
                    }
                }

                if (e.KeyCode == Keys.NumPad6)
                {
                    float detectRadius = GetDetectionRadius();
                    Ped[] peds = World.GetNearbyPeds(Game.Player.Character.Position, detectRadius);
                    string status = "";
                    List<Result> results = new List<Result>();
                    bool valid = false;

                    foreach (Ped ped in peds)
                    {
                        valid = false;
                        if (getSetting("onscreen") == 0 && hashes.ContainsKey(ped.Model.NativeValue.ToString()) && ped.IsVisible && !ped.IsDead)
                            valid = true;

                        if (getSetting("onscreen") == 1 && hashes.ContainsKey(ped.Model.NativeValue.ToString()) && ped.IsVisible && ped.IsOnScreen && !ped.IsDead)
                            valid = true;
                        if (valid)
                        {
                            if (hashes[ped.Model.NativeValue.ToString()] != "player_one" && hashes[ped.Model.NativeValue.ToString()] != "player_two" && hashes[ped.Model.NativeValue.ToString()] != "player_zero")
                            {
                                string name = (status + " " + hashes[ped.Model.NativeValue.ToString()]);
                                double xyDistance = Math.Round(World.GetDistance(Game.Player.Character.Position, ped.Position) - Math.Abs(Game.Player.Character.Position.Z - ped.Position.Z), 1);
                                double zDistance = Math.Round(ped.Position.Z - Game.Player.Character.Position.Z, 1);
                                string direction = getDir(calculate_x_y_angle(Game.Player.Character.Position.X, Game.Player.Character.Position.Y, ped.Position.X, ped.Position.Y, 0));
                                Result result = new Result(name, xyDistance, zDistance, direction);
                                results.Add(result);
                            }
                        }
                    }

                    Tolk.Speak(listToString(results, "Nearest Characters: "));

                }

                if (e.KeyCode == Keys.NumPad5 && !keyState[5])
                {
                    keyState[5] = true;
                    float detectRadius = GetDetectionRadius();
                    Prop[] props = World.GetNearbyProps(Game.Player.Character.Position, detectRadius);
                    string status = "";
                    List<Result> results = new List<Result>();

                    bool valid = false;

                    foreach (Prop prop in props)
                    {
                        valid = false;

                        if (getSetting("onscreen") == 0 && hashes.ContainsKey(prop.Model.NativeValue.ToString()) && prop.IsVisible && !prop.IsAttachedTo(Game.Player.Character) && (hashes[prop.Model.NativeValue.ToString()].Contains("door") || hashes[prop.Model.NativeValue.ToString()].Contains("gate")))
                            valid = true;

                        if (getSetting("onscreen") == 1 && hashes.ContainsKey(prop.Model.NativeValue.ToString()) && prop.IsVisible && prop.IsOnScreen && !prop.IsAttachedTo(Game.Player.Character) && (hashes[prop.Model.NativeValue.ToString()].Contains("door") || hashes[prop.Model.NativeValue.ToString()].Contains("gate")))
                            valid = true;

                        if (valid)
                        {
                            string name = (status + " " + hashes[prop.Model.NativeValue.ToString()]);
                            double xyDistance = Math.Round(World.GetDistance(Game.Player.Character.Position, prop.Position) - Math.Abs(Game.Player.Character.Position.Z - prop.Position.Z), 1);
                            double zDistance = Math.Round(prop.Position.Z - Game.Player.Character.Position.Z, 1);
                            string direction = getDir(calculate_x_y_angle(Game.Player.Character.Position.X, Game.Player.Character.Position.Y, prop.Position.X, prop.Position.Y, 0));
                            Result result = new Result(name, xyDistance, zDistance, direction);
                            results.Add(result);

                        }
                    }

                    Tolk.Speak(listToString(results, "Nearest Doors: "));
                }

                if (e.KeyCode == Keys.NumPad8 && !keyState[8])
                {
                    keyState[8] = true;
                    float detectRadius = GetDetectionRadius();
                    Prop[] props = World.GetNearbyProps(Game.Player.Character.Position, detectRadius);
                    string status = "";
                    List<Result> results = new List<Result>();
                    bool valid = false;

                    foreach (Prop prop in props)
                    {
                        valid = false;

                        if (getSetting("onscreen") == 0 && hashes.ContainsKey(prop.Model.NativeValue.ToString()) && prop.IsVisible && !prop.IsAttachedTo(Game.Player.Character) && !hashes[prop.Model.NativeValue.ToString()].Contains("door") && !hashes[prop.Model.NativeValue.ToString()].Contains("gate"))
                            valid = true;

                        if (getSetting("onscreen") == 1 && hashes.ContainsKey(prop.Model.NativeValue.ToString()) && prop.IsVisible && prop.IsOnScreen && !prop.IsAttachedTo(Game.Player.Character) && !hashes[prop.Model.NativeValue.ToString()].Contains("door") && !hashes[prop.Model.NativeValue.ToString()].Contains("gate"))
                            valid = true;

                        if (valid)
                        {
                            string name = (status + " " + hashes[prop.Model.NativeValue.ToString()]);
                            double xyDistance = Math.Round(World.GetDistance(Game.Player.Character.Position, prop.Position) - Math.Abs(Game.Player.Character.Position.Z - prop.Position.Z), 1);
                            double zDistance = Math.Round(prop.Position.Z - Game.Player.Character.Position.Z, 1);
                            string direction = getDir(calculate_x_y_angle(Game.Player.Character.Position.X, Game.Player.Character.Position.Y, prop.Position.X, prop.Position.Y, 0));
                            Result result = new Result(name, xyDistance, zDistance, direction);
                            results.Add(result);

                        }
                    }

                    Tolk.Speak(listToString(results, "Nearest Objects: "));

                }

                if (e.KeyCode == Keys.NumPad0 && !keyState[0])
                {
                    keyState[0] = true;
                    if (e.Control)
                    {
                        // Announce time AND date
                        TimeSpan t = World.CurrentTimeOfDay;
                        string zero = "";
                        if (t.Minutes > 0 && t.Minutes < 10)
                            zero = "0";

                        // Get date
                        int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
                        int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
                        int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);

                        string[] monthNames = { "January", "February", "March", "April", "May", "June",
                            "July", "August", "September", "October", "November", "December" };
                        string monthName = (month >= 0 && month < 12) ? monthNames[month] : "Unknown";

                        Tolk.Speak("The time is: " + t.Hours + ":" + zero + t.Minutes + ". " + monthName + " " + day + ", " + year);
                    }
                    else
                    {
                        // Add money to location announcement
                        string moneyStr = ". Cash: $" + Game.Player.Money.ToString("N0");

                        if (Game.Player.Character.CurrentVehicle == null)
                        {
                            Tolk.Speak("Current location: " + World.GetStreetName(Game.Player.Character.Position) + ", " + World.GetZoneLocalizedName(Game.Player.Character.Position) + moneyStr);
                        }
                        else
                        {
                            Tolk.Speak("Current location: " + "Inside of a " + Game.Player.Character.CurrentVehicle.DisplayName + " at " + World.GetStreetName(Game.Player.Character.Position) + ", " + World.GetZoneLocalizedName(Game.Player.Character.Position) + moneyStr);
                        }


                    }

                }

                if (e.KeyCode == Keys.NumPad2 && !shifting && !keyState[2])
                {
                    Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET", true);
                    keyState[2] = true;

                    if (mainMenuIndex == 0)
                    {

                        if (Game.Player.Character.CurrentVehicle != null)
                        {
                            Game.Player.Character.CurrentVehicle.Position = locations[locationMenuIndex].coords;

                        }
                        else
                        {

                            Game.Player.Character.Position = locations[locationMenuIndex].coords;
                        }
                    }

                    // Auto-Drive menu - NumPad2 stops auto-navigation (when active),
                    // except on the Refresh Driving Task item where it should refresh instead.
                    if (mainMenuIndex == 3 && autodriveFlagMenuIndex != 35)
                    {
                        if (isAutodriving)
                        {
                            isAutodriving = false;
                            autodriveWanderMode = false;
                            if (guardDriverActive && bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive)
                                bodyguards[0].Task.ClearAllImmediately();
                            else
                                Game.Player.Character.Task.ClearAll();
                            if (autonavMode == "fly")
                                Tolk.Speak("Autopilot disengaged. You have control.");
                            else if (autonavMode == "walk")
                                Tolk.Speak("Auto-walk cancelled. You have control.");
                            else
                                Tolk.Speak("Auto-drive cancelled. You have control.");
                            autonavMode = "drive";
                        }
                    }

                    if (mainMenuIndex == 1)
                    {
                        Vehicle vehicle = World.CreateVehicle(spawns[spawnMenuIndex].id, Game.Player.Character.Position + Game.Player.Character.ForwardVector * 2.0f, Game.Player.Character.Heading + 90);
                        if (vehicle == null) { Tolk.Speak("Could not spawn vehicle."); return; }
                        vehicle.PlaceOnGround();
                        if (getSetting("warpInsideVehicle") == 1)
                        {
                            Game.Player.Character.SetIntoVehicle(vehicle, VehicleSeat.Driver);
                        }

                    }

                    if (mainMenuIndex == 5)
                    {
                        HandleBodyguardMenuSelect(bodyguardMenuIndex);
                    }

                    if (mainMenuIndex == 6)
                    {
                        // Section headers cannot be monitored
                        if (statusMenuIndex == 0 || statusMenuIndex == 23 || statusMenuIndex == 57 || statusMenuIndex == 63 || statusMenuIndex == 80)
                        {
                            Tolk.Speak(GetStatusMenuText(statusMenuIndex), true);
                        }
                        else if (statusMonitoredItems.Contains(statusMenuIndex))
                        {
                            statusMonitoredItems.Remove(statusMenuIndex);
                            Tolk.Speak("Stopped monitoring " + GetStatusMenuText(statusMenuIndex));
                        }
                        else
                        {
                            statusMonitoredItems.Add(statusMenuIndex);
                            Tolk.Speak("Monitoring " + GetStatusMenuText(statusMenuIndex));
                        }
                    }

                    if (mainMenuIndex == 7)
                    {
                        if (planeFlightMenuIndex == 0)
                        {
                            // Toggle pilot mode
                            requestedFlightAIPilot = !requestedFlightAIPilot;
                            Tolk.Speak("Pilot: " + (requestedFlightAIPilot ? "AI" : "Player"));
                        }
                        else
                        {
                            RequestPlaneFlight(planeFlightMenuIndex - 1);
                        }
                    }

                    if (mainMenuIndex == 2)
                    {
                        if (funMenuIndex == 0)
                        {
                            Vehicle[] vehicles = World.GetNearbyVehicles(Game.Player.Character.Position, 100);
                            foreach (Vehicle v in vehicles)
                            {
                                if (!v.IsDead)
                                {
                                    if (getSetting("vehicleGodMode") == 0 && Game.Player.Character.CurrentVehicle == v)
                                    {
                                        v.CanBeVisiblyDamaged = true;
                                        v.CanEngineDegrade = true;
                                        v.CanTiresBurst = true;
                                        v.CanWheelsBreak = true;
                                        v.IsExplosionProof = false;
                                        v.IsFireProof = false;
                                        v.IsInvincible = false;
                                        v.IsBulletProof = false;
                                        v.IsMeleeProof = false;
                                    }
                                    v.Explode();
                                    v.MarkAsNoLongerNeeded();
                                }
                            }
                        }

                        if (funMenuIndex == 1)
                        {
                            Ped[] tempPeds = World.GetNearbyPeds(Game.Player.Character.Position, 5000);

                            List<Ped> peds = new List<Ped>();

                            foreach (Ped ped in tempPeds)
                            {

                                if (hashes.ContainsKey(ped.Model.NativeValue.ToString()) && !ped.IsDead)
                                {
                                    if (hashes[ped.Model.NativeValue.ToString()] != "player_one" && hashes[ped.Model.NativeValue.ToString()] != "player_two" && hashes[ped.Model.NativeValue.ToString()] != "player_zero")
                                    {
                                        peds.Add(ped);
                                    }
                                }
                            }
                            if (peds.Count < 4)
                            {
                                Tolk.Speak("More nearby people are needed.");
                            }
                            else
                            {

                                int r = 0;
                                for (int i = 0; i < peds.Count; i++)
                                {
                                    r = random.Next(0, peds.Count - 1);
                                    while (r == i)
                                    {
                                        r = random.Next(0, peds.Count - 1);
                                    }
                                    peds[i].Task.ClearAllImmediately();
                                    peds[i].AlwaysKeepTask = false;
                                    peds[i].BlockPermanentEvents = false;
                                    peds[i].Weapons.Give(WeaponHash.APPistol, 1000, true, true);
                                    peds[i].Task.FightAgainst(peds[r]);
                                    peds[i].AlwaysKeepTask = true;
                                    peds[i].BlockPermanentEvents = true;

                                }
                            }
                        }

                        if (funMenuIndex == 2)
                        {
                            Ped[] tempPeds = World.GetNearbyPeds(Game.Player.Character.Position, 5000);

                            List<Ped> peds = new List<Ped>();
                            foreach (Ped ped in tempPeds)
                            {
                                if (hashes.ContainsKey(ped.Model.NativeValue.ToString()) && !ped.IsDead)
                                {
                                    if (hashes[ped.Model.NativeValue.ToString()] != "player_one" && hashes[ped.Model.NativeValue.ToString()] != "player_two" && hashes[ped.Model.NativeValue.ToString()] != "player_zero")
                                    {
                                        peds.Add(ped);
                                    }
                                }
                            }

                            foreach (Ped ped in peds)
                            {
                                ped.Kill();
                            }

                        }

                        if (funMenuIndex == 3)
                        {
                            if (Game.Player.WantedLevel < 5)
                                Game.Player.WantedLevel++;
                        }

                        if (funMenuIndex == 4)
                        {
                            Game.Player.WantedLevel = 0;

                        }

                        if (funMenuIndex == 5)
                        {
                            GTA.Math.Vector3 pos = Game.Player.Character.Position;
                            // Random offset 30-40 meters away to avoid auto-clear
                            float angle = (float)(random.NextDouble() * Math.PI * 2);
                            float dist = 30f + (float)(random.NextDouble() * 10.0);
                            float wx = pos.X + dist * (float)Math.Cos(angle);
                            float wy = pos.Y + dist * (float)Math.Sin(angle);
                            Function.Call(Hash.SET_NEW_WAYPOINT, wx, wy);
                            Tolk.Speak("Waypoint placed near current location.");
                        }


                    }


                    // Auto-Drive menu (mainMenuIndex == 3)
                    if (mainMenuIndex == 3)
                    {
                        if (autodriveFlagMenuIndex == 32)
                        {
                            // Land action
                            ExecuteLandCommand();
                        }
                        else if (autodriveFlagMenuIndex == 33)
                        {
                            // Park action
                            ExecuteParkCommand();
                        }
                        else if (autodriveFlagMenuIndex == 34)
                        {
                            // Hitch trailer action
                            ExecuteHitchTrailerCommand();
                        }
                        else if (autodriveFlagMenuIndex == 35)
                        {
                            // Refresh active driving/flight task
                            RefreshAutodriveTask();
                        }
                        else
                        {
                        // Toggle the current flag (works while driving)
                        autodriveFlags[autodriveFlagMenuIndex] = !autodriveFlags[autodriveFlagMenuIndex];
                        string flagState = autodriveFlags[autodriveFlagMenuIndex] ? "ON" : "OFF";
                        Tolk.Speak(autodriveFlagNames[autodriveFlagMenuIndex] + ", " + flagState);

                        // If currently autodriving, restart the task with new flags
                        if (isAutodriving)
                        {
                            UpdateAutodriveSpeed(); // This also updates flags
                        }
                        }
                    }

                    // Settings menu (mainMenuIndex == 4)
                    if (mainMenuIndex == 4)
                    {
                        // Special handling for detection radius (cycles through 10m/25m/50m/100m/125m/150m/200m/250m/300m)
                        if (settingsMenu[settingsMenuIndex].id == "detectionRadius")
                        {
                            int currentVal = settingsMenu[settingsMenuIndex].value;
                            currentVal = (currentVal + 1) % detectionRadiusOptions.Length; // Cycle through all radius options
                            settingsMenu[settingsMenuIndex].value = currentVal;

                            float radius = detectionRadiusOptions[currentVal];
                            Tolk.Speak("Detection Radius: " + (int)radius + " meters");
                        }
                        // Special handling for steering assist (cycles through Off/Assistive/Full)
                        else if (settingsMenu[settingsMenuIndex].id == "steeringAssist")
                        {
                            int currentVal = settingsMenu[settingsMenuIndex].value;
                            currentVal = (currentVal + 1) % 3; // Cycle 0->1->2->0
                            settingsMenu[settingsMenuIndex].value = currentVal;

                            string modeText = currentVal == 0 ? "Off" : (currentVal == 1 ? "Assistive" : "Full");
                            Tolk.Speak("Steering Assist Mode: " + modeText);
                        }
                        else
                        {
                            // Normal on/off toggle for other settings
                            if (settingsMenu[settingsMenuIndex].value == 0)
                            {
                                settingsMenu[settingsMenuIndex].value = 1;
                                Tolk.Speak(settingsMenu[settingsMenuIndex].displayName + "On! ");
                            }
                            else if (settingsMenu[settingsMenuIndex].value == 1)
                            {
                                settingsMenu[settingsMenuIndex].value = 0;
                                Tolk.Speak(settingsMenu[settingsMenuIndex].displayName + "Off! ");
                            }
                        }

                        saveSettings();
                    }
                }

                if (e.KeyCode == Keys.NumPad1 && !keyState[1])
                {
                    Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "NAV_LEFT_RIGHT", "HUD_FRONTEND_DEFAULT_SOUNDSET", true);

                    keyState[1] = true;
                    if (mainMenuIndex == 0)
                    {
                        if (locationMenuIndex > 00)
                        {
                            locationMenuIndex--;
                            Tolk.Speak(locations[locationMenuIndex].name);
                        }

                        else
                        {
                            locationMenuIndex = locations.Count - 1;
                            Tolk.Speak(locations[locationMenuIndex].name);
                        }
                    }

                    if (mainMenuIndex == 1)
                    {
                        if (!shifting)
                        {
                            if (spawnMenuIndex > 0)
                            {
                                spawnMenuIndex--;
                                Tolk.Speak(spawns[spawnMenuIndex].name);
                            }

                            else
                            {
                                spawnMenuIndex = spawns.Count - 1;
                                Tolk.Speak(spawns[spawnMenuIndex].name);
                            }

                        }

                        if (shifting)
                        {
                            if (spawnMenuIndex > 25)
                            {
                                spawnMenuIndex = spawnMenuIndex - 25;
                                Tolk.Speak(spawns[spawnMenuIndex].name);
                            }

                            else
                            {
                                int rem = spawnMenuIndex;
                                spawnMenuIndex = spawns.Count - 1 - rem;
                                Tolk.Speak(spawns[spawnMenuIndex].name);
                            }
                        }

                    }

                    if (mainMenuIndex == 2)
                    {
                        if (funMenuIndex > 0)
                        {
                            funMenuIndex--;
                            Tolk.Speak(funMenu[funMenuIndex]);
                        }

                        else
                        {
                            funMenuIndex = funMenu.Count - 1;
                            Tolk.Speak(funMenu[funMenuIndex]);
                        }
                    }

                    // Auto-Drive menu navigation (cycle through 32 flags + 4 action items) - works while driving
                    if (mainMenuIndex == 3)
                    {
                        if (autodriveFlagMenuIndex > 0)
                            autodriveFlagMenuIndex--;
                        else
                            autodriveFlagMenuIndex = 35; // Wrap to end

                        if (autodriveFlagMenuIndex == 32)
                            Tolk.Speak("33. Land");
                        else if (autodriveFlagMenuIndex == 33)
                            Tolk.Speak("34. Park at Nearest Safe Spot");
                        else if (autodriveFlagMenuIndex == 34)
                            Tolk.Speak("35. Hitch to Nearest Trailer");
                        else if (autodriveFlagMenuIndex == 35)
                            Tolk.Speak("36. Refresh Driving Task");
                        else
                        {
                        string flagState = autodriveFlags[autodriveFlagMenuIndex] ? "ON" : "OFF";
                        Tolk.Speak((autodriveFlagMenuIndex + 1) + ". " + autodriveFlagNames[autodriveFlagMenuIndex] + ", " + flagState);
                        }
                    }

                    if (mainMenuIndex == 4)
                    {
                        if (settingsMenuIndex > 0)
                        {
                            settingsMenuIndex--;
                            SpeakCurrentSetting();
                        }
                        else
                        {
                            settingsMenuIndex = settingsMenu.Count - 1;
                            SpeakCurrentSetting();
                        }
                    }

                    if (mainMenuIndex == 5)
                    {
                        if (bodyguardMenuIndex > 0)
                            bodyguardMenuIndex--;
                        else
                            bodyguardMenuIndex = bodyguardMenu.Count - 1;
                        Tolk.Speak(GetBodyguardMenuText(bodyguardMenuIndex));
                    }

                    if (mainMenuIndex == 6)
                    {
                        if (statusMenuIndex > 0)
                            statusMenuIndex--;
                        else
                            statusMenuIndex = STATUS_MENU_ITEM_COUNT - 1;
                        string statusText = GetStatusMenuText(statusMenuIndex);
                        if (statusMonitoredItems.Contains(statusMenuIndex))
                            statusText += " Monitoring.";
                        Tolk.Speak(statusText);
                    }

                    if (mainMenuIndex == 7)
                    {
                        if (planeFlightMenuIndex > 0)
                            planeFlightMenuIndex--;
                        else
                            planeFlightMenuIndex = GetFlightMenuCount() - 1;
                        Tolk.Speak(GetFlightMenuText(planeFlightMenuIndex));
                    }

                }

                if (e.KeyCode == Keys.NumPad3 & !keyState[3])
                {
                    Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "NAV_LEFT_RIGHT", "HUD_FRONTEND_DEFAULT_SOUNDSET", true);
                    keyState[3] = true;
                    if (mainMenuIndex == 0)
                    {
                        if (locationMenuIndex < locations.Count - 1)
                        {
                            locationMenuIndex++;
                            Tolk.Speak(locations[locationMenuIndex].name);

                        }
                        else
                        {
                            locationMenuIndex = 0;
                            Tolk.Speak(locations[locationMenuIndex].name);

                        }
                    }

                    if (mainMenuIndex == 1)
                    {
                        if (!shifting)
                        {
                            if (spawnMenuIndex < spawns.Count - 1)
                            {
                                spawnMenuIndex++;
                                Tolk.Speak(spawns[spawnMenuIndex].name);

                            }
                            else
                            {
                                spawnMenuIndex = 0;
                                Tolk.Speak(spawns[spawnMenuIndex].name);

                            }
                        }

                        if (shifting)
                        {
                            if (spawnMenuIndex < spawns.Count - 26)
                            {
                                spawnMenuIndex = spawnMenuIndex + 25;
                                Tolk.Speak(spawns[spawnMenuIndex].name);

                            }
                            else
                            {
                                int rem = spawns.Count - 1 - spawnMenuIndex;
                                spawnMenuIndex = rem;
                                Tolk.Speak(spawns[spawnMenuIndex].name);

                            }
                        }

                    }

                    if (mainMenuIndex == 2)
                    {
                        if (funMenuIndex < funMenu.Count - 1)
                        {
                            funMenuIndex++;
                            Tolk.Speak(funMenu[funMenuIndex]);

                        }
                        else
                        {
                            funMenuIndex = 0;
                            Tolk.Speak(funMenu[funMenuIndex]);

                        }
                    }

                    // Auto-Drive menu navigation (cycle through 32 flags + 4 action items)
                    // Auto-Drive menu navigation - works while driving
                    if (mainMenuIndex == 3)
                    {
                        if (autodriveFlagMenuIndex < 35)
                            autodriveFlagMenuIndex++;
                        else
                            autodriveFlagMenuIndex = 0; // Wrap to start

                        if (autodriveFlagMenuIndex == 32)
                            Tolk.Speak("33. Land");
                        else if (autodriveFlagMenuIndex == 33)
                            Tolk.Speak("34. Park at Nearest Safe Spot");
                        else if (autodriveFlagMenuIndex == 34)
                            Tolk.Speak("35. Hitch to Nearest Trailer");
                        else if (autodriveFlagMenuIndex == 35)
                            Tolk.Speak("36. Refresh Driving Task");
                        else
                        {
                        string flagState = autodriveFlags[autodriveFlagMenuIndex] ? "ON" : "OFF";
                        Tolk.Speak((autodriveFlagMenuIndex + 1) + ". " + autodriveFlagNames[autodriveFlagMenuIndex] + ", " + flagState);
                        }
                    }

                    if (mainMenuIndex == 4)
                    {
                        if (settingsMenuIndex < settingsMenu.Count - 1)
                        {
                            settingsMenuIndex++;
                            SpeakCurrentSetting();
                        }
                        else
                        {
                            settingsMenuIndex = 0;
                            SpeakCurrentSetting();
                        }
                    }

                    if (mainMenuIndex == 5)
                    {
                        if (bodyguardMenuIndex < bodyguardMenu.Count - 1)
                            bodyguardMenuIndex++;
                        else
                            bodyguardMenuIndex = 0;
                        Tolk.Speak(GetBodyguardMenuText(bodyguardMenuIndex));
                    }

                    if (mainMenuIndex == 6)
                    {
                        if (statusMenuIndex < STATUS_MENU_ITEM_COUNT - 1)
                            statusMenuIndex++;
                        else
                            statusMenuIndex = 0;
                        string statusText = GetStatusMenuText(statusMenuIndex);
                        if (statusMonitoredItems.Contains(statusMenuIndex))
                            statusText += " Monitoring.";
                        Tolk.Speak(statusText);
                    }

                    if (mainMenuIndex == 7)
                    {
                        if (planeFlightMenuIndex < GetFlightMenuCount() - 1)
                            planeFlightMenuIndex++;
                        else
                            planeFlightMenuIndex = 0;
                        Tolk.Speak(GetFlightMenuText(planeFlightMenuIndex));
                    }


                }

                if (e.KeyCode == Keys.NumPad7 && !keyState[7])
                {
                    Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET", true);
                    keyState[7] = true;
                    if (mainMenuIndex > 0)
                    {
                        mainMenuIndex--;
                        speakMenu();
                    }

                    else
                    {
                        mainMenuIndex = mainMenu.Count - 1;
                        speakMenu();
                    }
                }

                if (e.KeyCode == Keys.NumPad9 && !keyState[9])
                {
                    Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET", true);
                    keyState[9] = true;
                    if (mainMenuIndex < mainMenu.Count - 1)
                    {
                        mainMenuIndex++;
                        speakMenu();

                    }
                    else
                    {
                        mainMenuIndex = 0;
                        speakMenu();

                    }
                }

                // ============================================
                // AUTO-NAVIGATION SPEED CONTROL (Arrow Keys)
                // Left arrow = decrease speed, Right arrow = increase speed
                // Only active in Auto-Drive menu when not actively navigating
                // Speed granularity: 5 mph increments
                // ============================================
                if (e.KeyCode == Keys.Left && !keyState[13])
                {
                    keyState[13] = true;
                    if (mainMenuIndex == 3)
                    {
                        // Convert current m/s to mph, round to nearest 5, subtract 5, convert back
                        int currentMph = (int)Math.Round(autodriveSpeed * 2.23694);
                        int snappedMph = ((int)Math.Round(currentMph / 5.0)) * 5;
                        int newMph = Math.Max(5, snappedMph - 5);
                        autodriveSpeed = (float)(newMph / 2.23694);
                        Tolk.Speak("Speed: " + newMph + " mph");

                        // If currently autodriving, update the task speed
                        if (isAutodriving)
                        {
                            UpdateAutodriveSpeed();
                        }
                    }
                }

                if (e.KeyCode == Keys.Right && !keyState[14])
                {
                    keyState[14] = true;
                    if (mainMenuIndex == 3)
                    {
                        // Convert current m/s to mph, round to nearest 5, add 5, convert back
                        int currentMph = (int)Math.Round(autodriveSpeed * 2.23694);
                        int snappedMph = ((int)Math.Round(currentMph / 5.0)) * 5;
                        int newMph = snappedMph + 5;
                        autodriveSpeed = (float)(newMph / 2.23694);
                        Tolk.Speak("Speed: " + newMph + " mph");

                        // If currently autodriving, update the task speed
                        if (isAutodriving)
                        {
                            UpdateAutodriveSpeed();
                        }
                    }
                }

                // ============================================
                // AUTOPILOT ALTITUDE CONTROL (Up/Down Arrow Keys)
                // Up arrow = increase altitude, Down arrow = decrease altitude
                // Only active in Auto-Drive menu when not actively navigating
                // ============================================
                if (e.KeyCode == Keys.Up && !keyState[16])
                {
                    keyState[16] = true;
                    if (mainMenuIndex == 3 && !isAutodriving)
                    {
                        autopilotAltitude = autopilotAltitude + 25f;
                        Tolk.Speak("Flight altitude: " + (int)autopilotAltitude + " meters");
                    }
                }

                if (e.KeyCode == Keys.Down && !keyState[17])
                {
                    keyState[17] = true;
                    if (mainMenuIndex == 3 && !isAutodriving)
                    {
                        autopilotAltitude = Math.Max(25f, autopilotAltitude - 25f);
                        Tolk.Speak("Flight altitude: " + (int)autopilotAltitude + " meters");
                    }
                }

                // ============================================
                // AUTO-NAVIGATION START (NumPad Multiply)
                // Starts auto-drive, aircraft autopilot, or auto-walk
                // depending on whether player is in vehicle, aircraft, or on foot
                // ============================================
                if (e.KeyCode == Keys.Multiply && !keyState[15])
                {
                    keyState[15] = true;

                    if (isAutodriving)
                    {
                        // Cancel any active auto-navigation
                        isAutodriving = false;
                        autodriveWanderMode = false;
                        // Clear tasks on whoever is driving (Butler or player)
                        if (guardDriverActive && bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive)
                            bodyguards[0].Task.ClearAllImmediately();
                        else
                            Game.Player.Character.Task.ClearAll();
                        if (autonavMode == "fly")
                            Tolk.Speak("Autopilot disengaged. You have control.");
                        else if (autonavMode == "walk")
                            Tolk.Speak("Auto-walk cancelled. You have control.");
                        else
                            Tolk.Speak("Auto-drive cancelled. You have control.");
                        autonavMode = "drive";
                    }
                    else if (Game.Player.Character.IsInVehicle())
                    {
                        Vehicle veh = Game.Player.Character.CurrentVehicle;
                        Ped driver = (guardDriverActive && bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive) ? bodyguards[0] : Game.Player.Character;
                        int vehClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, veh);

                        // Check if waypoint exists
                        bool hasWaypoint = Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE);

                        // vehClass 15 = Helicopters, 16 = Planes
                        if (vehClass == 15 || vehClass == 16)
                        {
                            // ============================================
                            // AIRCRAFT AUTOPILOT
                            // ============================================
                            autonavMode = "fly";
                            bool isHeli = (vehClass == 15);

                            if (hasWaypoint)
                            {
                                int waypointBlipHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
                                GTA.Math.Vector3 waypointPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, waypointBlipHandle);

                                // Set target altitude above ground level
                                float groundZ = World.GetGroundHeight(new GTA.Math.Vector2(waypointPos.X, waypointPos.Y));
                                if (groundZ > 0)
                                    waypointPos.Z = groundZ + autopilotAltitude;
                                else
                                    waypointPos.Z = autopilotAltitude;

                                autodriveDestination = waypointPos;

                                GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                                autodriveStartDistance = World.GetDistance(playerPos, waypointPos);

                                if (isHeli)
                                {
                                    // TASK_HELI_MISSION: ped, vehicle, targetVeh(0), targetPed(0), x, y, z, missionType, speed, radius, heading, maxAlt, minAlt, slowDownDist, behaviorFlags
                                    // MissionType 4 = go to coord
                                    Function.Call(Hash.TASK_HELI_MISSION,
                                        driver, veh, 0, 0,
                                        waypointPos.X, waypointPos.Y, waypointPos.Z,
                                        4,                    // mission type: go to coord
                                        autodriveSpeed,       // cruise speed
                                        20f,                  // target radius
                                        -1f,                  // heading (-1 = any)
                                        (int)(waypointPos.Z + 100), // max altitude
                                        (int)(waypointPos.Z - 50),  // min altitude
                                        -1f,                  // slow down distance
                                        0);                   // behavior flags
                                }
                                else
                                {
                                    // Plane — initialize phase machine and let ReissueFlightMission
                                    // pick the right mission (Takeoff/Cruise/Landing).
                                    planePhase = veh.IsOnAllWheels ? PlanePhase.Takeoff : PlanePhase.Cruise;
                                    planeLandingRunwayIndex = FindNearestRunwayIndex(waypointPos);
                                    autodriveDestination = waypointPos;
                                    ReissueFlightMission();
                                }

                                isAutodriving = true;
                                autodriveWanderMode = false;
                                autodriveCheckTicks = DateTime.Now.Ticks;

                                int speedMph = (int)Math.Round(autodriveSpeed * 2.23694);
                                string acType = isHeli ? "Helicopter" : "Plane";
                                string phaseStr = "";
                                if (!isHeli)
                                {
                                    if (planePhase == PlanePhase.Takeoff) phaseStr = " Taking off.";
                                    if (planeLandingRunwayIndex >= 0)
                                        phaseStr += " Will land at " + RUNWAYS[planeLandingRunwayIndex].name + ".";
                                }
                                Tolk.Speak(acType + " autopilot engaged. Flying to waypoint at " + speedMph + " mph. " + (int)autodriveStartDistance + " meters. Altitude: " + (int)autopilotAltitude + " meters." + phaseStr);
                            }
                            else
                            {
                                // No waypoint - circle/cruise in current area
                                GTA.Math.Vector3 currentPos = Game.Player.Character.Position;
                                float currentAlt = currentPos.Z;
                                if (currentAlt < autopilotAltitude)
                                    currentAlt = autopilotAltitude;

                                autodriveDestination = new GTA.Math.Vector3(currentPos.X, currentPos.Y, currentAlt);

                                if (isHeli)
                                {
                                    // Hover in place for helicopters
                                    Function.Call(Hash.TASK_HELI_MISSION,
                                        driver, veh, 0, 0,
                                        currentPos.X, currentPos.Y, currentAlt,
                                        4, autodriveSpeed, 50f, -1f,
                                        (int)(currentAlt + 100), (int)(currentAlt - 50),
                                        -1f, 0);
                                }
                                else
                                {
                                    // Plane circling — no waypoint means no landing target.
                                    planePhase = veh.IsOnAllWheels ? PlanePhase.Takeoff : PlanePhase.Cruise;
                                    planeLandingRunwayIndex = -1;
                                    autodriveDestination = new GTA.Math.Vector3(currentPos.X, currentPos.Y, currentAlt);
                                    ReissueFlightMission();
                                }

                                isAutodriving = true;
                                autodriveWanderMode = true;
                                autodriveCheckTicks = DateTime.Now.Ticks;

                                int speedMph = (int)Math.Round(autodriveSpeed * 2.23694);
                                string acType = isHeli ? "Helicopter hovering" : "Plane circling";
                                Tolk.Speak(acType + " at " + speedMph + " mph. Set a waypoint for a destination.");
                            }
                        }
                        else
                        {
                            // ============================================
                            // GROUND VEHICLE AUTO-DRIVE (existing behavior)
                            // ============================================
                            autonavMode = "drive";
                            int drivingStyle = GetDrivingStyleFromFlags();

                            // Set driver ability for better AI driving
                            Function.Call(Hash.SET_DRIVER_ABILITY, driver, 1.0f);
                            Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, 0.5f);

                            if (hasWaypoint)
                            {
                                // WAYPOINT MODE - Drive to waypoint
                                int waypointBlipHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
                                GTA.Math.Vector3 waypointPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, waypointBlipHandle);

                                // Get ground Z at waypoint for proper height
                                float groundZ = World.GetGroundHeight(new GTA.Math.Vector2(waypointPos.X, waypointPos.Y));
                                if (groundZ > 0) waypointPos.Z = groundZ;

                                autodriveDestination = waypointPos;

                                // Calculate distance
                                GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                                autodriveStartDistance = World.GetDistance(playerPos, waypointPos);

                                // Use TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE for long distances
                                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                                    driver, veh,
                                    waypointPos.X, waypointPos.Y, waypointPos.Z,
                                    autodriveSpeed, drivingStyle, 20f); // 20m stop distance

                                isAutodriving = true;
                                autodriveWanderMode = false;
                                autodriveCheckTicks = DateTime.Now.Ticks;

                                int speedMph = (int)Math.Round(autodriveSpeed * 2.23694);
                                string steerAssistMsg = getSetting("steeringAssist") > 0 ? " Steering assist disabled." : "";
                                Tolk.Speak("Auto-driving to waypoint at " + speedMph + " mph. " + (int)autodriveStartDistance + " meters." + steerAssistMsg);
                            }
                            else
                            {
                                // WANDER MODE - Drive randomly
                                try
                                {
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER,
                                        driver, veh,
                                        autodriveSpeed, drivingStyle);

                                    isAutodriving = true;
                                    autodriveWanderMode = true;
                                    autodriveCheckTicks = DateTime.Now.Ticks;

                                    int speedMph = (int)Math.Round(autodriveSpeed * 2.23694);
                                    string steerAssistMsg = getSetting("steeringAssist") > 0 ? " Steering assist disabled." : "";
                                    Tolk.Speak("Wandering at " + speedMph + " mph." + steerAssistMsg);
                                }
                                catch (Exception ex)
                                {
                                    Tolk.Speak("Wander error: " + ex.Message);
                                }
                            }
                        }
                    }
                    else
                    {
                        // ============================================
                        // ON-FOOT AUTO-WALK
                        // ============================================
                        autonavMode = "walk";
                        Ped player = Game.Player.Character;
                        bool hasWaypoint = Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE);

                        if (hasWaypoint)
                        {
                            int waypointBlipHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
                            GTA.Math.Vector3 waypointPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, waypointBlipHandle);

                            // Get ground Z at waypoint
                            float groundZ = World.GetGroundHeight(new GTA.Math.Vector2(waypointPos.X, waypointPos.Y));
                            if (groundZ > 0) waypointPos.Z = groundZ;

                            autodriveDestination = waypointPos;

                            GTA.Math.Vector3 playerPos = player.Position;
                            autodriveStartDistance = World.GetDistance(playerPos, waypointPos);

                            // TASK_GO_TO_COORD_ANY_MEANS: ped, x, y, z, speed, p5, p6, walkingStyle, p8
                            // Speed: 1.0 = walk, 2.0 = jog/run
                            float walkSpeed = Math.Min(autodriveSpeed, 4f); // Cap walk speed to reasonable limit (4 m/s = fast run)
                            Function.Call(Hash.TASK_GO_TO_COORD_ANY_MEANS,
                                player,
                                waypointPos.X, waypointPos.Y, waypointPos.Z,
                                walkSpeed,
                                0, 0, 0, 0f);

                            isAutodriving = true;
                            autodriveWanderMode = false;
                            autodriveCheckTicks = DateTime.Now.Ticks;

                            Tolk.Speak("Auto-walking to waypoint. " + (int)autodriveStartDistance + " meters.");
                        }
                        else
                        {
                            // Wander on foot
                            Function.Call(Hash.TASK_WANDER_STANDARD,
                                player, 10f, 0);

                            isAutodriving = true;
                            autodriveWanderMode = true;
                            autodriveCheckTicks = DateTime.Now.Ticks;

                            Tolk.Speak("Wandering on foot.");
                        }
                    }
                }

            }
        }

        private void onKeyUp(object sender, KeyEventArgs e)
        {
            if (!e.Control)
            {
                shifting = false;
            }
            if (e.KeyCode == Keys.NumPad0 && keyState[0])
                keyState[0] = false;
            if (e.KeyCode == Keys.NumPad1 && keyState[1])
                keyState[1] = false;
            if (e.KeyCode == Keys.NumPad2 && keyState[2])
                keyState[2] = false;
            if (e.KeyCode == Keys.NumPad3 && keyState[3])
                keyState[3] = false;
            if (e.KeyCode == Keys.NumPad4 && keyState[4])
                keyState[4] = false;
            if (e.KeyCode == Keys.NumPad5 && keyState[5])
                keyState[5] = false;
            if (e.KeyCode == Keys.NumPad6 && keyState[6])
                keyState[6] = false;
            if (e.KeyCode == Keys.NumPad7 && keyState[7])
                keyState[7] = false;
            if (e.KeyCode == Keys.NumPad8 && keyState[8])
                keyState[8] = false;
            if (e.KeyCode == Keys.NumPad9 && keyState[9])
                keyState[9] = false;
            if (e.KeyCode == Keys.Decimal && keyState[10])
                keyState[10] = false;
            if (e.KeyCode == Keys.Add && keyState[11])
                keyState[11] = false;
            if (e.KeyCode == Keys.Subtract && keyState[12])
                keyState[12] = false;
            if (e.KeyCode == Keys.Left && keyState[13])
                keyState[13] = false;
            if (e.KeyCode == Keys.Right && keyState[14])
                keyState[14] = false;
            if (e.KeyCode == Keys.Multiply && keyState[15])
                keyState[15] = false;
            if (e.KeyCode == Keys.Up && keyState[16])
                keyState[16] = false;
            if (e.KeyCode == Keys.Down && keyState[17])
                keyState[17] = false;
            if (e.KeyCode == Keys.NumPad0 && keyState[19])
                keyState[19] = false;
            if (e.KeyCode == Keys.F1 && keyState[18])
                keyState[18] = false;


        }


        private double calculate_x_y_angle(double x1, double y1, double x2, double y2, double deg)
        {
            double x = x1 - x2;
            double y = y2 - y1;
            double rad = 0;
            if (x == 0 || y == 0)
            {
                rad = Math.Atan(0);
            }
            else
            {
                rad = Math.Atan(y / x);
            }
            double arctan = rad / Math.PI * 180;
            double fdeg = 0;
            if (x > 0)
            {
                fdeg = 90 - arctan;
            }
            else if (x < 0)
            {
                fdeg = 270 - arctan;
            }
            if (x == 0)
            {
                if (y > 0)
                {
                    fdeg = 0;
                }
                else if (y < 0)
                {
                    fdeg = 180;
                }
            }
            fdeg -= deg;
            if (fdeg < 0)
            {
                fdeg += 360;
            }
            fdeg = Math.Floor(fdeg);
            return fdeg;
        }

        private string getDir(double facing)
        {
            if (facing >= north && facing < northnortheast)
            {
                return "north";
            }
            if (facing >= northnortheast && facing < northeast)
            {
                return "north-northwest";
            }

            if (facing >= northeast && facing < eastnortheast)
            {
                return "northwest";
            }
            if (facing >= eastnortheast && facing < east)
            {
                return "west-northwest";
            }

            if (facing >= east && facing < eastsoutheast)
            {
                return "west";
            }
            if (facing >= eastsoutheast && facing < southeast)
            {
                return "west-southwest";
            }

            if (facing >= southeast && facing < southsoutheast)
            {
                return "southwest";
            }
            if (facing >= southsoutheast && facing < south)
            {
                return "south-southwest";
            }
            if (facing >= south && facing < southsouthwest)
            {
                return "south";
            }
            if (facing >= southsouthwest && facing < west)
            {
                return "south-southeast";
            }
            if (facing >= southwest && facing < westsouthwest)
            {
                return "southeast";
            }
            if (facing >= westsouthwest && facing < west)
            {
                return "east-southeast";
            }
            if (facing >= west && facing < westnorthwest)
            {
                return "east";
            }
            if (facing >= westnorthwest && facing < northwest)
            {
                return "east-northeast";
            }
            if (facing >= northwest && facing < northnorthwest)
            {
                return "northeast";
            }
            if (facing >= northnorthwest)
            {
                return "north-northeast";
            }
            return "";

        }

        private double fixHeading(double heading)
        {
            double new_heading = 0;

            if (heading <= 180)
            {
                new_heading = heading + 180;
            }
            else if (heading > 180)
            {
                new_heading = heading - 180;
            }

            return new_heading;
        }

        private double GetAngleOfLineBetweenTwoPoints(GTA.Math.Vector3 p1, GTA.Math.Vector3 p2)
        {
            double xDiff = p2.X - p1.X;
            double yDiff = p2.Y - p1.Y;
            return Math.Atan2(yDiff, xDiff) * (180 / Math.PI) + 180;
        }

        private int headingSlice(double heading)
        {

            if (heading >= 0 && heading < 45)
                return 0;
            if (heading >= 45 && heading < 90)
                return 1;
            if (heading >= 90 && heading < 135)
                return 2;
            if (heading >= 135 && heading < 180)
                return 3;
            if (heading >= 180 && heading < 225)
                return 4;
            if (heading >= 225 && heading < 270)
                return 5;
            if (heading >= 270 && heading < 315)
                return 6;
            if (heading >= 315)
                return 7;
            return -1;
        }

        private string headingSliceName(double heading)
        {
            if (headingSlice(heading) == 0)
                return ("north");
            if (headingSlice(heading) == 1)
                return ("northwest");
            if (headingSlice(heading) == 2)
                return ("west");
            if (headingSlice(heading) == 3)
                return ("southwest");
            if (headingSlice(heading) == 4)
                return ("south");
            if (headingSlice(heading) == 5)
                return ("southeast");
            if (headingSlice(heading) == 6)
                return ("east");
            if (headingSlice(heading) == 7)
                return ("northeast");
            return "None";

        }

        public string listToString(List<Result> results, string prependedText = "")
        {
            string text = prependedText;
            string vertical = "";

            Result[] r = results.ToArray();
            Array.Sort(r);
            foreach (Result i in r)
            {
                if (i.zDistance != 0)
                {
                    if (i.zDistance > 0)
                    {
                        vertical = " " + Math.Abs(i.zDistance) + " meters above , ";
                    }
                    else
                    {
                        vertical = " " + Math.Abs(i.zDistance) + " meters below, ";
                    }


                }
                text = text + i.xyDistance + " meters " + i.direction + ", " + vertical + i.name + ". ";
            }
            return (text);

        }

        private void speakMenu()
        {
            string result = mainMenu[mainMenuIndex];
            if (mainMenuIndex == 0)
                result = result + locations[locationMenuIndex].name;
            if (mainMenuIndex == 1)
                result = result + spawns[spawnMenuIndex].name;
            if (mainMenuIndex == 2)
                result = result + funMenu[funMenuIndex];
            // Auto-Drive menu - flag-based
            if (mainMenuIndex == 3)
            {
                if (isAutodriving)
                {
                    string modeStr;
                    if (autonavMode == "fly")
                        modeStr = autodriveWanderMode ? "hovering" : "flying to waypoint";
                    else if (autonavMode == "walk")
                        modeStr = autodriveWanderMode ? "wandering on foot" : "walking to waypoint";
                    else
                        modeStr = autodriveWanderMode ? "wandering" : "driving to waypoint";
                    if (autodriveFlagMenuIndex == 35)
                        result = result + "Refresh Driving Task. Currently " + modeStr + ". Press NumPad 2 to refresh.";
                    else
                        result = result + "Currently " + modeStr + ". Press NumPad 2 to cancel.";
                }
                else
                {
                    int speedMph = (int)Math.Round(autodriveSpeed * 2.23694);
                    if (autodriveFlagMenuIndex == 32)
                    {
                        result = result + "Land. Select to land the helicopter. Speed: " + speedMph + " mph. Flight altitude: " + (int)autopilotAltitude + " meters.";
                    }
                    else if (autodriveFlagMenuIndex == 33)
                    {
                        result = result + "Park at Nearest Safe Spot. Select to park and end driving. Speed: " + speedMph + " mph.";
                    }
                    else if (autodriveFlagMenuIndex == 34)
                    {
                        result = result + "Hitch to Nearest Trailer. Select to attach the nearest trailer within 20 meters.";
                    }
                    else if (autodriveFlagMenuIndex == 35)
                    {
                        result = result + "Refresh Driving Task. Select to re-issue the active driving or flight task.";
                    }
                    else
                    {
                    // Show current flag and state, plus speed
                    string flagState = autodriveFlags[autodriveFlagMenuIndex] ? "ON" : "OFF";
                    result = result + "Flag " + (autodriveFlagMenuIndex + 1) + " of 36: " + autodriveFlagNames[autodriveFlagMenuIndex] + ", " + flagState + ". Speed: " + speedMph + " mph. Flight altitude: " + (int)autopilotAltitude + " meters. NumPad Multiply to start. Left/Right arrows adjust speed. Up/Down arrows adjust flight altitude. Works in vehicles, aircraft, and on foot.";
                    }
                }
            }
            // Settings menu
            if (mainMenuIndex == 4)
            {
                // Special handling for detection radius
                if (settingsMenu[settingsMenuIndex].id == "detectionRadius")
                {
                    int radiusIndex = settingsMenu[settingsMenuIndex].value;
                    if (radiusIndex < 0 || radiusIndex >= detectionRadiusOptions.Length)
                        radiusIndex = 1;
                    float radius = detectionRadiusOptions[radiusIndex];
                    result = result + "Detection Radius: " + (int)radius + " meters";
                }
                else
                {
                    string toggle = "";
                    if (settingsMenu[settingsMenuIndex].value == 0)
                        toggle = "Off";
                    if (settingsMenu[settingsMenuIndex].value == 1)
                        toggle = "On";
                    result = result + settingsMenu[settingsMenuIndex].displayName + toggle;
                }
            }
            // Bodyguard menu
            if (mainMenuIndex == 5)
            {
                result = result + GetBodyguardMenuText(bodyguardMenuIndex);
            }
            // Status menu
            if (mainMenuIndex == 6)
            {
                result = result + "Item " + (statusMenuIndex + 1) + " of " + STATUS_MENU_ITEM_COUNT + ". " + GetStatusMenuText(statusMenuIndex);
                if (statusMonitoredItems.Contains(statusMenuIndex))
                    result += " Monitoring.";
                if (statusMonitoredItems.Count > 0)
                    result += " " + statusMonitoredItems.Count + " item" + (statusMonitoredItems.Count > 1 ? "s" : "") + " monitored.";
            }
            // Request Plane Flight menu
            if (mainMenuIndex == 7)
            {
                result = result + GetFlightMenuText(planeFlightMenuIndex);
            }

            Tolk.Speak(result, true);

        }

        void setupSettings()
        {
            Dictionary<string, int> dictionary = new Dictionary<string, int>();
            string json;
            string[] ids = { "announceHeadings", "announceZones", "announceTime", "altitudeIndicator", "targetPitchIndicator", "navigationAssist", "navAssistBeeps", "pickupDetection", "coverDetection", "waterHazardDetection", "vehicleHealthFeedback", "staminaFeedback", "interactableDetection", "trafficAwareness", "wantedLevelDetails", "slopeTerrainFeedback", "turnByTurnNavigation", "serviceProximity", "detectionRadius", "radioOff", "warpInsideVehicle", "onscreen", "speed", "godMode", "policeIgnore", "vehicleGodMode", "amphibiousMode", "infiniteAmmo", "neverWanted", "superJump", "runFaster", "swimFaster", "exsplosiveAmmo", "fireAmmo", "explosiveMelee", "aimAutolock", "steeringAssist", "shapeCasting", "roadTeleport", "waypointDriveAssist", "hapticFeedback", "adaptiveCruise", "bodyguardAutoRespawn", "driveAssistDebugLog" };
            System.IO.StreamWriter fileOut;

            if (!System.IO.Directory.Exists(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings"))
                System.IO.Directory.CreateDirectory(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings");

            if (!System.IO.File.Exists(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/gta11ySettings.json"))
            {
                fileOut = new System.IO.StreamWriter(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/gta11ySettings.json");

                foreach (string i in ids)
                {
                    if (i == "announceHeadings" || i == "announceZones" || i == "altitudeIndicator" || i == "announceTime" || i == "turnByTurnNavigation" || i == "navAssistBeeps")
                    {
                        dictionary.Add(i, 1);
                    }
                    else if (i == "detectionRadius")
                    {
                        dictionary.Add(i, 1); // Default to 25m (index 1)
                    }
                    else
                    {
                        dictionary.Add(i, 0);
                    }

                }

                json = JsonConvert.SerializeObject(dictionary, Formatting.Indented);
                fileOut.Write(json);
                fileOut.Close();
            }
            dictionary.Clear();
            System.IO.StreamReader fileIn = new System.IO.StreamReader(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/gta11ySettings.json");
            json = fileIn.ReadToEnd();
            fileIn.Close();
            try
            {
                dictionary = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
            }
            catch
            {
                System.IO.File.Delete(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/gta11ySettings.json");
                setupSettings();
            }
            try
            {
                settingsMenu.Clear();
                foreach (string i in ids)
                {
                    if (dictionary.ContainsKey(i))
                    {
                        settingsMenu.Add(new Setting(i, idToName(i), dictionary[i]));
                    }
                    else
                    {
                        if (i == "announceHeadings" || i == "announceZones" || i == "altitudeIndicator" || i == "announceTime" || i == "targetPitchIndicator" || i == "speed" || i == "turnByTurnNavigation" || i == "navAssistBeeps")
                        {
                            settingsMenu.Add(new Setting(i, idToName(i), 1));
                        }
                        else if (i == "detectionRadius")
                        {
                            settingsMenu.Add(new Setting(i, idToName(i), 1)); // Default to 25m
                        }
                        else
                        {
                            settingsMenu.Add(new Setting(i, idToName(i), 0));
                        }

                    }
                }


            }
            catch
            {
                System.IO.File.Delete(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/gta11ySettings.json");
                setupSettings();
            }

            // ---- vehicleaihandlinginfo.meta loader (iter-8) ----
            // Drive-assist consults this for per-class curve target speeds and
            // brake-lookahead distances. See VehicleAIHandling.cs for details.
            try
            {
                string modSettingsFolder = @Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    + "/Rockstar Games/GTA V/ModSettings";
                string modDllFolder = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                VehicleAIHandlingRegistry.LoadOrFallback(modDllFolder, modSettingsFolder);
            }
            catch
            {
                // Non-fatal: GetForVehicle() will lazy-init the hardcoded
                // fallback if the loader didn't get a chance to run.
            }
        }

        void saveSettings()
        {
            Dictionary<string, int> dictionary = new Dictionary<string, int>();
            foreach (Setting i in settingsMenu)
            {
                dictionary.Add(i.id, i.value);
            }
            if (!System.IO.Directory.Exists(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/"))
                System.IO.Directory.CreateDirectory(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games / GTA V / ModSettings/");
            System.IO.StreamWriter fileOut = new System.IO.StreamWriter(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/gta11ySettings.json");
            string result = JsonConvert.SerializeObject(dictionary, Formatting.Indented);
            fileOut.Write(result);
            fileOut.Close();
        }

        // ============================================
        // AIM AUTOLOCK HELPER METHODS
        // ============================================

        /// <summary>
        /// Announces the locked target via Tolk screen reader
        /// </summary>
        private void AnnounceAutolockTarget(Entity target)
        {
            if (target == null) return;
            string announcement = "";

            if (target.EntityType == EntityType.Ped)
            {
                Ped ped = (Ped)target;
                int pedType = Function.Call<int>(Hash.GET_PED_TYPE, ped);
                string pedTypeName = GetPedTypeName(pedType);
                announcement = pedTypeName + ", targeting " + PED_TARGET_PARTS[autolockPartIndex].name;
            }
            else if (target.EntityType == EntityType.Vehicle)
            {
                Vehicle vehicle = (Vehicle)target;
                string vehName = vehicle.LocalizedName;
                if (string.IsNullOrEmpty(vehName) || vehName == "NULL")
                    vehName = vehicle.DisplayName;

                int occupantCount = 0;
                if (vehicle.Driver != null && vehicle.Driver.IsAlive) occupantCount++;
                for (int seat = 0; seat < vehicle.PassengerCapacity; seat++)
                {
                    Ped passenger = vehicle.GetPedOnSeat((VehicleSeat)seat);
                    if (passenger != null && passenger.IsAlive) occupantCount++;
                }

                string occupantStr = occupantCount > 0
                    ? " with " + occupantCount + " occupant" + (occupantCount > 1 ? "s" : "")
                    : " (empty)";
                announcement = vehName + occupantStr + ", targeting " + VEHICLE_TARGET_PARTS[autolockPartIndex].name;
            }

            if (!string.IsNullOrEmpty(announcement))
                Tolk.Speak(announcement, true);
        }

        /// <summary>
        /// Converts GTA ped type ID to human-readable name
        /// </summary>
        private string GetPedTypeName(int pedType)
        {
            switch (pedType)
            {
                case 6: return "Cop";
                case 21: return "Security guard";
                case 23: return "SWAT";
                case 24: return "FIB agent";
                case 27: return "Bodyguard";
                case 28: return "Army";
                case 29: return "Paramedic";
                case 30: return "Firefighter";
                case 1: return "Male civilian";
                case 2: return "Female civilian";
                case 3: case 4: case 5: return "Gang member";
                default: return "Person";
            }
        }

        /// <summary>
        /// Updates camera/aim to track the current target part
        /// </summary>
        /// <param name="instantSnap">If true, instantly snap to target instead of smooth lerp</param>
        private void UpdateAutolockAim(bool instantSnap = false)
        {
            if (autolockTarget == null || autolockTarget.IsDead) return;

            GTA.Math.Vector3 targetPos = GetTargetPartPosition();
            if (targetPos == GTA.Math.Vector3.Zero) return;

            GTA.Math.Vector3 camPos = GTA.GameplayCamera.Position;
            GTA.Math.Vector3 direction = GTA.Math.Vector3.Normalize(targetPos - camPos);

            float targetHeading = (float)(Math.Atan2(direction.X, direction.Y) * (180.0 / Math.PI));
            float targetPitch = (float)(Math.Asin(direction.Z) * (180.0 / Math.PI));

            float currentHeading = GTA.GameplayCamera.RelativeHeading;
            float currentPitch = GTA.GameplayCamera.RelativePitch;

            float headingDelta = targetHeading - Game.Player.Character.Heading - currentHeading;
            float pitchDelta = targetPitch - currentPitch;

            while (headingDelta > 180) headingDelta -= 360;
            while (headingDelta < -180) headingDelta += 360;

            // Use instant snap (1.0) when re-acquiring target, smooth lerp during normal tracking
            // Frame-rate independent: ~10 units per second tracking speed
            float lerpFactor = instantSnap ? 1.0f : Math.Min(1.0f, 10.0f * deltaTime);
            float adjustedHeading = currentHeading + (headingDelta * lerpFactor);
            float adjustedPitch = Math.Max(-70f, Math.Min(70f, currentPitch + (pitchDelta * lerpFactor)));

            Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_HEADING, adjustedHeading);
            Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_PITCH, adjustedPitch, 1f);
        }

        /// <summary>
        /// Gets the world position of the currently selected target part
        /// </summary>
        private GTA.Math.Vector3 GetTargetPartPosition()
        {
            if (autolockTarget == null) return GTA.Math.Vector3.Zero;

            try
            {
                if (autolockTarget.EntityType == EntityType.Ped)
                {
                    Ped ped = (Ped)autolockTarget;
                    int boneId = PED_TARGET_PARTS[autolockPartIndex].boneId;
                    GTA.Math.Vector3 bonePos = Function.Call<GTA.Math.Vector3>(
                        Hash.GET_PED_BONE_COORDS, ped, boneId, 0f, 0f, 0f);
                    return bonePos != GTA.Math.Vector3.Zero ? bonePos : ped.Position;
                }
                else if (autolockTarget.EntityType == EntityType.Vehicle)
                {
                    Vehicle vehicle = (Vehicle)autolockTarget;
                    string boneName = VEHICLE_TARGET_PARTS[autolockPartIndex].bone;
                    int boneIndex = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, vehicle, boneName);

                    if (boneIndex != -1)
                    {
                        GTA.Math.Vector3 bonePos = Function.Call<GTA.Math.Vector3>(
                            Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, vehicle, boneIndex);
                        if (bonePos != GTA.Math.Vector3.Zero) return bonePos;
                    }
                    return vehicle.Position;
                }
            }
            catch { }

            // Bone lookup failed; fall back to entity position, guarding against the
            // rare case where the target was freed between the null check at the top
            // of the function and the catch fall-through.
            if (autolockTarget == null || !autolockTarget.Exists()) return GTA.Math.Vector3.Zero;
            return autolockTarget.Position;
        }

        /// <summary>
        /// Gets the position of a specific part index for visibility checking
        /// </summary>
        private GTA.Math.Vector3 GetPartPositionByIndex(int partIndex)
        {
            if (autolockTarget == null) return GTA.Math.Vector3.Zero;

            try
            {
                if (autolockTarget.EntityType == EntityType.Ped)
                {
                    Ped ped = (Ped)autolockTarget;
                    int boneId = PED_TARGET_PARTS[partIndex].boneId;
                    GTA.Math.Vector3 bonePos = Function.Call<GTA.Math.Vector3>(
                        Hash.GET_PED_BONE_COORDS, ped, boneId, 0f, 0f, 0f);
                    return bonePos != GTA.Math.Vector3.Zero ? bonePos : ped.Position;
                }
                else if (autolockTarget.EntityType == EntityType.Vehicle)
                {
                    Vehicle vehicle = (Vehicle)autolockTarget;
                    string boneName = VEHICLE_TARGET_PARTS[partIndex].bone;
                    int boneIndex = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, vehicle, boneName);

                    if (boneIndex != -1)
                    {
                        GTA.Math.Vector3 bonePos = Function.Call<GTA.Math.Vector3>(
                            Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, vehicle, boneIndex);
                        if (bonePos != GTA.Math.Vector3.Zero) return bonePos;
                    }
                    return vehicle.Position;
                }
            }
            catch { }

            // Bone lookup failed; fall back to entity position, guarding against the
            // rare case where the target was freed between the null check at the top
            // of the function and the catch fall-through.
            if (autolockTarget == null || !autolockTarget.Exists()) return GTA.Math.Vector3.Zero;
            return autolockTarget.Position;
        }

        /// <summary>
        /// Checks if a specific target part is visible from the player's position using raycast
        /// Returns true if there's a clear line of sight to the part
        /// </summary>
        private bool IsPartVisible(int partIndex)
        {
            if (autolockTarget == null) return false;

            GTA.Math.Vector3 partPos = GetPartPositionByIndex(partIndex);
            if (partPos == GTA.Math.Vector3.Zero) return false;

            // Use camera position as origin (where bullets come from when aiming)
            GTA.Math.Vector3 camPos = GTA.GameplayCamera.Position;

            // Perform raycast from camera to target part
            // Flags: 1 = map, 2 = vehicles, 4 = peds, 8 = objects, 16 = plants
            // We want to check for vehicle/world blocking the view
            // Use IntersectWorld flag (1) to detect if part is on the other side of the vehicle
            RaycastResult ray = World.Raycast(camPos, partPos, IntersectFlags.Map | IntersectFlags.Vehicles, Game.Player.Character);

            if (ray.DidHit)
            {
                // Check if the ray hit the target entity itself or something blocking it
                if (ray.HitEntity != null && ray.HitEntity == autolockTarget)
                {
                    // Hit the target - check if we hit close to the part we're aiming at
                    float hitDistance = camPos.DistanceTo(ray.HitPosition);
                    float partDistance = camPos.DistanceTo(partPos);

                    // If the hit position is close to the part position, part is visible
                    // Allow some tolerance (2 meters) since bones might not be on the surface
                    if (ray.HitPosition.DistanceTo(partPos) < 2.5f)
                    {
                        return true;
                    }

                    // If we hit the target but not near the part, the part is on the other side
                    // (e.g., trying to hit left wheel from right side - ray hits the right side first)
                    return false;
                }
                else
                {
                    // Hit something else blocking the view (world geometry, another vehicle)
                    return false;
                }
            }

            // No hit means clear line of sight (shouldn't happen often)
            return true;
        }

        /// <summary>
        /// Finds the next visible part index, skipping non-visible parts
        /// Returns -1 if no visible parts found
        /// </summary>
        private int FindNextVisiblePart(int startIndex, int direction)
        {
            if (autolockTarget == null) return -1;

            int maxParts = autolockTarget.EntityType == EntityType.Ped
                ? PED_TARGET_PARTS.Length
                : VEHICLE_TARGET_PARTS.Length;

            // Try each part starting from the given index
            for (int i = 0; i < maxParts; i++)
            {
                int checkIndex = (startIndex + (direction * i) + maxParts) % maxParts;
                if (IsPartVisible(checkIndex))
                {
                    return checkIndex;
                }
            }

            // No visible parts found - fallback to center/torso (index 0 for vehicles, 1 for peds)
            return autolockTarget.EntityType == EntityType.Ped ? 1 : 0;
        }

        /// <summary>
        /// Handles right stick input for cycling through target parts
        /// Skips parts that are not visible from the player's position (e.g., gas tank on opposite side)
        /// </summary>
        private void HandlePartCycling()
        {
            if (autolockTarget == null) return;
            if (DateTime.Now.Ticks - autolockPartCycleTicks < 2000000) return; // 200ms debounce

            // Use GET_DISABLED_CONTROL_NORMAL since we disabled the control action
            // This still reads the input value even though default game action is suppressed
            float rightStickX = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, 220);

            int maxParts = autolockTarget.EntityType == EntityType.Ped
                ? PED_TARGET_PARTS.Length
                : VEHICLE_TARGET_PARTS.Length;

            bool changed = false;
            int direction = 0;
            int originalIndex = autolockPartIndex;

            if (rightStickX < -0.5f && !autolockPartCycleLeft)
            {
                autolockPartCycleLeft = true;
                direction = -1;
                changed = true;
            }
            else if (rightStickX >= -0.5f)
            {
                autolockPartCycleLeft = false;
            }

            if (rightStickX > 0.5f && !autolockPartCycleRight)
            {
                autolockPartCycleRight = true;
                direction = 1;
                changed = true;
            }
            else if (rightStickX <= 0.5f)
            {
                autolockPartCycleRight = false;
            }

            if (changed && direction != 0)
            {
                autolockPartCycleTicks = DateTime.Now.Ticks;

                // Find the next visible part in the given direction
                // Start from the next part in that direction
                int startIndex = (autolockPartIndex + direction + maxParts) % maxParts;
                int foundIndex = -1;

                // Search through all parts to find the next visible one
                for (int i = 0; i < maxParts; i++)
                {
                    int checkIndex = (startIndex + (direction * i) + maxParts) % maxParts;
                    if (IsPartVisible(checkIndex))
                    {
                        foundIndex = checkIndex;
                        break;
                    }
                }

                // If no visible part found, stay on current or fallback to center
                if (foundIndex == -1)
                {
                    // Play a lower "blocked" beep to indicate no other parts visible
                    outPartCycle.Stop();
                    partCycleBeep.Frequency = 300;
                    var blockedSample = partCycleBeep.Take(TimeSpan.FromSeconds(0.1));
                    outPartCycle.Init(blockedSample);
                    outPartCycle.Play();
                    Tolk.Speak("No other visible parts", true);
                    return;
                }

                // Update to the new visible part
                autolockPartIndex = foundIndex;

                outPartCycle.Stop();
                partCycleBeep.Frequency = 600 + (autolockPartIndex * 80);
                var sample = partCycleBeep.Take(TimeSpan.FromSeconds(0.05));
                outPartCycle.Init(sample);
                outPartCycle.Play();

                string partName = autolockTarget.EntityType == EntityType.Ped
                    ? PED_TARGET_PARTS[autolockPartIndex].name
                    : VEHICLE_TARGET_PARTS[autolockPartIndex].name;
                Tolk.Speak(partName, true);
            }
        }

        // ============================================
        // SMART STEERING ASSISTS HELPER METHODS
        // ============================================

        /// <summary>
        /// Main processing method for steering assist - scans for threats and applies corrections
        /// </summary>
        // ============================================
        // STEERING ASSIST v3 GATING HELPERS
        // ============================================

        /// <summary>
        /// Half-width of the player vehicle, used for the brake-threat lateral cone.
        /// Approximated from the existing collision-radius accessor (which scales
        /// with vehicle class), so a bus has a wider cone than a motorcycle.
        /// </summary>
        private float GetVehicleHalfWidth(Vehicle veh)
        {
            // BASE_COLLISION_RADIUS encodes a generous bounding radius; multiply by
            // ~0.42 to recover an approximate half-width. Adjusted by class via the
            // existing accessor for free.
            return Math.Max(0.6f, GetVehicleCollisionRadius(veh) * 0.42f);
        }

        /// <summary>
        /// Checks whether an obstacle sits inside the player's forward "brake cone"
        /// — i.e. its lateral offset (in vehicle frame) is within ±(half-width + 0.5 m).
        /// Adjacent-lane parked cars fail this and so are excluded from brake triggers
        /// while still appearing to side-clearance / steering logic.
        /// </summary>
        private bool IsInBrakeCone(Vehicle playerVeh, GTA.Math.Vector3 obstaclePos)
        {
            GTA.Math.Vector3 toObs = obstaclePos - playerVeh.Position;
            GTA.Math.Vector3 fwd = isReversing ? -playerVeh.ForwardVector : playerVeh.ForwardVector;
            GTA.Math.Vector3 right = isReversing ? -playerVeh.RightVector : playerVeh.RightVector;
            if (GTA.Math.Vector3.Dot(fwd, toObs) <= 0f) return false; // not ahead
            float lateral = Math.Abs(GTA.Math.Vector3.Dot(right, toObs));
            return lateral <= GetVehicleHalfWidth(playerVeh) + 0.5f;
        }

        /// <summary>
        /// Entity-age + monotonic-drop gate for entity-derived brake threats. Returns
        /// true if this entity has been visible long enough AND its TTC has been
        /// dropping for the required number of cycles. Single-frame spikes from
        /// stream-in or normal jitter fail this check.
        /// </summary>
        private bool BrakeThreatPassesEntityGates(int handle, float ttc, long nowTicks, bool isPedestrian, float playerSpeedMs)
        {
            // Track first-seen time.
            if (!entityFirstSeenTicks.ContainsKey(handle))
            {
                entityFirstSeenTicks[handle] = nowTicks;
                entityLastTtc[handle] = ttc;
                entityMonotonicCount[handle] = 0;
                // First sighting is silent — too noisy to log every new
                // entity passing the cone gate; would flood the log in
                // any traffic situation.
                return false;
            }

            long ageMs = (nowTicks - entityFirstSeenTicks[handle]) / 10000;
            if (nowTicks - entityFirstSeenTicks[handle] < ENTITY_AGE_MIN_TICKS)
            {
                LogBrakeReject(handle, "age-too-young ageMs=" + ageMs + " needed="
                    + (ENTITY_AGE_MIN_TICKS / 10000), nowTicks);
                return false;
            }

            // Iter-9 Patch B: high-speed imminent-threat bypass. The 3-frame
            // monotonic-drop gate adds ~150 ms latency. At 30 m/s closing on
            // a stopped lead vehicle, that's 4.5 m of distance lost before
            // brake arms (markers F1591, F9672, F9785, F8350 in driveassist-
            // 2026-05-25-180748 are this scenario). When the player is
            // already at meaningful speed AND TTC is below 1.0 s, the
            // monotonic streak isn't worth waiting for — arm now. The
            // entity-age floor (100 ms, above) still protects against
            // stream-in false positives.
            if (ttc < 1.0f && ttc > 0f && playerSpeedMs > 8f)
            {
                // Still update the streak fields so we don't double-count if
                // monotonic logic later sees this entity.
                float prevTtc = entityLastTtc.ContainsKey(handle) ? entityLastTtc[handle] : ttc;
                entityLastTtc[handle] = ttc;
                if (entityMonotonicCount.ContainsKey(handle))
                    entityMonotonicCount[handle] = Math.Max(entityMonotonicCount[handle], BRAKE_MONOTONIC_FRAMES);
                else
                    entityMonotonicCount[handle] = BRAKE_MONOTONIC_FRAMES;
                return true;
            }

            // Monotonic-drop count: increment if TTC dropped from last seen sample.
            // Vehicles use symmetric thresholds (+/-0.04 s) — that's the Bug E
            // fix from the prior iteration which stopped stale traffic from
            // randomly tripping brake.
            // Pedestrians use a slightly asymmetric pair (drop 0.025 / reset
            // 0.05): driveassist-2026-05-25-111452 showed visible peds at
            // 5-12 m failing to trip brake because the symmetric 0.04 gate
            // was too conservative for slow-streaming-in peds whose TTC
            // samples are noisy on first acquisition. Peds move at 1-2 m/s,
            // so an asymmetric threshold here can't generate phantom approach
            // trends (their actual TTC trend on approach is monotonically
            // strong).
            float dropThresh  = isPedestrian ? 0.025f : 0.04f;
            float resetThresh = isPedestrian ? 0.05f  : 0.04f;
            float prev = entityLastTtc.ContainsKey(handle) ? entityLastTtc[handle] : ttc;
            int count = entityMonotonicCount.ContainsKey(handle) ? entityMonotonicCount[handle] : 0;
            bool resetByRise = ttc > prev + resetThresh;
            if (ttc < prev - dropThresh) count++;
            else if (resetByRise) count = 0;
            entityLastTtc[handle] = ttc;
            entityMonotonicCount[handle] = count;

            bool passed = count >= BRAKE_MONOTONIC_FRAMES;
            if (!passed)
            {
                // Distinguish "TTC rising — entity moving away" from
                // "monotonic streak still building." Both are valid reasons
                // brake didn't arm; logging the distinction lets us spot a
                // gate misconfiguration in the next log.
                if (resetByRise)
                    LogBrakeReject(handle, "ttc-rising prev=" + prev.ToString("F2")
                        + " curr=" + ttc.ToString("F2"), nowTicks);
                else
                    LogBrakeReject(handle, "monotonic-streak count=" + count
                        + " needed=" + BRAKE_MONOTONIC_FRAMES
                        + " ttc=" + ttc.ToString("F2"), nowTicks);
            }
            return passed;
        }

        // Iter-9 Patch H: throttled log emission for brake-gate rejections.
        // Callers of BrakeThreatPassesEntityGates have already gated the
        // entity on "forward + in cone + ttc better than current best", so
        // anything that reaches this function is a serious candidate; that's
        // why a "didn't pass" outcome warrants logging. 1 s per-entity cooldown
        // prevents sustained tailing from spamming.
        private void LogBrakeReject(int handle, string reason, long nowTicks)
        {
            if (driveLogger == null || !driveLogger.IsRunning) return;
            long last;
            if (entityLastRejectLogTicks.TryGetValue(handle, out last)
                && nowTicks - last < BRAKE_REJECT_LOG_COOLDOWN_TICKS) return;
            entityLastRejectLogTicks[handle] = nowTicks;
            driveLogger.Write("[F" + driveLogFrameCount
                + "] EVENT brake-reject: entity=" + handle + " reason=" + reason);
        }

        /// <summary>
        /// Prunes stale per-entity tracking dictionaries so they don't grow without
        /// bound. Called periodically from ProcessSteeringAssist.
        /// </summary>
        private void PruneEntityTracking(long nowTicks)
        {
            if (nowTicks - entityTrackingLastPrune < 50000000) return; // 5 s
            entityTrackingLastPrune = nowTicks;
            const long STALE_TICKS = 30000000; // 3 s
            var stale = new List<int>();
            foreach (var kv in entityFirstSeenTicks)
                if (nowTicks - kv.Value > STALE_TICKS) stale.Add(kv.Key);
            foreach (int h in stale)
            {
                entityFirstSeenTicks.Remove(h);
                entityLastTtc.Remove(h);
                entityMonotonicCount.Remove(h);
                entityLastRejectLogTicks.Remove(h);
            }
            var staleLatch = new List<int>();
            foreach (var kv in adjacentLatchUntilTicks)
                if (nowTicks > kv.Value) staleLatch.Add(kv.Key);
            foreach (int h in staleLatch) adjacentLatchUntilTicks.Remove(h);
        }

        /// <summary>
        /// Refreshes the adjacent-vehicle latch by scanning nearby vehicles. A
        /// vehicle whose position falls inside the player's "shoulder band"
        /// (±ADJACENT_LATERAL_BAND lateral, -ADJACENT_LONG_BACK..+ADJACENT_LONG_FRONT
        /// longitudinal) is recorded with a latch expiry of now + ADJACENT_LATCH_TICKS.
        /// CalculateLateralAvoidance consults the latch to suppress steering
        /// toward adjacent vehicles, eliminating side-brushing.
        /// </summary>
        private void UpdateAdjacentLatch(Vehicle playerVeh, Vehicle[] nearbyVehs, long nowTicks)
        {
            adjacentLatchedLeft = false;
            adjacentLatchedRight = false;
            GTA.Math.Vector3 playerPos = playerVeh.Position;
            GTA.Math.Vector3 fwd = isReversing ? -playerVeh.ForwardVector : playerVeh.ForwardVector;
            GTA.Math.Vector3 right = isReversing ? -playerVeh.RightVector : playerVeh.RightVector;

            foreach (Vehicle veh in nearbyVehs)
            {
                if (veh == null || veh == playerVeh) continue;
                GTA.Math.Vector3 toVeh = veh.Position - playerPos;
                float longComp = GTA.Math.Vector3.Dot(fwd, toVeh);
                float latComp = GTA.Math.Vector3.Dot(right, toVeh);
                bool inBand = longComp > -ADJACENT_LONG_BACK
                              && longComp < ADJACENT_LONG_FRONT
                              && Math.Abs(latComp) < ADJACENT_LATERAL_BAND;
                int h = veh.Handle;
                if (inBand)
                {
                    adjacentLatchUntilTicks[h] = nowTicks + ADJACENT_LATCH_TICKS;
                    if (latComp > 0) adjacentLatchedRight = true;
                    else adjacentLatchedLeft = true;
                }
                else if (adjacentLatchUntilTicks.ContainsKey(h)
                         && nowTicks <= adjacentLatchUntilTicks[h])
                {
                    // Still latched from prior pass — clear only when truly separated.
                    if (Math.Abs(latComp) < ADJACENT_CLEAR_LATERAL)
                    {
                        if (latComp > 0) adjacentLatchedRight = true;
                        else adjacentLatchedLeft = true;
                    }
                    else
                    {
                        adjacentLatchUntilTicks.Remove(h);
                    }
                }
            }
        }

        private void ProcessSteeringAssist(Vehicle playerVeh, bool isFullMode)
        {
            GTA.Math.Vector3 playerPos = playerVeh.Position;
            GTA.Math.Vector3 forwardVec = playerVeh.ForwardVector;
            GTA.Math.Vector3 rightVec = playerVeh.RightVector;
            float vehicleSpeed = playerVeh.Speed;

            // ============================================
            // VEHICLE TYPE CHECK - Skip aircraft and boats
            // Drive assist only makes sense for land vehicles
            // ============================================
            if (playerVeh.IsAircraft || playerVeh.IsBoat)
            {
                steeringAssistActive = false;
                return;
            }

            // ============================================
            // REVERSE DRIVING DETECTION
            // Check if player is driving backwards based on velocity vs forward vector
            // ============================================
            GTA.Math.Vector3 velocity = playerVeh.Velocity;
            if (velocity.Length() > 1f)
            {
                float dotVelForward = GTA.Math.Vector3.Dot(GTA.Math.Vector3.Normalize(velocity), forwardVec);
                isReversing = dotVelForward < -0.3f; // Moving backward if velocity opposes forward vector
            }
            else
            {
                isReversing = false;
            }

            // ============================================
            // HILL/INCLINE COMPENSATION
            // Use a flattened forward vector for ground-based calculations
            // ============================================
            GTA.Math.Vector3 flatForward = new GTA.Math.Vector3(forwardVec.X, forwardVec.Y, 0);
            if (flatForward.Length() > 0.1f)
            {
                flatForward = GTA.Math.Vector3.Normalize(flatForward);
            }
            else
            {
                flatForward = forwardVec; // Fallback if nearly vertical (unlikely for cars)
            }

            // ============================================
            // WAYPOINT-AWARE DRIVE ASSIST
            // Updates cached waypoint direction for road node preference
            // Uses both waypoint and mission blip positions
            // ============================================
            if (getSetting("waypointDriveAssist") == 1)
            {
                // Update waypoint info periodically (every 500ms)
                if (DateTime.Now.Ticks - waypointUpdateTicks > 5000000)
                {
                    waypointUpdateTicks = DateTime.Now.Ticks;
                    UpdateWaypointDirection(playerPos);
                }
            }
            else
            {
                hasActiveWaypoint = false;
            }

            // ============================================
            // PATH POLYLINE — single source of truth for "where the AI would
            // drive next." Built once per detection tick from GPS route /
            // static graph / native nearest-node, in that priority. All
            // downstream gates (NPC corridor filter, Stanley lane keep) use
            // this polyline; replaces the noisy single-node sampling that
            // caused the side-to-side drift.
            // ============================================
            BuildPathPolyline(playerVeh);

            // Detection range scales with speed (10m to 40m)
            float speedFactor = Math.Min(vehicleSpeed / 30f, 1f);
            float detectionRange = 10f + (speedFactor * 30f);

            // ============================================
            // SEPARATED THREAT DETECTION:
            // - steerTTC: Used for steering avoidance (includes front AND side threats)
            // - brakeTTC: Used ONLY for braking (ONLY front/ahead threats)
            // This prevents side obstacles from triggering braking, which was limiting speed
            // ============================================
            float closestSteerTTC = float.MaxValue;  // For steering decisions (all directions)
            float closestBrakeTTC = float.MaxValue;  // For braking decisions (ONLY ahead)
            float closestBrakeDistance = float.MaxValue; // Distance to closest brake threat
            string closestDir = "none";
            string closestType = "none";
            int avoidDirection = 0;
            GTA.Math.Vector3 closestSteerThreatPos = GTA.Math.Vector3.Zero; // Position of closest steer threat
            GTA.Math.Vector3 closestSteerThreatVel = GTA.Math.Vector3.Zero; // Velocity (Zero = static)
            GTA.Math.Vector3 closestBrakeThreatPos = GTA.Math.Vector3.Zero; // Position of closest brake threat
            GTA.Math.Vector3 closestBrakeThreatVel = GTA.Math.Vector3.Zero;
            string closestBrakeType = "none"; // Type of closest brake threat

            // Scan nearby peds
            Ped[] nearbyPeds = World.GetNearbyPeds(playerPos, detectionRange);
            foreach (Ped ped in nearbyPeds)
            {
                if (ped == Game.Player.Character || ped.IsDead) continue;

                // Path-corridor filter: keep peds whose position is within
                // NPC_PATH_CORRIDOR_M of the upcoming polyline. Replaces the
                // old IsPositionOnRoad gate which was unreliable (especially
                // in parking lots and on the curb) and missed stationary peds
                // standing in our travel path. Moving peds (speed > 0.5) skip
                // the gate entirely — they may step into the path next frame.
                if (ped.Speed < 0.5f && !IsInPathCorridor(ped.Position, NPC_PATH_CORRIDOR_M)) continue;

                float ttc = CalculateTTC(playerVeh, ped.Position, ped.Velocity);
                string dir = GetThreatDirection(playerVeh, ped.Position);
                float dist = World.GetDistance(playerPos, ped.Position);

                // Update steering TTC (all directions)
                if (ttc < closestSteerTTC)
                {
                    closestSteerTTC = ttc;
                    closestType = "pedestrian";
                    closestDir = dir;
                    avoidDirection = GetAvoidDirection(playerVeh, ped.Position);
                    closestSteerThreatPos = ped.Position;
                    closestSteerThreatVel = ped.Velocity;
                }

                // Update braking TTC ONLY for threats ahead AND inside the brake cone
                // AND visibility-aged (kills spawn-in false TTC) AND monotonic-dropping.
                if (dir == "ahead" && ttc < closestBrakeTTC
                    && IsInBrakeCone(playerVeh, ped.Position)
                    && BrakeThreatPassesEntityGates(ped.Handle, ttc, DateTime.Now.Ticks, isPedestrian: true, playerSpeedMs: vehicleSpeed))
                {
                    closestBrakeTTC = ttc;
                    closestBrakeDistance = dist;
                    closestBrakeType = "pedestrian";
                    closestBrakeThreatPos = ped.Position;
                    closestBrakeThreatVel = ped.Velocity;
                }
            }

            // Scan nearby vehicles
            Vehicle[] nearbyVehs = World.GetNearbyVehicles(playerPos, detectionRange);
            long gateNow = DateTime.Now.Ticks;
            PruneEntityTracking(gateNow);
            UpdateAdjacentLatch(playerVeh, nearbyVehs, gateNow);
            foreach (Vehicle veh in nearbyVehs)
            {
                if (veh == playerVeh) continue;

                // Path-corridor filter: stationary vehicles outside the
                // upcoming polyline corridor are ignored (parked cars on the
                // shoulder). Moving vehicles always register since they may
                // turn into our path. Replaces the IsObstacleInTravelLane
                // cone heuristic — the polyline tracks curves, the cone did
                // not.
                if (veh.Speed < 0.5f && !IsInPathCorridor(veh.Position, NPC_PATH_CORRIDOR_M)) continue;

                float ttc = CalculateTTC(playerVeh, veh.Position, veh.Velocity);
                string dir = GetThreatDirection(playerVeh, veh.Position);
                float dist = World.GetDistance(playerPos, veh.Position);

                // Update steering TTC (all directions)
                if (ttc < closestSteerTTC)
                {
                    closestSteerTTC = ttc;
                    closestType = "vehicle";
                    closestDir = dir;
                    avoidDirection = GetAvoidDirection(playerVeh, veh.Position);
                    closestSteerThreatPos = veh.Position;
                    closestSteerThreatVel = veh.Velocity;
                }

                // Update braking TTC ONLY for threats ahead AND inside the brake cone
                // AND that have been visible long enough AND whose TTC is dropping.
                // Together these gates kill the "sudden brake out of nowhere" complaint.
                if (dir == "ahead" && ttc < closestBrakeTTC
                    && IsInBrakeCone(playerVeh, veh.Position)
                    && BrakeThreatPassesEntityGates(veh.Handle, ttc, gateNow, isPedestrian: false, playerSpeedMs: vehicleSpeed))
                {
                    closestBrakeTTC = ttc;
                    closestBrakeDistance = dist;
                    closestBrakeType = "vehicle";
                    closestBrakeThreatPos = veh.Position;
                    closestBrakeThreatVel = veh.Velocity;
                }
            }

            // ============================================
            // MULTI-RAYCAST FOR WORLD GEOMETRY
            // Uses flattened forward vector for hill compensation
            // Multiple rays provide better coverage for obstacles
            // Direction depends on whether we're reversing
            // ============================================
            GTA.Math.Vector3 startPos = playerPos + new GTA.Math.Vector3(0, 0, 0.5f);
            // Obstacle look-ahead: iter-8 derives this from GTA V's own AI
            // handling table for the current vehicle class instead of the
            // older class-agnostic `vehicleSpeed * 3.5f` formula. The meta's
            // BrakeLookaheadForSpeed blends MinBrakeDistance->MaxBrakeDistance
            // by speed/MaxSpeedAtBrakeDistance — a truck gets longer ray
            // range than a sports car at the same speed. 1.3x margin so the
            // brake has time to ramp up rather than firing at the last
            // possible moment. Same 15-150 m clamp as before for safety.
            var rayAi = VehicleAIHandlingRegistry.GetForVehicle(playerVeh);
            float rayRange = Math.Min(150f, Math.Max(15f,
                rayAi.BrakeLookaheadForSpeed(vehicleSpeed) * 1.3f));

            // Use the direction we're actually traveling
            GTA.Math.Vector3 travelDir = isReversing ? -flatForward : flatForward;
            GTA.Math.Vector3 travelRight = isReversing ? -rightVec : rightVec;

            // Cast a fan of 5 rays. The inner 3 (±0,±17°) feed STEERING and BRAKE
            // sources; the outer 2 (±35°) feed STEERING ONLY. Wider rays catch
            // glancing-angle rails/walls without producing the phantom-brake
            // problem (city walls in the periphery would otherwise constantly
            // brake the car).
            float[] rayOffsets = { 0f, -0.3f, 0.3f, -0.7f, 0.7f };
            bool[] rayBrakeEligible = { true, true, true, false, false };
            float closestRayDist = float.MaxValue;
            float closestBrakeEligibleRayDist = float.MaxValue;
            GTA.Math.Vector3 closestRayHitPos = GTA.Math.Vector3.Zero;
            GTA.Math.Vector3 closestBrakeEligibleRayHitPos = GTA.Math.Vector3.Zero;
            int bestAvoidDir = 0;
            // Nearest hit on each side of the fan — used to tell whether a
            // swerve actually has anywhere to go (see brake promotion below).
            float leftRayHitDist = float.MaxValue;
            float rightRayHitDist = float.MaxValue;

            for (int rIdx = 0; rIdx < rayOffsets.Length; rIdx++)
            {
                float offset = rayOffsets[rIdx];
                GTA.Math.Vector3 rayDir = GTA.Math.Vector3.Normalize(travelDir + travelRight * offset);
                RaycastResult ray = World.Raycast(startPos, startPos + (rayDir * rayRange),
                    IntersectFlags.Map | IntersectFlags.Objects, playerVeh);

                if (!ray.DidHit) continue;
                // GROUND-Z FILTER: skip hits that look like road/ground (mostly
                // vertical normal). Threshold 0.85 — only surfaces tilted up to
                // ~32 deg count as ground. Old 0.7 (~45 deg) was rejecting
                // sloped guardrails and embankment walls as "ground".
                if (Math.Abs(ray.SurfaceNormal.Z) > 0.85f) continue;

                float dist = World.GetDistance(startPos, ray.HitPosition);

                if (dist < closestRayDist)
                {
                    closestRayDist = dist;
                    closestRayHitPos = ray.HitPosition;
                    if (offset < 0) bestAvoidDir = 1;
                    else if (offset > 0) bestAvoidDir = -1;
                    else bestAvoidDir = GetClearerSide(playerVeh, rayRange);
                }
                if (offset < 0f) { if (dist < leftRayHitDist) leftRayHitDist = dist; }
                else if (offset > 0f) { if (dist < rightRayHitDist) rightRayHitDist = dist; }
                if (rayBrakeEligible[rIdx] && dist < closestBrakeEligibleRayDist)
                {
                    closestBrakeEligibleRayDist = dist;
                    closestBrakeEligibleRayHitPos = ray.HitPosition;
                }
            }

            // VOLUMETRIC CAPSULE SWEEP — the 5-ray fan has angular gaps that grow
            // with distance (at 80m the +/-17deg rays are ~24m apart laterally),
            // easily wide enough to miss a guardrail end-cap or a single pillar.
            // The capsule fills those gaps with a continuous swept volume.
            // Always brake-eligible: if it volumetrically clears, the car
            // physically cannot fit past.
            GTA.Math.Vector3 capsuleHitPos, capsuleHitNorm;
            float capsuleDist = PerformShapeCast(startPos, travelDir, travelRight,
                rayRange, IntersectFlags.Map | IntersectFlags.Objects, playerVeh,
                out capsuleHitPos, out capsuleHitNorm, vehicleSpeed);
            if (capsuleDist > 0f && Math.Abs(capsuleHitNorm.Z) <= 0.85f)
            {
                if (capsuleDist < closestRayDist)
                {
                    closestRayDist = capsuleDist;
                    closestRayHitPos = capsuleHitPos;
                    // Lateral side from hit normal — sign of dot with travelRight
                    // gives the side the wall is on; we want to steer AWAY from it.
                    float capLat = GTA.Math.Vector3.Dot(travelRight, capsuleHitNorm);
                    if (capLat > 0.1f) bestAvoidDir = -1;
                    else if (capLat < -0.1f) bestAvoidDir = 1;
                    else bestAvoidDir = GetClearerSide(playerVeh, rayRange);
                }
                if (capsuleDist < closestBrakeEligibleRayDist)
                {
                    closestBrakeEligibleRayDist = capsuleDist;
                    closestBrakeEligibleRayHitPos = capsuleHitPos;
                }
            }

            // STEERING source: any of the 5 rays (wide fan).
            if (closestRayDist < rayRange)
            {
                float ttc = closestRayDist / Math.Max(vehicleSpeed, 1f);
                if (ttc < closestSteerTTC)
                {
                    closestSteerTTC = ttc;
                    closestType = "obstacle";
                    closestDir = "ahead";
                    avoidDirection = bestAvoidDir;
                    closestSteerThreatPos = closestRayHitPos;
                    closestSteerThreatVel = GTA.Math.Vector3.Zero;
                }
            }
            // BRAKE source: only inner-fan rays, gated by the NARROW brake cone.
            // Wide-angle hits never produce brake — they only produce steering.
            if (closestBrakeEligibleRayDist < rayRange)
            {
                float ttc = closestBrakeEligibleRayDist / Math.Max(vehicleSpeed, 1f);
                if (ttc < closestBrakeTTC && IsInBrakeCone(playerVeh, closestBrakeEligibleRayHitPos))
                {
                    closestBrakeTTC = ttc;
                    closestBrakeDistance = closestBrakeEligibleRayDist;
                    closestBrakeType = "obstacle";
                    closestBrakeThreatPos = closestBrakeEligibleRayHitPos;
                    closestBrakeThreatVel = GTA.Math.Vector3.Zero;
                }
            }
            // FORWARD-OBSTACLE BRAKE PROMOTION: a wide-fan obstacle normally
            // produces a swerve only. But if BOTH sides of the fan are blocked
            // the swerve has nowhere to go and the car plows straight in (debug
            // log F3578: obstacle ahead, zero brake, hit at 38 mph). When the
            // swerve is blocked, escalate the obstacle to a brake threat.
            bool swerveBlocked = leftRayHitDist < rayRange && rightRayHitDist < rayRange;
            if (closestRayDist < rayRange && swerveBlocked)
            {
                float ttc = closestRayDist / Math.Max(vehicleSpeed, 1f);
                if (ttc < closestBrakeTTC)
                {
                    closestBrakeTTC = ttc;
                    closestBrakeDistance = closestRayDist;
                    closestBrakeType = "obstacle";
                    closestBrakeThreatPos = closestRayHitPos;
                    closestBrakeThreatVel = GTA.Math.Vector3.Zero;
                }
            }

            // STATIC-WALL CAST — Map-only, wider radius, longer range. Static
            // geometry never moves, so we can plan ~5 s ahead vs the ~3.5 s used
            // for traffic. Splitting flags also stops small objects (parked
            // cars, hydrants) from masking a wall 30 m behind them. Brake-only:
            // a static wall ahead with no closer dynamic threat means brake
            // straight, don't swerve into traffic.
            GTA.Math.Vector3 staticHitPos, staticHitNorm;
            float staticRange, staticRadius;
            float staticDist = PerformStaticWallCast(startPos, travelDir,
                vehicleSpeed, playerVeh,
                out staticHitPos, out staticHitNorm,
                out staticRange, out staticRadius);
            // Tightened 2026-05-25:
            //  - normalZ filter 0.85 -> 0.60. Old threshold accepted surfaces only
            //    32° off horizontal (sloped curbs, embankment toes). 0.60 keeps
            //    only surfaces >=53° from horizontal — true walls, signs, building
            //    faces. Sloped curbs at normalZ 0.7-0.85 are no longer "walls".
            //  - IsInBrakeCone gate: the swept capsule has lateral radius up to
            //    2.2 m, so it can hit geometry outside the actual driving lane.
            //    Require the hit point to be in our forward corridor (same gate
            //    used by the nav-assist center brake source) before promoting to
            //    a brake threat. Surface-normal "wall faces us" head-on still
            //    bypasses this check below.
            // Iter-9 Patch D: elevation-aware static-wall gate.
            // The original `staticHitNorm.Z <= 0.60` filter correctly accepts
            // vertical walls but REJECTS the top surface of a curb / step /
            // low platform — those have normalZ ≈ 1.0 yet the car can't drive
            // through them. Driveassist-2026-05-25-180748 F7970/F8030/F8056
            // (3 collisions, same location, all logged as STATIC-REJECTED
            // dist=0.7 normalZ=1.00) is exactly this pattern.
            //
            // Add a parallel acceptance gate based on HIT ELEVATION relative
            // to the vehicle chassis: anything 0.3 m–2.0 m above the chassis
            // is at bumper-to-roof height and unswervable regardless of
            // surface orientation. Below 0.3 m we still reject (road surface,
            // manhole, low rumble strip). Above 2.0 m we still reject
            // (overpass, overhead sign — we drive under those).
            float staticRelZ = staticDist > 0f
                ? staticHitPos.Z - playerVeh.Position.Z : 0f;
            bool staticIsVerticalSurface = Math.Abs(staticHitNorm.Z) <= 0.60f;
            bool staticIsBumperHeightHit = staticRelZ >= 0.30f && staticRelZ <= 2.0f;
            bool staticHitValid = staticDist > 0f
                && (staticIsVerticalSurface || staticIsBumperHeightHit);
            if (staticHitValid)
            {
                // Wall facing us head-on? swerve-clearance check.
                bool staticWallFacesUs = false;
                GTA.Math.Vector3 sn = staticHitNorm; sn.Z = 0f;
                GTA.Math.Vector3 sfwd = playerVeh.ForwardVector; sfwd.Z = 0f;
                if (sn.LengthSquared() > 0.0001f && sfwd.LengthSquared() > 0.0001f)
                {
                    sn.Normalize(); sfwd.Normalize();
                    staticWallFacesUs = GTA.Math.Vector3.Dot(sn, -sfwd) > WALL_NORMAL_FACE_DOT;
                }
                bool staticInCone = IsInBrakeCone(playerVeh, staticHitPos);
                // Head-on bypass is only safe at short range. The capsule sweep
                // has lateral radius up to 2.2 m and runs out to v*3.5 (>100 m
                // at highway speed), so a building corner 20-30 m off-axis can
                // still register a "head-on" normal — promoting it to a brake
                // threat caused the wall-collision cluster in the
                // driveassist-2026-05-25-103730 audit. Beyond STATIC_HEADON_RANGE
                // require the in-cone gate to also pass; below it the head-on
                // bypass still wins because we're close enough that an
                // imminent dead-ahead wall is a true emergency.
                const float STATIC_HEADON_RANGE = 8f;
                if (!staticInCone && !(staticWallFacesUs && staticDist <= STATIC_HEADON_RANGE))
                    staticHitValid = false;
            }
            if (staticHitValid)
            {
                float staticTtc = staticDist / Math.Max(vehicleSpeed, 1f);
                // Defer to closer non-map threats; only act if we beat the
                // current brake threat.
                if (staticTtc < closestBrakeTTC)
                {
                    closestBrakeTTC = staticTtc;
                    closestBrakeDistance = staticDist;
                    closestBrakeType = "wall";
                    closestBrakeThreatPos = staticHitPos;
                    closestBrakeThreatVel = GTA.Math.Vector3.Zero;
                }
                if (driveLogger != null && driveLogger.IsRunning)
                    driveLogger.Write("[F" + driveLogFrameCount
                        + "] shapecast: STATIC dist=" + staticDist.ToString("F1")
                        + " range=" + staticRange.ToString("F0")
                        + " radius=" + staticRadius.ToString("F1")
                        + " normalZ=" + staticHitNorm.Z.ToString("F2")
                        + " relZ=" + staticRelZ.ToString("F2"));
            }
            else if (staticDist > 0f && driveLogger != null && driveLogger.IsRunning)
            {
                // Log rejected static hits so the next telemetry pass can
                // confirm Fix 1 is filtering the right surfaces.
                driveLogger.Write("[F" + driveLogFrameCount
                    + "] shapecast: STATIC-REJECTED dist=" + staticDist.ToString("F1")
                    + " normalZ=" + staticHitNorm.Z.ToString("F2")
                    + " relZ=" + staticRelZ.ToString("F2"));
            }

            // ============================================
            // INTEGRATE NAV ASSIST DATA WITH ENTITY REFERENCES
            // Uses actual entity velocities for more accurate TTC calculation
            // Center: affects both steering and braking
            // Sides: affect ONLY steering, NOT braking
            // ============================================
            float navAssistMaxRange = detectionRange;

            // Center obstacle from nav assist (affects BOTH steering and braking)
            if (navAssistDistCenter < navAssistMaxRange && navAssistTypeCenter != "none")
            {
                float ttc;

                // Surface-normal classification of the center hit: if the wall
                // faces us nearly head-on a swerve cannot clear it, so escalate
                // to braking and suppress the steer nudge. An angled surface
                // keeps normal steering and only breaks ties below.
                bool wallFacesUs = false;
                int normalAvoidDir = 0;
                if (navAssistNormalCenter != GTA.Math.Vector3.Zero)
                {
                    GTA.Math.Vector3 wn = navAssistNormalCenter; wn.Z = 0f;
                    GTA.Math.Vector3 wfwd = playerVeh.ForwardVector; wfwd.Z = 0f;
                    if (wn.LengthSquared() > 0.0001f && wfwd.LengthSquared() > 0.0001f)
                    {
                        wn.Normalize();
                        wfwd.Normalize();
                        wallFacesUs = GTA.Math.Vector3.Dot(wn, -wfwd) > WALL_NORMAL_FACE_DOT;
                        float wlat = GTA.Math.Vector3.Dot(playerVeh.RightVector, navAssistNormalCenter);
                        normalAvoidDir = wlat > 0.1f ? 1 : (wlat < -0.1f ? -1 : 0);
                    }
                }
                // Strong rumble the instant a head-on wall is newly detected.
                if (wallFacesUs && !rumbleWallArmed) TriggerRumble(0.9f, 200);
                rumbleWallArmed = wallFacesUs;

                // Use actual entity velocity if available for more accurate TTC
                if (navAssistVehicleCenter != null && navAssistVehicleCenter.Exists())
                {
                    ttc = CalculateTTC(playerVeh, navAssistVehicleCenter.Position, navAssistVehicleCenter.Velocity);
                }
                else if (navAssistPedCenter != null && navAssistPedCenter.Exists())
                {
                    ttc = CalculateTTC(playerVeh, navAssistPedCenter.Position, navAssistPedCenter.Velocity);
                }
                else
                {
                    // World geometry - use simple distance/speed calculation
                    ttc = navAssistDistCenter / Math.Max(vehicleSpeed, 1f);
                }

                if (ttc < closestSteerTTC)
                {
                    closestSteerTTC = ttc;
                    closestType = navAssistTypeCenter == "ped" ? "pedestrian" :
                                  navAssistTypeCenter == "vehicle" ? "vehicle" : "obstacle";
                    closestDir = "ahead";
                    // Use nav assist side data to determine clearer direction
                    if (navAssistDistLeft > navAssistDistRight + 2f)
                        avoidDirection = -1; // Left is clearer
                    else if (navAssistDistRight > navAssistDistLeft + 2f)
                        avoidDirection = 1; // Right is clearer
                    else
                        avoidDirection = GetClearerSide(playerVeh, rayRange);
                    // Flat wall ahead: no swerve can clear it, brake instead.
                    // Angled surface with an ambiguous side pick: lean toward
                    // the side the surface normal points away from.
                    if (wallFacesUs)
                        avoidDirection = 0;
                    else if (avoidDirection == 0 && normalAvoidDir != 0)
                        avoidDirection = normalAvoidDir;
                    // Track threat position from nav assist entities
                    if (navAssistVehicleCenter != null && navAssistVehicleCenter.Exists())
                        closestSteerThreatPos = navAssistVehicleCenter.Position;
                    else if (navAssistPedCenter != null && navAssistPedCenter.Exists())
                        closestSteerThreatPos = navAssistPedCenter.Position;
                    else
                        closestSteerThreatPos = playerPos + playerVeh.ForwardVector * navAssistDistCenter;
                }
                // Apply gates to nav-assist center brake source. Lateral cone is
                // checked against the threat position (entity or projected forward).
                GTA.Math.Vector3 navCenterPos =
                    (navAssistVehicleCenter != null && navAssistVehicleCenter.Exists())
                    ? navAssistVehicleCenter.Position
                    : (navAssistPedCenter != null && navAssistPedCenter.Exists())
                        ? navAssistPedCenter.Position
                        : playerPos + playerVeh.ForwardVector * navAssistDistCenter;
                int navCenterHandle = (navAssistVehicleCenter != null && navAssistVehicleCenter.Exists())
                    ? navAssistVehicleCenter.Handle
                    : (navAssistPedCenter != null && navAssistPedCenter.Exists())
                        ? navAssistPedCenter.Handle
                        : 0;
                // A head-on wall is promoted to a brake threat even if the
                // lateral cone check is marginal — a swerve would just plow in.
                bool navCenterIsPed = navAssistPedCenter != null && navAssistPedCenter.Exists();
                bool navCenterBrakeOk = ttc < closestBrakeTTC
                                        && (IsInBrakeCone(playerVeh, navCenterPos) || wallFacesUs)
                                        && (navCenterHandle == 0
                                            || BrakeThreatPassesEntityGates(navCenterHandle, ttc, DateTime.Now.Ticks, navCenterIsPed, vehicleSpeed));
                if (navCenterBrakeOk)
                {
                    closestBrakeTTC = ttc;
                    closestBrakeDistance = navAssistDistCenter;
                    closestBrakeType = navAssistTypeCenter == "ped" ? "pedestrian" :
                                      navAssistTypeCenter == "vehicle" ? "vehicle" : "obstacle";
                    if (navAssistVehicleCenter != null && navAssistVehicleCenter.Exists())
                    {
                        closestBrakeThreatPos = navAssistVehicleCenter.Position;
                        closestBrakeThreatVel = navAssistVehicleCenter.Velocity;
                    }
                    else if (navAssistPedCenter != null && navAssistPedCenter.Exists())
                    {
                        closestBrakeThreatPos = navAssistPedCenter.Position;
                        closestBrakeThreatVel = navAssistPedCenter.Velocity;
                    }
                    else
                    {
                        closestBrakeThreatPos = playerPos + playerVeh.ForwardVector * navAssistDistCenter;
                        closestBrakeThreatVel = GTA.Math.Vector3.Zero;
                    }
                }
            }
            else
            {
                // No center obstacle this scan — clear the wall rumble latch so
                // the next wall encounter rumbles fresh.
                rumbleWallArmed = false;
            }

            // Left obstacle from nav assist - ONLY affects steering (steer right to avoid)
            // Does NOT contribute to braking - side obstacles shouldn't slow the car
            if (navAssistDistLeft < navAssistMaxRange * 0.5f && navAssistTypeLeft != "none")
            {
                float ttc;

                // Use actual entity velocity if available
                if (navAssistVehicleLeft != null && navAssistVehicleLeft.Exists())
                {
                    ttc = CalculateTTC(playerVeh, navAssistVehicleLeft.Position, navAssistVehicleLeft.Velocity);
                }
                else if (navAssistPedLeft != null && navAssistPedLeft.Exists())
                {
                    ttc = CalculateTTC(playerVeh, navAssistPedLeft.Position, navAssistPedLeft.Velocity);
                }
                else
                {
                    ttc = navAssistDistLeft / Math.Max(vehicleSpeed * 0.5f, 1f);
                }

                if (ttc < closestSteerTTC)
                {
                    closestSteerTTC = ttc;
                    closestType = navAssistTypeLeft == "ped" ? "pedestrian" :
                                  navAssistTypeLeft == "vehicle" ? "vehicle" : "obstacle";
                    closestDir = "left";
                    avoidDirection = 1; // Steer right to avoid left obstacle
                    if (navAssistVehicleLeft != null && navAssistVehicleLeft.Exists())
                    {
                        closestSteerThreatPos = navAssistVehicleLeft.Position;
                        closestSteerThreatVel = navAssistVehicleLeft.Velocity;
                    }
                    else if (navAssistPedLeft != null && navAssistPedLeft.Exists())
                    {
                        closestSteerThreatPos = navAssistPedLeft.Position;
                        closestSteerThreatVel = navAssistPedLeft.Velocity;
                    }
                    else
                    {
                        closestSteerThreatPos = playerPos - playerVeh.RightVector * navAssistDistLeft;
                        closestSteerThreatVel = GTA.Math.Vector3.Zero;
                    }
                }
                // Iter-9 Patch A: lateral entities can ALSO be brake threats
                // when they're inside our forward lane corridor. The center
                // cone misses lane-mate vehicles that are 1-2 m off-axis;
                // driveassist-2026-05-25-180748 markers F758/F1240/F2606/
                // F8673 are all this exact scenario (L or R sees a vehicle
                // at 10-20 m, C=none, player collides within 1 s).
                Entity leftEnt = (Entity)navAssistVehicleLeft ?? (Entity)navAssistPedLeft;
                if (leftEnt != null && leftEnt.Exists())
                {
                    GTA.Math.Vector3 toLE = leftEnt.Position - playerPos;
                    float lFwd = GTA.Math.Vector3.Dot(playerVeh.ForwardVector, toLE);
                    float lLat = Math.Abs(GTA.Math.Vector3.Dot(playerVeh.RightVector, toLE));
                    float lDist = toLE.Length();
                    if (lFwd > 0.3f && lLat < 2.5f && lDist < 12f && ttc < closestBrakeTTC)
                    {
                        bool lIsPed = navAssistPedLeft != null && navAssistPedLeft.Exists();
                        if (BrakeThreatPassesEntityGates(leftEnt.Handle, ttc, DateTime.Now.Ticks, lIsPed, vehicleSpeed))
                        {
                            closestBrakeTTC = ttc;
                            closestBrakeDistance = lDist;
                            closestBrakeType = lIsPed ? "pedestrian" : "vehicle";
                            closestBrakeThreatPos = leftEnt.Position;
                            closestBrakeThreatVel = navAssistVehicleLeft != null && navAssistVehicleLeft.Exists()
                                ? navAssistVehicleLeft.Velocity
                                : (navAssistPedLeft != null && navAssistPedLeft.Exists()
                                    ? navAssistPedLeft.Velocity
                                    : GTA.Math.Vector3.Zero);
                        }
                    }
                }
            }

            // Right obstacle from nav assist - ONLY affects steering (steer left to avoid)
            // Does NOT contribute to braking
            if (navAssistDistRight < navAssistMaxRange * 0.5f && navAssistTypeRight != "none")
            {
                float ttc;

                // Use actual entity velocity if available
                if (navAssistVehicleRight != null && navAssistVehicleRight.Exists())
                {
                    ttc = CalculateTTC(playerVeh, navAssistVehicleRight.Position, navAssistVehicleRight.Velocity);
                }
                else if (navAssistPedRight != null && navAssistPedRight.Exists())
                {
                    ttc = CalculateTTC(playerVeh, navAssistPedRight.Position, navAssistPedRight.Velocity);
                }
                else
                {
                    ttc = navAssistDistRight / Math.Max(vehicleSpeed * 0.5f, 1f);
                }

                if (ttc < closestSteerTTC)
                {
                    closestSteerTTC = ttc;
                    closestType = navAssistTypeRight == "ped" ? "pedestrian" :
                                  navAssistTypeRight == "vehicle" ? "vehicle" : "obstacle";
                    closestDir = "right";
                    avoidDirection = -1; // Steer left to avoid right obstacle
                    if (navAssistVehicleRight != null && navAssistVehicleRight.Exists())
                    {
                        closestSteerThreatPos = navAssistVehicleRight.Position;
                        closestSteerThreatVel = navAssistVehicleRight.Velocity;
                    }
                    else if (navAssistPedRight != null && navAssistPedRight.Exists())
                    {
                        closestSteerThreatPos = navAssistPedRight.Position;
                        closestSteerThreatVel = navAssistPedRight.Velocity;
                    }
                    else
                    {
                        closestSteerThreatPos = playerPos + playerVeh.RightVector * navAssistDistRight;
                        closestSteerThreatVel = GTA.Math.Vector3.Zero;
                    }
                }
                // Iter-9 Patch A: mirror of the Left block — promote
                // lateral-detected entities in the forward lane corridor to
                // brake threats.
                Entity rightEnt = (Entity)navAssistVehicleRight ?? (Entity)navAssistPedRight;
                if (rightEnt != null && rightEnt.Exists())
                {
                    GTA.Math.Vector3 toRE = rightEnt.Position - playerPos;
                    float rFwd = GTA.Math.Vector3.Dot(playerVeh.ForwardVector, toRE);
                    float rLat = Math.Abs(GTA.Math.Vector3.Dot(playerVeh.RightVector, toRE));
                    float rDist = toRE.Length();
                    if (rFwd > 0.3f && rLat < 2.5f && rDist < 12f && ttc < closestBrakeTTC)
                    {
                        bool rIsPed = navAssistPedRight != null && navAssistPedRight.Exists();
                        if (BrakeThreatPassesEntityGates(rightEnt.Handle, ttc, DateTime.Now.Ticks, rIsPed, vehicleSpeed))
                        {
                            closestBrakeTTC = ttc;
                            closestBrakeDistance = rDist;
                            closestBrakeType = rIsPed ? "pedestrian" : "vehicle";
                            closestBrakeThreatPos = rightEnt.Position;
                            closestBrakeThreatVel = navAssistVehicleRight != null && navAssistVehicleRight.Exists()
                                ? navAssistVehicleRight.Velocity
                                : (navAssistPedRight != null && navAssistPedRight.Exists()
                                    ? navAssistPedRight.Velocity
                                    : GTA.Math.Vector3.Zero);
                        }
                    }
                }
            }

            // Store for reference - use steer TTC for general threat tracking
            threatTimeToCollision = closestSteerTTC;
            threatDirection = closestDir;
            threatType = closestType;
            closestThreatPosition = closestSteerThreatPos;
            closestBrakeObstacleType = closestBrakeType;

            // Hand off live position/velocity to ApplyCachedSteeringInputs so it can
            // recompute brake/steer urgency every frame between full scans. The stamp
            // is checked for staleness so we don't act on data older than 250 ms.
            long nowStamp = DateTime.Now.Ticks;
            if (closestBrakeThreatPos != GTA.Math.Vector3.Zero)
            {
                // Initialize FirstSeen the moment the cache transitions from
                // empty -> populated. Subsequent re-confirmations refresh Stamp
                // but leave FirstSeen alone, so the 3-second wedge-detect below
                // measures actual elapsed time, not refresh recency.
                if (cachedBrakeThreatStamp == 0)
                {
                    cachedBrakeThreatFirstSeenStamp = nowStamp;
                    cachedBrakeThreatFirstSeenPlayerPos = playerVeh.Position;
                }
                cachedBrakeThreatPos = closestBrakeThreatPos;
                cachedBrakeThreatVel = closestBrakeThreatVel;
                cachedBrakeThreatStamp = nowStamp;
            }
            else
            {
                cachedBrakeThreatPos = GTA.Math.Vector3.Zero;
                cachedBrakeThreatStamp = 0;
                cachedBrakeThreatFirstSeenStamp = 0;
                // Iter-11 Patch I: reset distance history when cache empties
                // so a future re-acquisition starts a fresh measurement.
                cachedBrakeDistHistoryValid = false;
                cachedBrakeDistIdx = 0;
            }
            if (closestSteerThreatPos != GTA.Math.Vector3.Zero)
            {
                cachedSteerThreatPos = closestSteerThreatPos;
                cachedSteerThreatVel = closestSteerThreatVel;
                cachedSteerThreatStamp = nowStamp;
            }
            else
            {
                cachedSteerThreatPos = GTA.Math.Vector3.Zero;
            }

            // EARLY-CLEAR (2026-05-25 fix for Loop 2 + Loop 3). The C3 rescan
            // below only fires when the vehicle is stopped AND the cache is
            // stale AND emergencyBrakeActive is set. That gate misses two
            // common cases the failure-log analysis surfaced:
            //   (a) Drove past: the vehicle has moved beyond the cached
            //       threat position (forward-dot is now <= 0) or the threat
            //       has drifted laterally out of the brake cone as the road
            //       curves. The cache is referring to something physically
            //       behind/beside the car — clear it so the next scan can
            //       pick a fresh forward threat instead.
            //   (b) Long-wedged: the threat was first seen >3 s ago and the
            //       vehicle has moved <1 m since. Every refresh is the same
            //       roadside feature; the C3 staleness check never fires
            //       because Stamp keeps being refreshed. FirstSeenStamp
            //       captures the original detection time, decoupling from
            //       the refresh-driven Stamp.
            // Both clears also drop emergencyBrakeActive and
            // wasObstacleInBrakeZone so Loop 3 (Mechanism A keeping
            // emergency brake latched via critical-zone test) releases too.
            if (cachedBrakeThreatStamp > 0 && cachedBrakeThreatPos != GTA.Math.Vector3.Zero)
            {
                GTA.Math.Vector3 toCached = cachedBrakeThreatPos - playerVeh.Position;
                bool clearReasonPast = false;
                bool clearReasonWedged = false;

                // Iter-11 Patch I: update the 3-frame distance ring for the
                // drove-past confirmation. Sample the current distance every
                // tick the cache is non-zero; when a new threat replaces a
                // moved one (>3 m position delta from prior sample), reset
                // the ring so the rising-distance test isn't fooled by the
                // change of identity.
                float curDist = toCached.Length();
                if (cachedBrakeDistHistoryValid)
                {
                    float prevDist = cachedBrakeDistHistory[cachedBrakeDistIdx];
                    if (Math.Abs(curDist - prevDist) > 3f)
                    {
                        // Probable cache identity change — reset history.
                        cachedBrakeDistHistoryValid = false;
                        cachedBrakeDistIdx = 0;
                    }
                }
                cachedBrakeDistHistory[cachedBrakeDistIdx] = curDist;
                cachedBrakeDistIdx = (cachedBrakeDistIdx + 1) % 3;
                if (!cachedBrakeDistHistoryValid && cachedBrakeDistIdx == 0)
                    cachedBrakeDistHistoryValid = true;

                if (toCached.LengthSquared() > 0.0001f)
                {
                    GTA.Math.Vector3 toCachedN = GTA.Math.Vector3.Normalize(toCached);
                    GTA.Math.Vector3 vfwd = isReversing ? -playerVeh.ForwardVector : playerVeh.ForwardVector;
                    float fwdDot = GTA.Math.Vector3.Dot(vfwd, toCachedN);
                    // Iter-11 Patch I: drove-past also requires the distance
                    // to have risen by >0.5 m across the 3-frame ring. A
                    // momentary heading swing during evasion (fwdDot dipping
                    // below 0.15 for one frame) no longer wipes the cache;
                    // the threat must actually be receding for at least
                    // 50 ms (3 frames at ~60 fps) of measured motion.
                    bool distRising = false;
                    if (cachedBrakeDistHistoryValid)
                    {
                        float oldest = cachedBrakeDistHistory[cachedBrakeDistIdx];
                        if (curDist - oldest > 0.5f) distRising = true;
                    }
                    if (fwdDot <= 0.15f && distRising) clearReasonPast = true;
                }
                if (!clearReasonPast && cachedBrakeThreatFirstSeenStamp > 0)
                {
                    long firstSeenAgeMs = (DateTime.Now.Ticks - cachedBrakeThreatFirstSeenStamp) / 10000;
                    float playerMoved = (playerVeh.Position - cachedBrakeThreatFirstSeenPlayerPos).Length();
                    if (firstSeenAgeMs > 3000 && playerMoved < 1f)
                        clearReasonWedged = true;
                    // Creeping-under-brake gate: the original 3s+<1m window
                    // misses the dominant failure pattern in
                    // driveassist-2026-05-25-103730 — car creeps at 2-3 m/s
                    // under a latched emergency brake (so playerMoved exceeds
                    // 1 m well before 3 s, and the original gate never fires)
                    // while the cached threat XYZ stays pinned ahead of us.
                    // Once the threat has persisted >1.2 s while we're still
                    // slow AND emergencyBrakeActive is latched AND the same
                    // XYZ is still right in front (<6 m), it's almost
                    // certainly a phantom or unreachable feature — clear it
                    // and let the next scan re-evaluate.
                    else if (firstSeenAgeMs > 1200
                        && emergencyBrakeActive
                        && vehicleSpeed < 2.0f
                        && toCached.Length() < 6f)
                        clearReasonWedged = true;
                }
                if (clearReasonPast || clearReasonWedged)
                {
                    cachedBrakeThreatPos = GTA.Math.Vector3.Zero;
                    cachedBrakeThreatStamp = 0;
                    cachedBrakeThreatFirstSeenStamp = 0;
                    emergencyBrakeActive = false;
                    wasObstacleInBrakeZone = false;
                    if (driveLogger != null && driveLogger.IsRunning)
                        driveLogger.Write("[F" + driveLogFrameCount
                            + "] EVENT brake-cache-early-clear: "
                            + (clearReasonPast ? "drove-past" : "long-wedged"));
                    RecordDriveDecision("brake-cache-early-clear: "
                        + (clearReasonPast ? "drove-past" : "long-wedged"));
                }
            }

            // STALE BRAKE-THREAT RE-SCAN (C3 fix). When the cached brake threat
            // is >1 s old AND the vehicle has been stopped for >1 s AND
            // emergencyBrakeActive is stuck on, we are almost certainly acting
            // on stale obstacle data — the audit found 20% of failures in this
            // gridlock state. Force-clear the cache so this frame can re-pick
            // (or so the next scan can find a fresh threat or none at all).
            // 500 ms cooldown prevents re-trigger thrash.
            long rescanNow = DateTime.Now.Ticks;
            long brakeAgeMs = cachedBrakeThreatStamp > 0
                ? (rescanNow - cachedBrakeThreatStamp) / 10000 : 0;
            // Staleness threshold scales DOWN with speed: a 1 s old threat
            // position is already 30 m off at 67 mph. Floored at 200 ms so
            // city-speed noise doesn't constantly rescan. (Combined with the
            // gridlock check below this only fires when stopped, so the scaling
            // is mostly defensive for code paths that may grow into this check.)
            int staleThresholdMs = Math.Max(200,
                Math.Min(STALE_THREAT_AGE_MS, STALE_THREAT_AGE_MS - (int)(vehicleSpeed * 20f)));
            bool brakeStale     = brakeAgeMs > staleThresholdMs;
            bool brakeGridlock  = VehicleStoppedForLastMs(GRIDLOCK_SPEED_MS, GRIDLOCK_STOPPED_MS)
                                  && HistorySpansAtLeastMs(GRIDLOCK_STOPPED_MS);
            bool rescanCooldown = (rescanNow - lastForcedRescanTicks)
                                  > FORCED_RESCAN_COOLDOWN_TICKS;
            if (brakeStale && brakeGridlock && rescanCooldown && emergencyBrakeActive)
            {
                cachedBrakeThreatPos   = GTA.Math.Vector3.Zero;
                cachedBrakeThreatStamp = 0;
                cachedBrakeThreatFirstSeenStamp = 0;
                emergencyBrakeActive   = false;
                wasObstacleInBrakeZone = false;
                lastForcedRescanTicks  = rescanNow;
                if (driveLogger != null && driveLogger.IsRunning)
                    driveLogger.Write("[F" + driveLogFrameCount
                        + "] EVENT stale-threat-rescan: ageMs=" + brakeAgeMs
                        + " stoppedMs>=" + GRIDLOCK_STOPPED_MS);
                RecordDriveDecision("stale-threat-rescan: forced clear ageMs=" + brakeAgeMs);
            }

            // ============================================
            // DRIVE MODE STATE MACHINE — alignment is a STRICT FALLBACK.
            // Lane-keep runs first. Only when GetLaneCenterGuidance reports
            // isOnValidRoad=false do we look for a recovery node. This protects
            // the common case (correctly aligned on a road) from spurious
            // alignment-mode triggers that capped throttle.
            // ============================================
            bool sameVehicle = (playerVeh.Handle == lastDriveAssistVehicleHandle);
            // Teleport detection: same vehicle handle but position has jumped.
            // BIG_Z_CHANGE in the log is the same signal we want to react to.
            // Skip the check on the first frame (lastDriveAssistVehiclePosValid)
            // and when the vehicle just changed (handled below).
            bool teleported = false;
            if (sameVehicle && lastDriveAssistVehiclePosValid)
            {
                GTA.Math.Vector3 dp = playerVeh.Position - lastDriveAssistVehiclePos;
                float planar = (float)Math.Sqrt(dp.X * dp.X + dp.Y * dp.Y);
                if (planar > TELEPORT_JUMP_M) teleported = true;
            }
            lastDriveAssistVehiclePos = playerVeh.Position;
            lastDriveAssistVehiclePosValid = true;

            if (!sameVehicle || teleported)
            {
                lastDriveAssistVehicleHandle = playerVeh.Handle;
                vehicleEntryTicks = DateTime.Now.Ticks;
                // Reset alignment state so prior vehicle / location can't bleed in.
                currentDriveMode = DriveMode.LaneKeeping;
                alignmentEngageReverse = false;
                // Drop rolling history — prior snapshots are stale.
                historyHead = 0;
                historyCount = 0;
                lastModeChangeTicks = DateTime.Now.Ticks;
                // Reset lane-snap memory; prior location was likely a
                // different lane / road class.
                lastLaneIndex = int.MinValue;
                // Reset persistent-skew tracker.
                persistentSkewStartTicks = 0;
                // TELEPORT-ONLY EXTRA RESETS. The vehicle-change path above
                // already covered the per-vehicle state. Teleports need to
                // additionally clear the cached threat data and path polyline
                // because the OLD location's obstacles / road tangent would
                // otherwise drive 1-2 frames of wrong steering / braking at
                // the NEW location.
                if (teleported)
                {
                    ResetForTeleport("planar-jump");
                }
            }

            // Lane-keep first. Sets isOnValidRoad as a side effect.
            float leadVehicleSteer = 0f;
            roadSteerCorrection = GetLaneCenterGuidance(playerVeh);
            bool laneKeepOk = isOnValidRoad;

            // ---- PRE-EMPTIVE CURVE BRAKING ----
            // Runs after GetLaneCenterGuidance so lastClosestPolySeg is fresh.
            // Full mode brakes before a sharp bend; Assisted mode only cues.
            float curveBrake = ComputeCurveBrake(playerVeh);
            if (isFullMode)
            {
                curveBrakeRequest = curveBrake;
            }
            else
            {
                curveBrakeRequest = 0f;
                if (curveBrake > 0.05f
                    && DateTime.Now.Ticks - lastCurveCueTicks > 30000000)
                {
                    TriggerRumble(0.4f, 150);
                    Tolk.Speak("Curve ahead");
                    lastCurveCueTicks = DateTime.Now.Ticks;
                }
            }

            // ---- ADAPTIVE CRUISE CONTROL ----
            // Updates accThrottleOut / accBrakeOut for ApplyCachedSteeringInputs.
            ComputeACC(playerVeh, isFullMode);

            // EARLY SKEW DETECTION: ComputeStanleySteer reports isOnValidRoad
            // whenever a polyline exists, even while the car is badly rotated
            // off the road tangent. Stanley keeps demanding full correction but
            // the car keeps skewing. Treat a sustained large heading error as a
            // lane-keep failure so recovery engages BEFORE the polyline
            // collapses at ~75 deg skew and the car is hopelessly sideways.
            if (laneKeepOk && Math.Abs(roadHeadingDelta) > LANEKEEP_SKEW_FAIL_ANGLE)
                skewFailureStreak++;
            else
                skewFailureStreak = 0;
            if (skewFailureStreak >= LANEKEEP_SKEW_FAIL_FRAMES)
                laneKeepOk = false;
            // Iter-10 Patch B: SEVERE-SKEW INSTANT EJECT. The 6-frame streak
            // above is the right gate for moderate skew (~30°), but at 55°+
            // the car is essentially sideways — there's no flicker scenario
            // where one frame at that angle should be excused as transient
            // noise. Force laneKeepOk=false on the very first frame and
            // pre-load the streak so a subsequent OK flicker can't reset it
            // back to LaneKeeping immediately.
            const float LANEKEEP_SKEW_SEVERE_ANGLE = 55f;
            if (Math.Abs(roadHeadingDelta) > LANEKEEP_SKEW_SEVERE_ANGLE)
            {
                laneKeepOk = false;
                if (skewFailureStreak < LANEKEEP_SKEW_FAIL_FRAMES)
                    skewFailureStreak = LANEKEEP_SKEW_FAIL_FRAMES;
            }

            // PERSISTENT-SKEW ESCALATION. Track how long we've been pinned in
            // a heavily-skewed state outside LaneKeeping. Once past the bypass
            // threshold, allow the rate-clamped steering to release in one
            // frame. Past the righten threshold, snap the vehicle upright on
            // the road. Comfort doesn't matter when we're 50+ deg off-axis.
            bool inSkewedRecovery = currentDriveMode != DriveMode.LaneKeeping
                && Math.Abs(roadHeadingDelta) > PERSISTENT_SKEW_ANGLE;
            long skewNow = DateTime.Now.Ticks;
            bypassSteerRateClampThisFrame = false;
            if (inSkewedRecovery)
            {
                if (persistentSkewStartTicks == 0) persistentSkewStartTicks = skewNow;
                long skewMs = (skewNow - persistentSkewStartTicks) / 10000;
                if (skewMs >= PERSISTENT_SKEW_BYPASS_MS)
                    bypassSteerRateClampThisFrame = true;
                if (skewMs >= PERSISTENT_SKEW_RIGHT_MS
                    && playerVeh.Speed < PERSISTENT_SKEW_RIGHT_SPEED
                    && (skewNow - lastSkewRighteningTicks) > SKEW_RIGHTEN_COOLDOWN_TICKS)
                {
                    Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, playerVeh, 5.0f);
                    lastSkewRighteningTicks = skewNow;
                    persistentSkewStartTicks = 0;
                    if (driveLogger != null && driveLogger.IsRunning)
                        driveLogger.Write("[F" + driveLogFrameCount
                            + "] EVENT persistent-skew-righten: skewMs=" + skewMs
                            + " angle=" + roadHeadingDelta.ToString("F1"));
                    RecordDriveDecision("persistent-skew-righten: skewMs=" + skewMs);
                }
            }
            else
            {
                persistentSkewStartTicks = 0;
            }

            // Hysteresis on lane-keep failure (C2 fix). The OLD logic zeroed
            // the streak on a single OK frame, which let the system bounce
            // LaneKeeping <-> AligningHeading every ~60 frames (audit observed
            // streaks climbing to 46+ and failures clustering at the bounce).
            // NEW: decay the streak by 3 on each OK frame (asymmetric — a clean
            // frame in a noisy run is strong positive evidence) but bump by 1
            // on bad frames. Cap so it can't run away. Combined with the
            // sustained-evidence gates below, mode flips now require buffered
            // evidence in BOTH directions.
            // 2026-05-25 fix: prior `--` decay caused the streak to ceiling at
            // 20 and never drop back below LANEKEEP_FAILURE_HYSTERESIS once
            // saturated — every failure event in driveassist-2026-05-25-012227
            // shows laneKeepFailStreak=20.
            if (laneKeepOk && laneKeepFailureStreak > 0) laneKeepFailureStreak -= 3;
            else if (!laneKeepOk)
            {
                laneKeepFailureStreak++;
                lastStreakIncrementTicks = DateTime.Now.Ticks;
            }
            // Wall-clock decay: if the streak hasn't been incremented in the
            // last STREAK_TIME_DECAY_MS, drop it by 1 regardless of laneKeepOk.
            // Stops Mechanism D's "streak stuck at cap" pattern from chaotic
            // detection windows where laneKeepOk flickers True/False.
            if (laneKeepFailureStreak > 0 && lastStreakIncrementTicks > 0
                && (DateTime.Now.Ticks - lastStreakIncrementTicks) / 10000 > STREAK_TIME_DECAY_MS)
            {
                laneKeepFailureStreak--;
                // Slide the timestamp forward so the next decay step needs
                // another full STREAK_TIME_DECAY_MS to elapse.
                lastStreakIncrementTicks = DateTime.Now.Ticks;
            }
            if (laneKeepFailureStreak < 0) laneKeepFailureStreak = 0;
            if (laneKeepFailureStreak > LANEKEEP_FAILURE_STREAK_CAP)
                laneKeepFailureStreak = LANEKEEP_FAILURE_STREAK_CAP;

            // Sustained-evidence gates polled from the rolling history. Fall
            // back to legacy behavior in the first ~200 ms after start /
            // vehicle change (HistorySpansAtLeastMs returns false there).
            long modeNowTicks = DateTime.Now.Ticks;
            long msSinceModeChange = (modeNowTicks - lastModeChangeTicks) / 10000;
            bool transitionLocked = lastModeChangeTicks > 0
                && msSinceModeChange < MODE_CHANGE_HYSTERESIS_MS
                && HistorySpansAtLeastMs(MODE_CHANGE_HYSTERESIS_MS);

            bool useLaneKeep;
            if (transitionLocked)
            {
                // Recently flipped — hold whatever mode we're in. Prevents the
                // sub-second ping-pong the audit identified.
                useLaneKeep = (currentDriveMode == DriveMode.LaneKeeping);
            }
            else if (currentDriveMode == DriveMode.LaneKeeping)
            {
                // STAY in LaneKeeping unless sustained failure. Legacy
                // hysteresis still applies as a fallback for the warmup window.
                // Sustained failure = <20% OK frames in confirm window. The
                // prior "zero OK frames allowed" rule was too strict — a single
                // noisy OK frame in the middle of a clear failure stretch would
                // veto recovery for another full ALIGN_CONFIRM_MS.
                bool sustainedFailure =
                    HistorySpansAtLeastMs(ALIGN_CONFIRM_MS) &&
                    FractionLaneKeepOkInLastMs(ALIGN_CONFIRM_MS) < ALIGN_OK_FRACTION_MAX;
                bool legacyFallback = !HistorySpansAtLeastMs(ALIGN_CONFIRM_MS)
                    && laneKeepFailureStreak >= LANEKEEP_FAILURE_HYSTERESIS;
                // Iter-10 Patch A: SATURATED-STREAK EJECT. If
                // laneKeepFailureStreak has hit its cap (20), we've already
                // registered 20 distinct failure events — overwhelming
                // evidence the car isn't actually lane-keeping. The cap must
                // not be a stable resting state. Force the mode out of
                // LaneKeeping regardless of momentary laneKeepOk flickers.
                // driveassist-2026-05-25-231603 F5397 was 30 seconds in
                // mode=LaneKeeping with failStreak=20 and skewAngle=70.5° —
                // exactly the limbo this gate fixes.
                bool streakSaturated = laneKeepFailureStreak >= LANEKEEP_FAILURE_STREAK_CAP;
                useLaneKeep = (laneKeepOk && !streakSaturated)
                    || !(sustainedFailure || legacyFallback || streakSaturated);
            }
            else
            {
                // RETURN to LaneKeeping ONLY on sustained success — never on a
                // single OK frame (that's what caused the oscillation).
                useLaneKeep = WasOnValidRoadForLastMs(LANEKEEP_CONFIRM_MS);
            }

            DriveMode prevMode = currentDriveMode;

            if (useLaneKeep)
            {
                // Entering LaneKeeping is the canonical "we're OK" event. Reset
                // the failure streak so a saturated cap (20) from a prior
                // recovery cycle doesn't keep us pinned in the legacy-fallback
                // sustained-failure branch on the very next bad frame.
                if (currentDriveMode != DriveMode.LaneKeeping)
                    laneKeepFailureStreak = 0;
                currentDriveMode = DriveMode.LaneKeeping;
                // Re-flag as on-road so downstream gates pass even during a brief
                // detection blip — we keep applying the LAST valid road correction
                // (held by smoothedRoadCorrection from prior cycles).
                isOnValidRoad = true;
                leadVehicleSteer = GetLeadVehicleGuidance(playerVeh);
                if (hasLeadVehicle)
                {
                    // 55% lead vehicle (lateral lane position) + 45% road heading.
                    roadSteerCorrection = roadSteerCorrection * 0.45f + leadVehicleSteer * 0.55f;
                }
                hasRecoveryTarget = false;
                recoveryTargetLatched = false;
                recoveryBrakeRequest = 0f;
                recoveryUturnDir = 0;
                alignmentEngageReverse = false;
                // Clear residual recovery fields so they don't appear in the
                // logs (or feed any future code path) with values from a
                // recovery target the player drove away from minutes ago.
                // Analysis cluster 10 showed recoveryDist=2.6 logged while
                // the player was 510 m from the target.
                recoveryTargetPos = GTA.Math.Vector3.Zero;
                recoveryTargetHeading = 0f;
                recoveryTargetDistance = 999f;
                recoveryHeadingDelta = 0f;
                offroadModeStartTicks = 0; // back on the road; clear timeout
            }
            else
            {
                // Off-road / wrong-way timeout: if we've been stuck in
                // recovery for >3 s, force the lane-keep failure streak back
                // to threshold so FindBestRecoveryNode below re-resolves
                // against the latest vehicle state. Prevents the "stuck
                // forever" failure when a transient recovery target became
                // stale.
                long now = DateTime.Now.Ticks;
                bool timeoutReResolve = false;
                if (offroadModeStartTicks == 0) offroadModeStartTicks = now;
                else if (now - offroadModeStartTicks > OFFROAD_TIMEOUT_TICKS)
                {
                    laneKeepFailureStreak = LANEKEEP_FAILURE_HYSTERESIS;
                    offroadModeStartTicks = now; // re-arm so we re-check in another 3 s
                    timeoutReResolve = true;     // drop the latch so we re-pick
                }

                // LATCHED RECOVERY TARGET: pick a node ONCE when recovery
                // begins and keep pursuing the SAME node. Re-resolving every
                // tick made the car chase a moving "nearest node" and orbit it
                // forever (the ~1900-frame stuck loop in the debug log).
                bool needNewTarget = !recoveryTargetLatched || timeoutReResolve;
                if (recoveryTargetLatched && !timeoutReResolve)
                {
                    // Refresh distance / heading error against the SAME node as
                    // the car moves.
                    recoveryTargetDistance = World.GetDistance(playerVeh.Position, recoveryTargetPos);
                    float hd = recoveryTargetHeading - playerVeh.Heading;
                    while (hd > 180f) hd -= 360f;
                    while (hd < -180f) hd += 360f;
                    recoveryHeadingDelta = hd;
                    hasRecoveryTarget = true;
                    // The car wandered well clear of the latched node — it is
                    // no longer a useful target, so re-resolve.
                    if (recoveryTargetDistance > RECOVERY_TARGET_ABANDON)
                        needNewTarget = true;
                }

                if (needNewTarget)
                {
                    // Lane-keep failed — look for ANY recovery node.
                    hasRecoveryTarget = FindBestRecoveryNode(playerVeh,
                        out recoveryTargetPos, out recoveryTargetHeading,
                        out recoveryTargetDistance, out recoveryHeadingDelta);
                    recoveryTargetLatched = hasRecoveryTarget;

                    if (driveLogger != null && driveLogger.IsRunning)
                        driveLogger.Write("[F" + driveLogFrameCount + "] EVENT recovery-search:"
                            + " found=" + hasRecoveryTarget
                            + " targetPos=" + FmtV(recoveryTargetPos)
                            + " vehHeading=" + playerVeh.Heading.ToString("F1")
                            + " targetHeading=" + recoveryTargetHeading.ToString("F1")
                            + " dist=" + recoveryTargetDistance.ToString("F1")
                            + " headingDelta=" + recoveryHeadingDelta.ToString("F1")
                            + " flipAvoided=" + (Math.Abs(recoveryHeadingDelta) <= 120f));
                    RecordDriveDecision("recovery-search: found=" + hasRecoveryTarget
                        + " dist=" + recoveryTargetDistance.ToString("F1"));
                }

                if (hasRecoveryTarget)
                {
                    // Mode hysteresis: enter RecoveringToRoad past the engage
                    // distance, but once recovering stay there until the target
                    // is comfortably closer than the disengage distance. The
                    // gap kills the flip-flop the log showed at the 8 m line.
                    bool wantRecover = currentDriveMode == DriveMode.RecoveringToRoad
                        ? recoveryTargetDistance > RECOVERY_DISTANCE_DISENGAGE
                        : recoveryTargetDistance > RECOVERY_DISTANCE_ENGAGE;
                    currentDriveMode = wantRecover
                        ? DriveMode.RecoveringToRoad
                        : DriveMode.AligningHeading;
                }
                else
                {
                    // No nodes at all — leave the assist passive.
                    currentDriveMode = DriveMode.LaneKeeping;
                    recoveryTargetLatched = false;
                    recoveryBrakeRequest = 0f;
                    recoveryUturnDir = 0;
                    alignmentEngageReverse = false;
                }

                if (currentDriveMode != DriveMode.LaneKeeping)
                {
                    GetAlignmentRecoverySteer(playerVeh, recoveryTargetPos, recoveryTargetHeading,
                        recoveryTargetDistance, recoveryHeadingDelta,
                        out roadSteerCorrection, out alignmentEngageReverse,
                        out recoveryBrakeRequest);
                    isOnValidRoad = true;            // Downstream code expects a guidance source.
                    roadHeadingDelta = recoveryHeadingDelta;
                    hasLeadVehicle = false;

                    // Iter-9 Patch E: dense-traffic-aware pause. F3527 in
                    // driveassist-2026-05-25-180748 collided at 0.46 m/s
                    // while recovery was steering it through a parking-lot-
                    // density cluster. Pause-mode: count nearby vehicles
                    // within RECOVERY_PAUSE_BLOCK_R (10 m); if >=2 AND the
                    // car has been stalled <2 m/s for RECOVERY_PAUSE_STALL_MS,
                    // hold a brake (RECOVERY_PAUSE_BRAKE) and zero the
                    // recovery steer so the car doesn't fight to push
                    // through traffic. Resume when no vehicle within
                    // RECOVERY_PAUSE_CLOSE_R for RECOVERY_PAUSE_RESUME_MS.
                    long nowEPause = DateTime.Now.Ticks;
                    int nearbyBlockCount = 0;
                    int nearbyCloseCount = 0;
                    foreach (Vehicle nv in World.GetNearbyVehicles(playerVeh.Position,
                        RECOVERY_PAUSE_BLOCK_R + 0.1f))
                    {
                        if (nv == null || !nv.Exists()) continue;
                        if (nv.Handle == playerVeh.Handle) continue;
                        float nd = playerVeh.Position.DistanceTo(nv.Position);
                        if (nd <= RECOVERY_PAUSE_BLOCK_R) nearbyBlockCount++;
                        if (nd <= RECOVERY_PAUSE_CLOSE_R) nearbyCloseCount++;
                    }
                    bool stalled = vehicleSpeed < 2f
                        && VehicleStoppedForLastMs(2f, RECOVERY_PAUSE_STALL_MS);
                    bool blockedEnough = nearbyBlockCount >= RECOVERY_PAUSE_BLOCK_COUNT;

                    if (nearbyCloseCount == 0) recoveryLastClearTicks = nowEPause;

                    // Enter pause if blocked AND stalled, or stay paused until
                    // the resume gate (clear for RECOVERY_PAUSE_RESUME_MS) is
                    // met. recoveryLastClearTicks is updated whenever the
                    // close-band is empty; the resume comparison reads it.
                    bool wasPaused = recoveryPausedSinceTicks > 0;
                    bool stayPaused = wasPaused
                        && (nowEPause - recoveryLastClearTicks) < RECOVERY_PAUSE_RESUME_MS * 10000;
                    // Iter-11 Patch L: 8-second hard timeout escape hatch.
                    // driveassist-2026-05-27-171958 showed 48 pause/resume
                    // events but at least 5 PLAYER markers stuck in pause
                    // for 10+ seconds because the jam blockers never moved.
                    // The resume gate (close-band empty for 500 ms) cannot
                    // fire in a true traffic jam. Force-resume after 8 s of
                    // continuous pause so the recovery steer + brake pipeline
                    // can at least try to nudge through. The existing
                    // UpdateStuckRecovery escalation will take over if the
                    // nudge doesn't make progress.
                    const long RECOVERY_PAUSE_HARD_TIMEOUT_TICKS = 80000000L; // 8 s
                    bool hardTimeout = wasPaused
                        && (nowEPause - recoveryPausedSinceTicks) > RECOVERY_PAUSE_HARD_TIMEOUT_TICKS;
                    bool nowPaused = ((blockedEnough && stalled) || stayPaused) && !hardTimeout;

                    if (nowPaused)
                    {
                        if (!wasPaused)
                        {
                            recoveryPausedSinceTicks = nowEPause;
                            if (driveLogger != null && driveLogger.IsRunning)
                                driveLogger.Write("[F" + driveLogFrameCount
                                    + "] EVENT recovery-paused: nearbyVehicles=" + nearbyBlockCount
                                    + " speed=" + vehicleSpeed.ToString("F2"));
                            RecordDriveDecision("recovery-paused: blockers=" + nearbyBlockCount);
                            recoveryPauseLogged = true;
                        }
                        // Override outputs: hold brake, drop the recovery
                        // steer so the wheel doesn't push toward the target
                        // through bodies.
                        if (recoveryBrakeRequest < RECOVERY_PAUSE_BRAKE)
                            recoveryBrakeRequest = RECOVERY_PAUSE_BRAKE;
                        roadSteerCorrection = 0f;
                    }
                    else if (wasPaused)
                    {
                        // Resume: either surroundings cleared, OR iter-11
                        // Patch L 8-second hard timeout fired. Log
                        // distinguishes the two so post-hoc analysis can
                        // count how often the timeout escape was needed.
                        if (driveLogger != null && driveLogger.IsRunning && recoveryPauseLogged)
                        {
                            if (hardTimeout)
                                driveLogger.Write("[F" + driveLogFrameCount
                                    + "] EVENT recovery-pause-timeout: pausedMs="
                                    + ((nowEPause - recoveryPausedSinceTicks) / 10000));
                            else
                                driveLogger.Write("[F" + driveLogFrameCount
                                    + "] EVENT recovery-resumed: clearMs="
                                    + ((nowEPause - recoveryLastClearTicks) / 10000));
                        }
                        RecordDriveDecision(hardTimeout ? "recovery-pause-timeout" : "recovery-resumed");
                        recoveryPausedSinceTicks = 0;
                        recoveryPauseLogged = false;
                    }
                }
                else
                {
                    recoveryBrakeRequest = 0f;
                    // Leaving recovery — clear the pause state too.
                    if (recoveryPausedSinceTicks > 0)
                    {
                        recoveryPausedSinceTicks = 0;
                        recoveryPauseLogged = false;
                    }
                }
            }

            // Stamp the mode-change time so the next-tick hysteresis gate can
            // measure how long ago we last flipped.
            if (currentDriveMode != prevMode)
                lastModeChangeTicks = DateTime.Now.Ticks;

            // Mode-transition announcements (rate-limited to 5 s).
            if (currentDriveMode != lastAnnouncedMode
                && DateTime.Now.Ticks - lastDriveModeAnnounceTicks > 50000000)
            {
                string modeMsg = null;
                if (currentDriveMode == DriveMode.RecoveringToRoad) modeMsg = "Off road, recovering";
                else if (currentDriveMode == DriveMode.AligningHeading) modeMsg = "Aligning to road";
                else if (lastAnnouncedMode != DriveMode.LaneKeeping) modeMsg = "Aligned";
                if (modeMsg != null) Tolk.Speak(modeMsg, true);
                lastDriveModeAnnounceTicks = DateTime.Now.Ticks;
                lastAnnouncedMode = currentDriveMode;
            }

            if (driveLogger != null && driveLogger.IsRunning && currentDriveMode != driveLogLastMode)
            {
                driveLogger.Write("[F" + driveLogFrameCount + "] EVENT mode-change: "
                    + driveLogLastMode + " -> " + currentDriveMode
                    + " laneKeepOk=" + laneKeepOk
                    + " failStreak=" + laneKeepFailureStreak
                    + " hasRecovery=" + hasRecoveryTarget
                    + " recoveryDist=" + recoveryTargetDistance.ToString("F1"));
                RecordDriveDecision("mode-change: " + driveLogLastMode + " -> " + currentDriveMode
                    + " failStreak=" + laneKeepFailureStreak);
                driveLogLastMode = currentDriveMode;
            }

            // STUCK RECOVERY: detect a recovery mode that is commanding the
            // car but making no physical progress, and escalate. Runs after
            // the mode block so it can override alignmentEngageReverse for the
            // reverse-out layer before ApplySteeringAssist consumes it.
            UpdateStuckRecovery(playerVeh, isFullMode);

            // ============================================
            // SPATIAL AWARENESS - Analyze front/back clearance for handbrake turn decisions
            // ============================================
            float frontClearance = navAssistDistCenter;
            float rearClearance = navAssistDistBehind;
            float leftClearance = navAssistDistLeft;
            float rightClearance = navAssistDistRight;

            // Calculate vehicle skew relative to road heading
            vehicleSkewAngle = roadHeadingDelta; // Already calculated by GetRoadCurveGuidance
            vehicleIsSkewed = Math.Abs(vehicleSkewAngle) > 25f; // More than 25 degrees off road heading

            // Determine if handbrake turn would help correct the situation
            // Conditions for handbrake turn assist:
            // 1. Vehicle is significantly skewed from road direction (>25°)
            // 2. There's an obstacle ahead within braking distance
            // 3. There's clearance on one side to turn into
            // 4. Speed is in the right range (5-20 m/s) - too fast is dangerous
            // 5. Not reversing (handbrake turns don't work well in reverse)
            bool needsHandbrakeTurn = false;
            float handbrakeSteerDir = 0f;

            // Only enable handbrake turns at moderate speeds (5-20 m/s / ~10-45 mph)
            // Too slow: regular steering works fine
            // Too fast: handbrake turn is dangerous and could cause rollover
            bool speedOkForHandbrake = vehicleSpeed > 5f && vehicleSpeed < 20f;

            if (isFullMode && speedOkForHandbrake && !isReversing)
            {
                // Check if we need to make a sharp correction
                // Scale the "blocked" threshold - at higher speeds, need more time to react
                float timeAhead = Math.Max(1.5f, vehicleSpeed / 15f); // 1.5s minimum, scales with speed
                bool frontBlocked = frontClearance < vehicleSpeed * timeAhead;
                bool needsSharpTurn = Math.Abs(roadSteerCorrection) > 0.6f || vehicleIsSkewed;

                // Also require sufficient clearance on the target side (at least 8m)
                float minClearanceForTurn = 8f;

                if (frontBlocked && needsSharpTurn)
                {
                    // Determine which way to turn based on road direction and clearance
                    if (roadSteerCorrection > 0.3f && rightClearance > leftClearance + 3f && rightClearance > minClearanceForTurn)
                    {
                        // Need to turn right and right side is clearer
                        needsHandbrakeTurn = true;
                        handbrakeSteerDir = 1f;
                    }
                    else if (roadSteerCorrection < -0.3f && leftClearance > rightClearance + 3f && leftClearance > minClearanceForTurn)
                    {
                        // Need to turn left and left side is clearer
                        needsHandbrakeTurn = true;
                        handbrakeSteerDir = -1f;
                    }
                    else if (vehicleIsSkewed)
                    {
                        // Vehicle is skewed - turn toward road heading
                        // If skew is positive (road is to our right), turn right
                        if (vehicleSkewAngle > 25f && rightClearance > minClearanceForTurn)
                        {
                            needsHandbrakeTurn = true;
                            handbrakeSteerDir = 1f;
                        }
                        else if (vehicleSkewAngle < -25f && leftClearance > minClearanceForTurn)
                        {
                            needsHandbrakeTurn = true;
                            handbrakeSteerDir = -1f;
                        }
                    }
                }
            }

            // Apply controls if threat detected
            float steerThreshold = isFullMode ? STEER_THRESHOLD_FULL : STEER_THRESHOLD_ASSIST;
            float brakeThreshold = isFullMode ? BRAKE_THRESHOLD_FULL : BRAKE_THRESHOLD_ASSIST;
            float minBrakeDist = isFullMode ? MIN_BRAKE_DISTANCE_FULL : MIN_BRAKE_DISTANCE;

            // LOW-SPEED TTC TIGHTENING: below ~5 mph, normal time-based thresholds
            // produce nuisance brakes because small absolute distances translate to
            // small TTCs. Require an imminent (≤0.3 s) TTC at creep speeds.
            if (vehicleSpeed < BRAKE_LOW_SPEED_CUTOFF)
            {
                brakeThreshold = Math.Min(brakeThreshold, BRAKE_LOW_SPEED_TTC);
            }

            // ============================================
            // NPC AI-STYLE SWERVE-VS-BRAKE DECISION SYSTEM
            // Based on vehicleaihandlinginfo.meta parameters:
            // - At high speed, prefer swerving (fSpeedForSwerving concept)
            // - Use per-obstacle-type distances (fSwerveDist / fBrakingDist)
            // - Consider angle to obstacle (fMinSteerAngleForBraking)
            // ============================================
            bool hasSteerThreat = false;
            bool hasBrakeThreat = false;

            if (closestSteerTTC < float.MaxValue || closestBrakeTTC < float.MaxValue)
            {
                // Get per-type distances
                float swerveDistForType = GetSwerveDistForType(closestType);
                float brakeDistForType = GetBrakeDistForType(closestBrakeType);

                // Speed-scale the distances (faster = need to react earlier)
                float speedScale = Math.Max(1.0f, vehicleSpeed / 10.0f);
                float effectiveSwerveRange = swerveDistForType * speedScale;
                float effectiveBrakeRange = brakeDistForType * speedScale;

                // Calculate angle to the obstacle for swerve-vs-brake decision
                float angleToObstacle = closestSteerThreatPos != GTA.Math.Vector3.Zero
                    ? GetAngleToObstacle(playerVeh, closestSteerThreatPos)
                    : 0f;

                if (vehicleSpeed > SPEED_FOR_SWERVING && angleToObstacle < MIN_STEER_ANGLE_FOR_BRAKING)
                {
                    // HIGH SPEED + obstacle roughly ahead: prefer swerving over braking
                    // Swerve range is larger than brake range, so we start steering earlier
                    float swerveTTC = effectiveSwerveRange / Math.Max(vehicleSpeed, 1f);
                    float brakeTTC = effectiveBrakeRange / Math.Max(vehicleSpeed, 1f);

                    hasSteerThreat = closestSteerTTC < Math.Max(steerThreshold, swerveTTC);
                    // Only brake if obstacle is very close and swerving alone won't work
                    hasBrakeThreat = closestBrakeTTC < Math.Min(brakeThreshold, brakeTTC)
                                    && closestBrakeDistance > BRAKE_MIN_USEFUL_DIST
                                    && closestBrakeTTC < 0.8f; // Very imminent
                }
                else
                {
                    // LOW SPEED or large angle: prefer braking
                    hasBrakeThreat = closestBrakeTTC < brakeThreshold && closestBrakeDistance > BRAKE_MIN_USEFUL_DIST;
                    hasSteerThreat = closestSteerTTC < steerThreshold;
                }

                // Always allow steering for very imminent threats regardless of preference
                if (closestSteerTTC < steerThreshold * 0.5f)
                    hasSteerThreat = true;
                // Always allow braking for very imminent threats regardless of preference
                if (closestBrakeTTC < 0.5f && closestBrakeDistance > BRAKE_MIN_USEFUL_DIST)
                    hasBrakeThreat = true;

                // Iter-11 Patch H: decouple brake from steer for forward-cone
                // threats. The high-speed branch above narrows the brake gate
                // to closestBrakeTTC < 0.8f when steering is preferred, which
                // is the structural cause of 14 (48%) of the player markers
                // in driveassist-2026-05-27-171958 — rear-end collisions at
                // skew 5-30° where the mod chose swerve over brake even
                // though the vehicle ahead was unavoidable. For a TRULY
                // forward threat (angle < 25°), brake and steer should both
                // engage in parallel — the brake pipeline (rampedBrakeInput)
                // and steer pipeline (combinedSteer) write distinct outputs,
                // so coexistence has no architectural conflict.
                if (angleToObstacle < 25f
                    && closestBrakeTTC < brakeThreshold
                    && closestBrakeDistance > BRAKE_MIN_USEFUL_DIST)
                {
                    hasBrakeThreat = true;
                }
            }

            // ============================================
            // FIRST-CONTACT EMERGENCY STOP (REWRITTEN)
            // Previous version multiplied vehicle.Velocity directly to simulate a hard
            // brake. That bypassed the game's brake physics and produced a sudden,
            // unphysical slowdown (the "jerky brake" complaint).
            //
            // Now: announce the emergency, set a flag so ApplyCachedSteeringInputs
            // ramps the brake control input up over a few frames at full magnitude
            // (cachedBrakeMagnitude = 1.0). The game's actual brake/ABS physics handle
            // the deceleration curve, which feels natural instead of teleporting speed.
            // ============================================
            // AVOID-DIRECTION HYSTERESIS. The per-scan threat priority can
            // ping-pong between two sources (e.g. ped-ahead vs wall-from-
            // static-cast) and flip avoidDirection sign on neighbouring
            // frames, yaw-oscillating the wheel. The previous gate compared
            // only against cachedAvoidDirection, so +1 -> 0 -> -1 published
            // -1 instantly because the intermediate 0 reset the cache. New
            // log driveassist-2026-05-25-111452 F478-F502 is exactly that
            // pattern.
            //
            // Now: track the most recent NON-ZERO published direction (with a
            // 500 ms memory window). Opposite-sign re-acquisition against
            // memory requires AVOID_DIR_FLIP_HOLD consecutive frames before
            // publishing — even when the immediately previous cached value
            // was zero. Clearing to 0 still publishes immediately because
            // zero is always safe.
            long avoidNow = DateTime.Now.Ticks;
            bool memoryStale = lastNonZeroAvoidDir == 0
                || (avoidNow - lastNonZeroAvoidDirTicks) > AVOID_DIR_MEMORY_TICKS;
            int publishedAvoidDir;
            if (avoidDirection == 0)
            {
                publishedAvoidDir = 0;
                proposedAvoidDirection = 0;
                avoidDirHoldFrames = 0;
            }
            else if (memoryStale || avoidDirection == lastNonZeroAvoidDir)
            {
                // No conflicting recent memory, or same sign as memory —
                // publish immediately.
                publishedAvoidDir = avoidDirection;
                proposedAvoidDirection = avoidDirection;
                avoidDirHoldFrames = 0;
            }
            else
            {
                // Opposite sign vs recent memory — require sustained evidence.
                if (avoidDirection == proposedAvoidDirection)
                    avoidDirHoldFrames++;
                else
                {
                    proposedAvoidDirection = avoidDirection;
                    avoidDirHoldFrames = 1;
                }
                publishedAvoidDir = avoidDirHoldFrames >= AVOID_DIR_FLIP_HOLD
                    ? avoidDirection
                    : (cachedAvoidDirection != 0 ? cachedAvoidDirection : lastNonZeroAvoidDir);
            }
            if (publishedAvoidDir != 0)
            {
                lastNonZeroAvoidDir = publishedAvoidDir;
                lastNonZeroAvoidDirTicks = avoidNow;
            }

            // Real release-distance hysteresis. The old single-threshold
            // gate (obstacleInCriticalZone = dist <= minBrakeDist) flips on/off
            // every frame when raycast distance jitters across the threshold,
            // re-firing the Tolk "Emergency brake!" line and re-latching
            // emergencyBrakeActive at the underlying scan cadence. Separate
            // ENTER and RELEASE distances stop that flip-flop:
            //   ENTER   = minBrakeDist            (3 m assist / 5 m full)
            //   RELEASE = minBrakeDist + 1.5 m    (4.5 m / 6.5 m)
            // wasObstacleInBrakeZone now tracks the latched zone state, not
            // just last-frame entry, so the release threshold is what unlatches
            // it. Also requires >= CRITICAL_ARM_FRAMES consecutive entry frames
            // before firing — a single noisy scan can't slam the brake.
            const float CRITICAL_RELEASE_MARGIN = 1.5f;
            const int CRITICAL_ARM_FRAMES = 2;
            float releaseDist = minBrakeDist + CRITICAL_RELEASE_MARGIN;
            bool obstacleEnter   = closestBrakeDistance <= minBrakeDist && closestBrakeDistance < 999f;
            bool obstacleRelease = closestBrakeDistance > releaseDist || closestBrakeDistance >= 999f;
            if (obstacleEnter) criticalZoneArmFrames++;
            else criticalZoneArmFrames = 0;
            bool obstacleInCriticalZone;
            if (wasObstacleInBrakeZone)
            {
                // Already latched — only unlatch when we cross the release band.
                obstacleInCriticalZone = !obstacleRelease;
            }
            else
            {
                // Not latched — arm only after N consecutive entry frames.
                obstacleInCriticalZone = obstacleEnter && criticalZoneArmFrames >= CRITICAL_ARM_FRAMES;
            }

            if (obstacleInCriticalZone && !wasObstacleInBrakeZone && !cachedIsBraking && vehicleSpeed > 1f)
            {
                emergencyBrakeActive = true;

                // Light steering nudge away from the obstacle (only in Full mode).
                // Use the hysteresis-gated direction so a flipping scan can't
                // yank the wheel the wrong way on first contact.
                if (publishedAvoidDir != 0 && isFullMode)
                {
                    smoothedSteerCorrection = publishedAvoidDir * 0.5f;
                }

                if (DateTime.Now.Ticks - lastAssistAnnounceTicks > 10000000)
                {
                    Tolk.Speak("Emergency brake!", true);
                    lastAssistAnnounceTicks = DateTime.Now.Ticks;
                }
            }
            else if (!obstacleInCriticalZone)
            {
                emergencyBrakeActive = false;
            }

            // Update tracking for next frame
            wasObstacleInBrakeZone = obstacleInCriticalZone;

            bool hasThreat = hasSteerThreat || hasBrakeThreat;

            // Check if we need to auto-teleport back to road
            CheckRoadTeleport(playerVeh, isFullMode, closestSteerTTC);

            // Always activate if we have road guidance or a threat
            if (hasThreat
                || (isOnValidRoad && Math.Abs(roadSteerCorrection) > 0.05f)
                || needsHandbrakeTurn
                || currentDriveMode != DriveMode.LaneKeeping)
            {
                steeringAssistActive = true;
                // Pass both steer and brake TTCs to the assist function.
                // publishedAvoidDir is the hysteresis-gated direction computed
                // just above the emergency-brake first-contact block.
                ApplySteeringAssist(playerVeh, closestSteerTTC, closestBrakeTTC, closestBrakeDistance, publishedAvoidDir, isFullMode, steerThreshold, brakeThreshold, hasSteerThreat, hasBrakeThreat, needsHandbrakeTurn, handbrakeSteerDir);
            }
            else if (steeringAssistActive)
            {
                steeringAssistActive = false;
                smoothedSteerCorrection = 0f;
                smoothedRoadCorrection = 0f;
                cachedHandbrakeMagnitude = 0f;
                previousFrameSteer = 0f;
                brakeArmed = false;
                try { outBrakeWarn.Stop(); } catch { }
                // Drop the rolling history so re-enable starts from fail-safe
                // (gates default to legacy behavior until 200 ms accumulates).
                historyHead  = 0;
                historyCount = 0;
            }

            // Record this tick's final state into the rolling-history ring.
            // Single canonical write site — every gate that polls the buffer
            // sees a consistent snapshot of the decisions we just made.
            PushDriveAssistSnapshot(playerVeh, closestSteerTTC, closestBrakeTTC,
                hasSteerThreat, hasBrakeThreat);
        }

        /// <summary>
        /// Calculates time to collision based on relative velocity.
        /// Now handles stationary obstacles and uses vehicle size for collision radius.
        /// </summary>
        private float CalculateTTC(Vehicle playerVeh, GTA.Math.Vector3 targetPos, GTA.Math.Vector3 targetVel)
        {
            GTA.Math.Vector3 relPos = targetPos - playerVeh.Position;
            GTA.Math.Vector3 relVel = targetVel - playerVeh.Velocity;

            float distance = relPos.Length();
            if (distance < 0.1f) return 0.1f;

            // Calculate collision radius based on vehicle size
            float collisionRadius = GetVehicleCollisionRadius(playerVeh);

            float closingSpeed = -GTA.Math.Vector3.Dot(GTA.Math.Vector3.Normalize(relPos), relVel);

            // Handle stationary or slowly moving obstacles
            // Only consider them threats if they're DIRECTLY ahead (high dot product) and relatively close
            if (closingSpeed <= 0.5f)
            {
                // Check if obstacle is in our path (ahead or behind depending on direction)
                GTA.Math.Vector3 moveDir = isReversing ? -playerVeh.ForwardVector : playerVeh.ForwardVector;
                float dotToTarget = GTA.Math.Vector3.Dot(moveDir, GTA.Math.Vector3.Normalize(relPos));

                // Static obstacle ahead test. 0.85 ≈ 30° cone. The widened 0.6
                // cone fired for curbs and parked cars off to the side, causing
                // phantom brakes in dense traffic; static side hazards instead
                // feed the wider raycast fan (steering only).
                if (dotToTarget > 0.85f)
                {
                    // Use our own speed as closing speed for stationary obstacles
                    float ourSpeed = playerVeh.Speed;
                    if (ourSpeed > 1f)
                    {
                        closingSpeed = ourSpeed * dotToTarget;
                    }
                    else
                    {
                        // Very slow/stopped - only worry about VERY close stationary obstacles
                        if (distance < 5f) // Reduced from 15f to 5f
                        {
                            return distance / 3f; // Artificial TTC based on distance
                        }
                        return float.MaxValue;
                    }
                }
                else
                {
                    return float.MaxValue; // Not directly in our path
                }
            }

            if (distance < collisionRadius) return 0.1f;
            return (distance - collisionRadius) / closingSpeed;
        }

        /// <summary>
        /// Gets collision radius based on vehicle type.
        /// Larger vehicles need more clearance.
        /// </summary>
        private float GetVehicleCollisionRadius(Vehicle veh)
        {
            // Vehicle class lookup is a pure property read — no exceptions possible.
            switch (veh.ClassType)
            {
                case VehicleClass.Motorcycles:
                case VehicleClass.Cycles:
                    return BASE_COLLISION_RADIUS;
                case VehicleClass.Compacts:
                case VehicleClass.Coupes:
                    return BASE_COLLISION_RADIUS + 0.5f;
                case VehicleClass.Sedans:
                case VehicleClass.Sports:
                case VehicleClass.SportsClassics:
                case VehicleClass.Muscle:
                    return BASE_COLLISION_RADIUS + 1f;
                case VehicleClass.SUVs:
                case VehicleClass.OffRoad:
                case VehicleClass.Vans:
                    return BASE_COLLISION_RADIUS + 1.5f;
                case VehicleClass.Industrial:
                case VehicleClass.Commercial:
                case VehicleClass.Utility:
                    return BASE_COLLISION_RADIUS + 2.5f; // Trucks
                case VehicleClass.Super:
                    return BASE_COLLISION_RADIUS + 1f;   // Wide but low
                default:
                    return BASE_COLLISION_RADIUS + 1f;
            }
        }

        /// <summary>
        /// Determines the direction of a threat relative to the vehicle.
        /// Now properly handles "behind" direction and accounts for reverse driving.
        /// </summary>
        private string GetThreatDirection(Vehicle playerVeh, GTA.Math.Vector3 threatPos)
        {
            GTA.Math.Vector3 toThreat = GTA.Math.Vector3.Normalize(threatPos - playerVeh.Position);
            float dotForward = GTA.Math.Vector3.Dot(playerVeh.ForwardVector, toThreat);
            float dotRight = GTA.Math.Vector3.Dot(playerVeh.RightVector, toThreat);

            // Direction labels stay anchored to the vehicle (== camera) frame even when
            // reversing. The camera doesn't flip when you reverse in GTA V, and blind
            // users navigate by the audible direction — a threat on the physical right
            // side of the car must always be announced as "right" regardless of which
            // way the car is moving. Only the forward axis flips with travel direction
            // so the "ahead" label tracks the direction of motion.
            if (isReversing)
            {
                if (dotForward < -0.5f) return "ahead";   // Direction of travel
                if (dotForward > 0.5f) return "behind";   // Opposite of travel
                if (dotRight > 0.3f) return "right";
                if (dotRight < -0.3f) return "left";
                return "ahead";
            }
            else
            {
                if (dotForward > 0.5f) return "ahead";
                if (dotForward < -0.5f) return "behind";
                if (dotRight > 0.3f) return "right";
                if (dotRight < -0.3f) return "left";
                return "ahead";
            }
        }

        /// <summary>
        /// Determines which direction to steer to avoid a threat
        /// Returns: -1 for left, 0 for straight (brake only), 1 for right
        /// </summary>
        private int GetAvoidDirection(Vehicle playerVeh, GTA.Math.Vector3 threatPos)
        {
            GTA.Math.Vector3 toThreat = GTA.Math.Vector3.Normalize(threatPos - playerVeh.Position);
            float dotRight = GTA.Math.Vector3.Dot(playerVeh.RightVector, toThreat);

            if (dotRight > 0.2f) return -1;  // Threat on right, steer left
            if (dotRight < -0.2f) return 1;  // Threat on left, steer right
            return GetClearerSide(playerVeh, 15f);
        }

        /// <summary>
        /// Uses raycasts to determine which side has more clearance
        /// Returns: -1 for left clearer, 1 for right clearer, 0 for equal
        /// </summary>
        private int GetClearerSide(Vehicle playerVeh, float checkDist)
        {
            GTA.Math.Vector3 startPos = playerVeh.Position + new GTA.Math.Vector3(0, 0, 0.5f);
            GTA.Math.Vector3 forwardVec = playerVeh.ForwardVector;
            GTA.Math.Vector3 rightVec = playerVeh.RightVector;

            GTA.Math.Vector3 leftDir = GTA.Math.Vector3.Normalize(forwardVec - rightVec);
            GTA.Math.Vector3 rightDir = GTA.Math.Vector3.Normalize(forwardVec + rightVec);

            RaycastResult leftRay = World.Raycast(startPos, startPos + leftDir * checkDist,
                IntersectFlags.Everything, playerVeh);
            RaycastResult rightRay = World.Raycast(startPos, startPos + rightDir * checkDist,
                IntersectFlags.Everything, playerVeh);

            float leftClear = leftRay.DidHit ? World.GetDistance(startPos, leftRay.HitPosition) : checkDist;
            float rightClear = rightRay.DidHit ? World.GetDistance(startPos, rightRay.HitPosition) : checkDist;

            if (leftClear > rightClear + 2f) return -1;
            if (rightClear > leftClear + 2f) return 1;
            return 0;
        }

        /// <summary>
        /// NPC AI-inspired lateral offset avoidance.
        /// Instead of binary left/right, calculates the proportional steering magnitude
        /// needed to clear an obstacle based on actual lateral clearance deficit.
        /// Returns -1.0 (full left) to 1.0 (full right), proportional to the offset needed.
        /// </summary>
        private float CalculateLateralAvoidance(Vehicle playerVeh, GTA.Math.Vector3 obstaclePos,
            string obstacleType, float obstacleDistance)
        {
            GTA.Math.Vector3 toObstacle = obstaclePos - playerVeh.Position;
            GTA.Math.Vector3 forward = isReversing ? -playerVeh.ForwardVector : playerVeh.ForwardVector;
            GTA.Math.Vector3 right = isReversing ? -playerVeh.RightVector : playerVeh.RightVector;

            // Calculate the obstacle's lateral position relative to our forward path
            float lateralPos = GTA.Math.Vector3.Dot(right, toObstacle);
            float forwardPos = GTA.Math.Vector3.Dot(forward, toObstacle);

            // If obstacle is behind us, ignore
            if (forwardPos < 0) return 0f;

            // Get the required clearance for this obstacle type (from vehicleaihandlinginfo.meta concepts)
            float requiredClearance = GetLateralClearanceForType(obstacleType)
                                    + GetVehicleCollisionRadius(playerVeh);

            // Calculate how much lateral offset we need. We add a bumper margin so
            // the controller starts nudging *before* clearance bottoms out — the old
            // `>=` cliff produced zero correction at exactly the threshold, which
            // is how the player ended up side-by-side touching mirrors.
            float currentClearance = Math.Abs(lateralPos);
            if (currentClearance > requiredClearance + LATERAL_AVOID_MARGIN)
            {
                return 0f;  // Comfortably clear — no correction needed
            }

            // Calculate the steering correction proportional to the deficit. Deficit
            // now ramps smoothly from `LATERAL_AVOID_MARGIN` outward to `requiredClearance`
            // inward, instead of jumping on/off at the threshold.
            float deficit = (requiredClearance + LATERAL_AVOID_MARGIN) - currentClearance;
            float maxDeficit = requiredClearance + LATERAL_AVOID_MARGIN;
            float urgency = Math.Min(1.0f, deficit / Math.Max(0.01f, maxDeficit));

            // Direction: steer away from obstacle
            float direction = lateralPos >= 0 ? -1f : 1f;

            // Scale by distance: closer obstacles need more urgent correction
            float distanceFactor = Math.Max(0.3f, 1.0f - (obstacleDistance / 30f));
            // Static obstacles (rails/walls) get a much stronger pull. They don't
            // move out of the way, and the user still reported side collisions
            // even with the 1.5× boost — going to 2.0× plus a higher minimum.
            if (obstacleType == "obstacle")
            {
                distanceFactor = Math.Max(0.7f, Math.Min(2.0f, distanceFactor * 2.0f));
            }

            // Verify the chosen side is actually clear
            int clearerSide = GetClearerSide(playerVeh, 15f);
            if (clearerSide != 0 && Math.Sign(direction) != clearerSide)
            {
                // The natural avoid direction is blocked -- use the clearer side instead
                direction = clearerSide;
                urgency *= 0.7f;  // Reduced magnitude since it's a less ideal path
            }

            // ADJACENT-VEHICLE SUPPRESSION MASK: don't steer toward a vehicle that
            // is (or recently was) in our shoulder band. Latch state was set by
            // UpdateAdjacentLatch during the current detection pass.
            if (direction > 0 && adjacentLatchedRight) return 0f; // Can't go right
            if (direction < 0 && adjacentLatchedLeft)  return 0f; // Can't go left

            return direction * urgency * distanceFactor;
        }

        /// <summary>
        /// Gets the lateral clearance distance for a given obstacle type.
        /// Based on vehicleaihandlinginfo.meta fAvoidanceDist parameters.
        /// </summary>
        private float GetLateralClearanceForType(string obstacleType)
        {
            switch (obstacleType)
            {
                case "vehicle": return AVOID_LATERAL_VEHICLE;
                case "pedestrian": return AVOID_LATERAL_PED;
                default: return AVOID_LATERAL_OBJECT;
            }
        }

        /// <summary>
        /// Gets the angle (in degrees) between the vehicle's travel direction and an obstacle.
        /// Used for the swerve-vs-brake decision system.
        /// </summary>
        private float GetAngleToObstacle(Vehicle playerVeh, GTA.Math.Vector3 obstaclePos)
        {
            GTA.Math.Vector3 toObstacle = GTA.Math.Vector3.Normalize(obstaclePos - playerVeh.Position);
            GTA.Math.Vector3 forward = isReversing ? -playerVeh.ForwardVector : playerVeh.ForwardVector;
            float dot = GTA.Math.Vector3.Dot(forward, toObstacle);
            return (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, dot))) * (180.0 / Math.PI));
        }

        /// <summary>
        /// Gets the swerve initiation distance for a given obstacle type.
        /// Based on vehicleaihandlinginfo.meta fSwerveDist parameters.
        /// </summary>
        private float GetSwerveDistForType(string obstacleType)
        {
            switch (obstacleType)
            {
                case "vehicle": return SWERVE_DIST_VEHICLE;
                case "pedestrian": return SWERVE_DIST_PED;
                default: return SWERVE_DIST_OBJECT;
            }
        }

        /// <summary>
        /// Gets the braking initiation distance for a given obstacle type.
        /// Based on vehicleaihandlinginfo.meta fBrakingDist parameters.
        /// </summary>
        private float GetBrakeDistForType(string obstacleType)
        {
            switch (obstacleType)
            {
                case "vehicle": return BRAKE_DIST_VEHICLE;
                case "pedestrian": return BRAKE_DIST_PED;
                default: return BRAKE_DIST_OBJECT;
            }
        }

        /// <summary>
        /// Checks if a position is on the road surface using the GTA V native IS_POINT_ON_ROAD.
        /// Used to filter out parked vehicles on shoulders/sidewalks.
        /// </summary>
        private bool IsPositionOnRoad(GTA.Math.Vector3 position)
        {
            // IS_POINT_ON_ROAD (0x125BF4ABFC536B09) is a pure read native — it can't fail
            // in normal execution. The previous try/catch defaulted to "true" on failure
            // (treat as threat), so removing it preserves identical observable behavior
            // when the native does return something sensible.
            return Function.Call<bool>((Hash)0x125BF4ABFC536B09,
                position.X, position.Y, position.Z, 0);
        }

        /// <summary>
        /// Iter-12 Patch Q: returns true if the position is on a named street.
        /// GET_STREET_NAME_AT_COORD writes a street-name hash and a crossing
        /// hash; the street-name hash is non-zero only on named roads (paved
        /// or dirt with a name). Pure off-road (open desert, fields, large
        /// parking lots, construction zones) all return streetHash=0. This
        /// complements IS_POINT_ON_ROAD which returns true for any nav-graph
        /// node including unnamed dirt nodes in Sandy Shores.
        /// </summary>
        private bool IsOnNamedStreet(GTA.Math.Vector3 position)
        {
            OutputArgument outStreet   = new OutputArgument();
            OutputArgument outCrossing = new OutputArgument();
            // GET_STREET_NAME_AT_COORD = 0x2EB41072B4C1E4C0
            Function.Call((Hash)0x2EB41072B4C1E4C0,
                position.X, position.Y, position.Z, outStreet, outCrossing);
            return outStreet.GetResult<uint>() != 0;
        }

        /// <summary>
        /// Checks if a stationary obstacle is actually in the vehicle's projected travel lane.
        /// Combines road context with lane-width analysis to filter false positives from
        /// parked cars on shoulders. Moving obstacles always pass this check.
        /// </summary>
        private bool IsObstacleInTravelLane(Vehicle playerVeh, GTA.Math.Vector3 obstaclePos,
            float obstacleSpeed)
        {
            // Moving obstacles are always relevant (active traffic)
            if (obstacleSpeed > 1.0f) return true;

            // For stationary obstacles, check if they're on the road
            if (!IsPositionOnRoad(obstaclePos)) return false;

            // Even if on road, check if the obstacle is in our projected path
            GTA.Math.Vector3 toObstacle = obstaclePos - playerVeh.Position;
            GTA.Math.Vector3 forward = isReversing ? -playerVeh.ForwardVector : playerVeh.ForwardVector;
            GTA.Math.Vector3 right = playerVeh.RightVector;

            float lateralOffset = Math.Abs(GTA.Math.Vector3.Dot(right, toObstacle));
            float forwardDist = GTA.Math.Vector3.Dot(forward, toObstacle);

            // Not ahead of us
            if (forwardDist < 0) return false;

            // Lane width is road-type-aware: a freeway lane is ~4m, a city street
            // ~3.5m, an alley ~2.5m. MapDb returns "unknown" outside classified
            // polygons and we fall back to the surface preset (3.5m, legacy value).
            float laneWidth = GetLanePreset(MapDb.GetRoadTypeAt(playerVeh.Position)).widthM
                              + GetVehicleCollisionRadius(playerVeh);
            return lateralOffset < laneWidth;
        }

        /// <summary>
        /// Finds the best vehicle ahead traveling in the same direction to follow.
        /// Following a lead vehicle provides natural lane keeping since traffic stays in-lane.
        /// Returns a lateral steering correction to match the lead vehicle's lane position.
        /// </summary>
        private float GetLeadVehicleGuidance(Vehicle playerVeh)
        {
            hasLeadVehicle = false;
            leadVehicle = null;
            leadVehicleLateralOffset = 0f;

            GTA.Math.Vector3 playerPos = playerVeh.Position;
            GTA.Math.Vector3 forwardVec = playerVeh.ForwardVector;
            float vehicleSpeed = playerVeh.Speed;
            float vehicleHeading = playerVeh.Heading;

            // Search range scales with speed AND vehicle class (iter-8). Was
            // a class-agnostic `vehicleSpeed * 3f`; now uses the meta brake-
            // lookahead formula so a truck (longer required following gap)
            // looks further ahead than a sports car at the same speed.
            // Same 15-60 m clamp preserves the existing ACC range envelope.
            var accAi = VehicleAIHandlingRegistry.GetForVehicle(playerVeh);
            float searchRange = Math.Max(15f, Math.Min(60f,
                accAi.BrakeLookaheadForSpeed(vehicleSpeed) * 1.3f));

            Vehicle[] nearbyVehicles = World.GetNearbyVehicles(playerPos, searchRange);
            if (nearbyVehicles == null || nearbyVehicles.Length == 0)
                return 0f;

            Vehicle bestLead = null;
            float bestScore = float.MaxValue; // Lower is better (closest suitable vehicle)

            foreach (Vehicle veh in nearbyVehicles)
            {
                if (veh == null || !veh.Exists() || veh == playerVeh)
                    continue;

                // Must be moving (not parked)
                if (veh.Speed < 2f)
                    continue;

                GTA.Math.Vector3 toVeh = veh.Position - playerPos;
                float distance = toVeh.Length();

                // Must be ahead of us
                float dotForward = GTA.Math.Vector3.Dot(forwardVec, GTA.Math.Vector3.Normalize(toVeh));
                if (dotForward < 0.7f) // Must be roughly ahead (within ~45 degrees)
                    continue;

                // Must be traveling in a similar direction (same lane direction)
                float headingDiff = veh.Heading - vehicleHeading;
                while (headingDiff > 180f) headingDiff -= 360f;
                while (headingDiff < -180f) headingDiff += 360f;
                if (Math.Abs(headingDiff) > 30f) // Must be within 30 degrees of our heading
                    continue;

                // Must not be too far to the side (within ~2 lanes)
                float lateralDist = Math.Abs(GTA.Math.Vector3.Dot(playerVeh.RightVector, toVeh));
                if (lateralDist > 8f) // More than ~2 lane widths away
                    continue;

                // Score: prefer closest vehicle that's directly ahead
                // Weight forward distance more than lateral distance
                float forwardDist = GTA.Math.Vector3.Dot(forwardVec, toVeh);
                float score = forwardDist + lateralDist * 2f; // Penalize lateral offset

                if (score < bestScore)
                {
                    bestScore = score;
                    bestLead = veh;
                }
            }

            if (bestLead == null)
                return 0f;

            // Found a lead vehicle - calculate lateral offset to match their position
            hasLeadVehicle = true;
            leadVehicle = bestLead;

            GTA.Math.Vector3 toLeadVeh = bestLead.Position - playerPos;
            float leadLateral = GTA.Math.Vector3.Dot(playerVeh.RightVector, toLeadVeh);
            leadVehicleLateralOffset = leadLateral;

            // Steer toward the lead vehicle's lateral position
            // Scale correction by how far off we are, gentle at small offsets
            float lateralCorrection = leadLateral / 6f; // Gentle: 6m offset = full correction
            lateralCorrection = Math.Max(-0.5f, Math.Min(0.5f, lateralCorrection)); // Cap at 0.5

            return lateralCorrection;
        }

        /// <summary>
        /// Heading-only road guidance with gentle lateral nudge toward the nearest same-direction node.
        ///
        /// KEY DESIGN PRINCIPLE: On divided highways, GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING
        /// returns nodes at road-center for ALL lanes (both directions). We CANNOT reliably determine
        /// which side of a node is "our lane" because nodes don't carry that information.
        ///
        /// Instead, this method focuses almost entirely on HEADING ALIGNMENT:
        /// - Match the road's heading direction (keeps us going straight on the road)
        /// - Apply only a very small lateral nudge toward the node (keeps us from drifting off-road)
        /// - Use a TIGHT heading filter (25°) to strongly reject opposite-direction nodes on freeways
        /// - Additionally require that the node is ahead of us (positive forward dot), not behind/beside
        ///
        /// This avoids the trap of steering toward road-center (which is the median on a divided highway).
        /// </summary>
        // PURE-PURSUIT LANE KEEPING (rewritten)
        // ======================================
        // The previous implementation was a pure proportional controller on heading
        // difference: every degree of misalignment produced a non-zero steering input,
        // and small noise (suspension, road-node jitter) generated a small correction
        // that caused more misalignment that generated a larger correction → the
        // vehicle drove in circles even on a straight road with no obstacles.
        //
        // This version uses three classic fixes:
        //   1. PURE PURSUIT: pick a target point ~2 seconds of travel ahead and steer
        //      toward it geometrically (atan2 of lateral/forward components). This is
        //      smoother than chasing the angular delta to a node directly because the
        //      lookahead distance acts as natural damping — the further ahead you aim,
        //      the smaller the steering response to small heading errors.
        //   2. DEADBAND: ignore tiny pursuit angles (<2°) and tiny lateral offsets
        //      (<0.75 m). On a straight road with normal noise this returns 0.0, so
        //      the wheel stops moving entirely.
        //   3. RATE LIMIT: cap how fast the correction can change per second. This
        //      damps any residual oscillation without softening response to real
        //      curves (which evolve over many frames).
        /// <summary>
        /// Derives the player's actual lane center from a path-node centerline.
        /// GTA path nodes sit on the road centerline, not a lane center; GTA V drives
        /// on the right. This offsets to the proper right-hand carriageway and snaps
        /// to whichever lane the player is closest to. Used as the pure-pursuit goal.
        /// </summary>
        private GTA.Math.Vector3 LaneCenterFromNode(GTA.Math.Vector3 nodeCenter,
            float nodeHeadingDeg, int totalLanes, bool isHighway, GTA.Math.Vector3 playerPos)
        {
            if (totalLanes <= 1)
            {
                // One-way or unknown: node center IS the lane center.
                return nodeCenter;
            }

            // Right-perpendicular to node heading in world XY. GTA heading convention:
            // 0° faces +Y, increasing CCW from above. Forward = (-sin H, cos H, 0),
            // so right (90° clockwise from forward) = (cos H, sin H, 0).
            double H = nodeHeadingDeg * Math.PI / 180.0;
            GTA.Math.Vector3 right = new GTA.Math.Vector3(
                (float)Math.Cos(H), (float)Math.Sin(H), 0f);

            float laneWidth = isHighway ? LANE_WIDTH_HIGHWAY : LANE_WIDTH_SURFACE;
            int K = Math.Max(1, (totalLanes + 1) / 2);   // lanes per direction (ceil)

            // Carriageway center is offset right of road centerline by half the
            // total per-direction width.
            GTA.Math.Vector3 carriageCenter = nodeCenter + right * (K * laneWidth * 0.5f);

            // Snap to the lane the player is closest to.
            float playerOffset = GTA.Math.Vector3.Dot(playerPos - carriageCenter, right);
            int rawLaneIndex = (int)Math.Round(playerOffset / laneWidth);
            rawLaneIndex = Math.Max(-(K - 1) / 2, Math.Min((K - 1) / 2, rawLaneIndex));

            // Hysteresis: require crossing the boundary by 0.4 m before accepting
            // a one-step change. Prevents lane-snap flicker when the player sits
            // near a lane boundary.
            int laneIndex = rawLaneIndex;
            if (lastLaneIndex != int.MinValue && Math.Abs(rawLaneIndex - lastLaneIndex) == 1)
            {
                float boundaryOffset = (lastLaneIndex + Math.Sign(rawLaneIndex - lastLaneIndex) * 0.5f) * laneWidth;
                float overshoot = (playerOffset - boundaryOffset) * Math.Sign(rawLaneIndex - lastLaneIndex);
                if (overshoot < LANE_CHANGE_HYSTERESIS_M)
                    laneIndex = lastLaneIndex;
            }
            lastLaneIndex = laneIndex;

            return carriageCenter + right * (laneIndex * laneWidth);
        }

        // GetLaneCenterGuidance — Stanley lateral controller over the
        // prebuilt path polyline (see BuildPathPolyline).
        //
        // What changed (Path-Aware Drive Assist):
        //   - Source: pathPolyline is composed once per detection tick from
        //     GPS route / static node graph (67k+ shipped nodes) / live
        //     native, in priority order. Replaces the single noisy
        //     GET_NTH_CLOSEST_VEHICLE_NODE_FAVOUR_DIRECTION sample that
        //     caused side-to-side drift as the favoured node snapped between
        //     parallel lanes.
        //   - Controller: Stanley (Hoffmann et al., DARPA 2005).
        //     δ = -ψ_e + atan2(k·e, v+ε). Stable at low lookahead — pure
        //     pursuit oscillates here.
        //   - The body lives in ComputeStanleySteer; this wrapper exists so
        //     the rest of ProcessSteeringAssist's call site is unchanged.
        private float GetLaneCenterGuidance(Vehicle playerVeh)
        {
            float stanley = ComputeStanleySteer(playerVeh);
            lastRoadCorrection = stanley;
            return stanley;
        }

        // Legacy pure-pursuit implementation retained for reference / fallback;
        // not called. Delete once Path-Aware Drive Assist has soaked.
        private float GetLaneCenterGuidance_PurePursuit_Legacy(Vehicle playerVeh)
        {
            GTA.Math.Vector3 playerPos = playerVeh.Position;
            float vehicleSpeed = playerVeh.Speed;
            GTA.Math.Vector3 forwardVec = playerVeh.ForwardVector;
            GTA.Math.Vector3 rightVec = playerVeh.RightVector;

            // Adaptive lookahead: L_d = k_v·v + L_min, clamped to [L_min, L_max].
            float Ld = Math.Max(PURE_PURSUIT_L_MIN,
                       Math.Min(PURE_PURSUIT_L_MAX, PURE_PURSUIT_K_V * vehicleSpeed + PURE_PURSUIT_L_MIN));
            GTA.Math.Vector3 lookAheadPoint = playerPos + forwardVec * Ld;

            // ---- FAVOUR-DIRECTION NODE FETCH ----
            // GET_NTH_CLOSEST_VEHICLE_NODE_FAVOUR_DIRECTION = 0x45905BE8654AE067
            // Signature: (x, y, z, desiredX, desiredY, desiredZ, n, outPos, outHeading,
            //             nodeFlags, p10=3.0, p11=0)
            GTA.Math.Vector3 desired = lookAheadPoint + forwardVec; // bias direction
            OutputArgument outFavPos = new OutputArgument();
            OutputArgument outFavHeading = new OutputArgument();
            bool gotFav = Function.Call<bool>((Hash)0x45905BE8654AE067,
                lookAheadPoint.X, lookAheadPoint.Y, lookAheadPoint.Z,
                desired.X, desired.Y, desired.Z,
                1, outFavPos, outFavHeading, 0 /* paved-only flags */, 3.0f, 0f);

            if (!gotFav)
            {
                isOnValidRoad = false;
                roadHeadingDelta = 0f;
                ppGoalInitialized = false;
                lastRoadCorrection *= 0.5f; // decay so stale input doesn't linger
                return lastRoadCorrection;
            }

            GTA.Math.Vector3 nodePos = outFavPos.GetResult<GTA.Math.Vector3>();
            float nodeHeading = outFavHeading.GetResult<float>();

            // Sanity-check direction. The favoured native is usually correct but the
            // lookahead point can momentarily snap to a sliplane or off-ramp; if the
            // node heading differs from ours by more than 90°, refuse to follow it.
            float vehHeading = playerVeh.Heading;
            float headingDiff = nodeHeading - vehHeading;
            while (headingDiff > 180f) headingDiff -= 360f;
            while (headingDiff < -180f) headingDiff += 360f;
            if (Math.Abs(headingDiff) > 90f)
            {
                isOnValidRoad = false;
                roadHeadingDelta = 0f;
                ppGoalInitialized = false;
                lastRoadCorrection *= 0.5f;
                return lastRoadCorrection;
            }

            roadHeadingDelta = headingDiff;
            isOnValidRoad = true;

            // ---- LANE COUNT + ROAD CLASS (via supplementary natives) ----
            // FAVOUR_DIRECTION doesn't return totalLanes; WITH_HEADING does. Calling
            // it at the same coords with n=1 gives us the lane count for free.
            int totalLanes = 2;
            OutputArgument outLaneCheckPos = new OutputArgument();
            OutputArgument outLaneCheckHeading = new OutputArgument();
            OutputArgument outLaneCheckLanes = new OutputArgument();
            bool gotLanes = Function.Call<bool>(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                nodePos.X, nodePos.Y, nodePos.Z,
                1, outLaneCheckPos, outLaneCheckHeading, outLaneCheckLanes,
                1, 3.0f, 0f);
            if (gotLanes) totalLanes = Math.Max(1, outLaneCheckLanes.GetResult<int>());

            // GET_VEHICLE_NODE_PROPERTIES = 0x0568566ACBB5DEDC
            // Bit 64 = HIGHWAY. We use this in preference to MapDb's polygon lookup
            // because MapDb depends on a JSON file the player may not have synced.
            OutputArgument outDensity = new OutputArgument();
            OutputArgument outFlags = new OutputArgument();
            bool gotProps = Function.Call<bool>((Hash)0x0568566ACBB5DEDC,
                nodePos.X, nodePos.Y, nodePos.Z, outDensity, outFlags);
            bool isHighway;
            if (gotProps)
            {
                int flags = outFlags.GetResult<int>();
                isHighway = (flags & 64) != 0;
            }
            else
            {
                string rt = MapDb.GetRoadTypeAt(playerPos);
                isHighway = (rt == "freeway" || rt == "highway");
            }

            // ---- LANE-CENTER GOAL POINT ----
            GTA.Math.Vector3 goal = LaneCenterFromNode(nodePos, nodeHeading,
                totalLanes, isHighway, playerPos);

            // ---- 1st-ORDER LPF ON GOAL ----
            // τ = 250 ms. The goal can jump by ~lane width when a new node is the
            // closest favoured one; the LPF smooths these steps out.
            if (!ppGoalInitialized)
            {
                ppGoalSmoothed = goal;
                ppGoalInitialized = true;
            }
            else
            {
                float a = Math.Max(0.01f, Math.Min(1f, deltaTime / GOAL_LPF_TAU));
                ppGoalSmoothed += (goal - ppGoalSmoothed) * a;
            }

            // ---- PURE PURSUIT STEERING ----
            // Project goal into vehicle frame. fwd>0 = ahead, right>0 = to player's right.
            GTA.Math.Vector3 toGoal = ppGoalSmoothed - playerPos;
            float fwdC = GTA.Math.Vector3.Dot(forwardVec, toGoal);
            float rightC = GTA.Math.Vector3.Dot(rightVec, toGoal);
            if (fwdC < 0.5f)
            {
                // Goal is essentially abreast / behind us — can't pure-pursuit it
                // safely; just hold the last correction with mild decay.
                lastRoadCorrection *= 0.85f;
                return lastRoadCorrection;
            }

            double alpha = Math.Atan2(rightC, fwdC);
            // δ = atan2(2·L_wb·sin α, L_d)
            double delta = Math.Atan2(
                2.0 * PURE_PURSUIT_WHEELBASE * Math.Sin(alpha), Ld);
            float steerInput = (float)(delta / PURE_PURSUIT_DELTA_MAX);
            if (steerInput > 1f) steerInput = 1f;
            else if (steerInput < -1f) steerInput = -1f;

            // sin α is its own soft deadband; no explicit angle/lateral deadband needed.
            lastRoadCorrection = steerInput;
            return steerInput;
        }

        /// <summary>
        /// Gets road guidance by finding the best path nodes ahead of the vehicle.
        /// Returns a steering correction value (-1 to 1) to follow the road.
        /// Also sets isOnValidRoad and roadHeadingDelta fields.
        /// Filters out nodes for opposite-direction lanes to prevent U-turn behavior.
        /// </summary>
        private float GetRoadGuidance(Vehicle playerVeh)
        {
            GTA.Math.Vector3 playerPos = playerVeh.Position;
            GTA.Math.Vector3 forwardVec = playerVeh.ForwardVector;
            float vehicleSpeed = playerVeh.Speed;
            float vehicleHeading = playerVeh.Heading;

            // Look ahead based on speed (minimum 10m, up to 35m - reduced to avoid cross-road confusion)
            float lookAhead = Math.Max(10f, Math.Min(35f, vehicleSpeed * 1.5f));

            // Position to check: ahead of the vehicle
            GTA.Math.Vector3 checkPos = playerPos + (forwardVec * lookAhead);

            // Try up to 3 closest nodes to find one going our direction
            for (int nodeIndex = 1; nodeIndex <= 3; nodeIndex++)
            {
                OutputArgument outPos = new OutputArgument();
                OutputArgument outHeading = new OutputArgument();
                OutputArgument outLanes = new OutputArgument();

                // GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING
                bool foundNode = Function.Call<bool>(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                    checkPos.X, checkPos.Y, checkPos.Z,
                    nodeIndex,
                    outPos,
                    outHeading,
                    outLanes,
                    1,              // nodeFlags - include switched off nodes
                    3.0f,           // zMeasureMult
                    0f);            // zTolerance

                if (!foundNode)
                {
                    continue;
                }

                GTA.Math.Vector3 nodePos = outPos.GetResult<GTA.Math.Vector3>();
                float nodeHeading = outHeading.GetResult<float>();

                // Check if the node is reasonably close (not on a completely different road)
                float distToNode = World.GetDistance(playerPos, nodePos);
                if (distToNode > lookAhead * 2f)
                {
                    continue;
                }

                // Calculate the heading difference
                float headingDiff = nodeHeading - vehicleHeading;
                while (headingDiff > 180f) headingDiff -= 360f;
                while (headingDiff < -180f) headingDiff += 360f;

                // FILTER: Skip nodes that are not closely aligned (>25° difference)
                // Tight filter prevents steering toward opposite-direction lanes on divided highways
                if (Math.Abs(headingDiff) > 25f)
                {
                    continue;
                }

                // Found a valid same-direction node
                isOnValidRoad = true;
                roadHeadingDelta = headingDiff;

                // Also consider lateral offset from the road center
                GTA.Math.Vector3 toNode = GTA.Math.Vector3.Normalize(nodePos - playerPos);
                float dotRight = GTA.Math.Vector3.Dot(playerVeh.RightVector, toNode);

                // Heading-dominant correction — lateral is very weak to avoid pulling toward median
                // on divided highways where road-center nodes sit between opposing lanes
                float headingCorrection = headingDiff / 90f;
                float lateralCorrection = dotRight * 0.1f;    // Very weak: prevents median-pull

                // Clamp total correction
                float totalCorrection = Math.Max(-1f, Math.Min(1f, headingCorrection + lateralCorrection));
                return totalCorrection;
            }

            // No valid same-direction node found
            isOnValidRoad = false;
            roadHeadingDelta = 0f;
            return 0f;
        }

        /// <summary>
        /// Updates the cached waypoint direction for waypoint-aware drive assist.
        /// Gets the target position from either waypoint or mission blip.
        /// Calculates the heading direction TO the target for road node preference.
        /// </summary>
        private void UpdateWaypointDirection(GTA.Math.Vector3 playerPos)
        {
            GTA.Math.Vector3 targetPos = GTA.Math.Vector3.Zero;

            // Check for active waypoint first
            if (Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE))
            {
                int wpHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8); // 8 = waypoint blip
                if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, wpHandle))
                {
                    targetPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, wpHandle);
                }
            }
            // Fall back to mission blip if no waypoint
            else if (missionTrackingActive && trackedBlipHandle != -1)
            {
                if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, trackedBlipHandle))
                {
                    targetPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, trackedBlipHandle);
                }
            }

            if (targetPos != GTA.Math.Vector3.Zero)
            {
                cachedWaypointPos = targetPos;
                hasActiveWaypoint = true;

                // Calculate heading TO the waypoint (in degrees, matching GTA's heading system)
                // GTA V heading: 0=North, increases CLOCKWISE (90=East, 180=South, 270=West)
                float dx = targetPos.X - playerPos.X;
                float dy = targetPos.Y - playerPos.Y;

                // atan2(-dx, dy) gives angle from North, increasing clockwise
                // This matches GTA's vehicle heading system
                float angleRad = (float)Math.Atan2(-dx, dy);
                float angleDeg = angleRad * (180f / (float)Math.PI);

                // Normalize to 0-360
                if (angleDeg < 0) angleDeg += 360f;

                // GTA headings are actually counter-clockwise from what we calculated
                // So we need to flip it: heading = 360 - angle (or just negate and normalize)
                cachedWaypointHeading = (360f - angleDeg) % 360f;
            }
            else
            {
                hasActiveWaypoint = false;
            }
        }

        /// <summary>
        /// Checks if a road node heading is favorable for reaching the waypoint.
        /// Returns a score: higher = better alignment with waypoint direction.
        /// Used to prefer road nodes that lead toward the destination.
        /// </summary>
        private float GetWaypointAlignmentScore(float nodeHeading, float vehicleHeading)
        {
            if (!hasActiveWaypoint) return 0f;

            // Calculate how well the node heading aligns with the waypoint direction
            float headingToWaypoint = cachedWaypointHeading;

            float nodeDiff = nodeHeading - headingToWaypoint;
            while (nodeDiff > 180f) nodeDiff -= 360f;
            while (nodeDiff < -180f) nodeDiff += 360f;

            // Score based on alignment (1.0 = perfect alignment, 0 = perpendicular, -1 = opposite)
            // Use cosine-like scoring
            float alignmentScore = (float)Math.Cos(nodeDiff * Math.PI / 180f);

            return alignmentScore;
        }

        /// <summary>
        /// Gets multiple road nodes ahead to understand the road curve better.
        /// Returns the average steering direction needed.
        /// Filters out nodes for opposite-direction lanes to prevent U-turn behavior on multi-lane roads.
        /// When waypoint-aware drive assist is enabled, prefers nodes heading toward the waypoint.
        /// </summary>
        private float GetRoadCurveGuidance(Vehicle playerVeh)
        {
            GTA.Math.Vector3 playerPos = playerVeh.Position;
            GTA.Math.Vector3 forwardVec = playerVeh.ForwardVector;
            float vehicleSpeed = playerVeh.Speed;
            float vehicleHeading = playerVeh.Heading;

            float totalCorrection = 0f;
            int validNodes = 0;

            // Check if waypoint-aware mode is active
            bool useWaypointGuidance = hasActiveWaypoint && getSetting("waypointDriveAssist") == 1;

            // Speed-adaptive look-ahead distances (NPC AI's fLookAheadDist and fAheadSpeedFollowDist)
            // At low speed: short distances with near-point emphasis
            // At high speed: longer distances capped at 40m to avoid cross-road confusion
            float speedLookAhead = Math.Max(LOOK_AHEAD_BASE, Math.Min(40f, vehicleSpeed * 1.5f));
            float[] distances = {
                speedLookAhead * 0.3f,   // Near point
                speedLookAhead * 0.6f,   // Medium point (reduced from 0.7)
                speedLookAhead * 0.9f    // Far point (reduced from 1.2 - stay closer)
            };
            // Shift weights toward far points at higher speeds for better curve anticipation
            float farWeight = Math.Min(0.4f, vehicleSpeed / 80f);
            float[] weights = { 0.5f - farWeight * 0.3f, 0.3f, 0.2f + farWeight * 0.3f };

            for (int i = 0; i < distances.Length; i++)
            {
                float lookAhead = Math.Max(distances[i], Math.Min(45f, vehicleSpeed * (i + 1) * 0.5f));
                GTA.Math.Vector3 checkPos = playerPos + (forwardVec * lookAhead);

                // Collect valid candidate nodes (up to 5)
                List<Tuple<GTA.Math.Vector3, float, float>> candidateNodes = new List<Tuple<GTA.Math.Vector3, float, float>>();

                // Try up to 5 closest nodes to find candidates
                for (int nodeIndex = 1; nodeIndex <= 5; nodeIndex++)
                {
                    OutputArgument outPos = new OutputArgument();
                    OutputArgument outHeading = new OutputArgument();
                    OutputArgument outLanes = new OutputArgument();

                    bool foundNode = Function.Call<bool>(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                        checkPos.X, checkPos.Y, checkPos.Z, nodeIndex, outPos, outHeading, outLanes, 1, 3.0f, 0f);

                    if (foundNode)
                    {
                        GTA.Math.Vector3 nodePos = outPos.GetResult<GTA.Math.Vector3>();
                        float nodeHeading = outHeading.GetResult<float>();

                        float headingDiff = nodeHeading - vehicleHeading;
                        while (headingDiff > 180f) headingDiff -= 360f;
                        while (headingDiff < -180f) headingDiff += 360f;

                        // FILTER: Skip nodes that are not closely aligned (>25° difference)
                        // Tight filter prevents steering toward opposite-direction lanes on divided highways
                        if (Math.Abs(headingDiff) > 25f)
                        {
                            continue;
                        }

                        // This node passes the heading filter, add as candidate
                        candidateNodes.Add(new Tuple<GTA.Math.Vector3, float, float>(nodePos, nodeHeading, headingDiff));
                    }
                }

                // Select best node from candidates
                if (candidateNodes.Count > 0)
                {
                    GTA.Math.Vector3 bestNodePos;
                    float bestHeadingDiff;

                    if (useWaypointGuidance && candidateNodes.Count > 1)
                    {
                        // WAYPOINT-AWARE: Score each candidate by alignment with waypoint direction
                        float bestScore = float.MinValue;
                        int bestIndex = 0;

                        for (int j = 0; j < candidateNodes.Count; j++)
                        {
                            float waypointScore = GetWaypointAlignmentScore(candidateNodes[j].Item2, vehicleHeading);

                            // Also factor in how close the heading is to current vehicle heading (prefer smoother turns)
                            float smoothnessBonus = 1f - (Math.Abs(candidateNodes[j].Item3) / 45f) * 0.3f;

                            float totalScore = waypointScore + smoothnessBonus;

                            if (totalScore > bestScore)
                            {
                                bestScore = totalScore;
                                bestIndex = j;
                            }
                        }

                        bestNodePos = candidateNodes[bestIndex].Item1;
                        bestHeadingDiff = candidateNodes[bestIndex].Item3;
                    }
                    else
                    {
                        // Standard mode: use first valid node (closest)
                        bestNodePos = candidateNodes[0].Item1;
                        bestHeadingDiff = candidateNodes[0].Item3;
                    }

                    // Heading-dominant correction — lateral is very weak to avoid median-pull
                    GTA.Math.Vector3 toNode = GTA.Math.Vector3.Normalize(bestNodePos - playerPos);
                    float dotRight = GTA.Math.Vector3.Dot(playerVeh.RightVector, toNode);

                    float correction = (bestHeadingDiff / 90f) + (dotRight * 0.08f); // Very weak lateral
                    correction = Math.Max(-1f, Math.Min(1f, correction));

                    totalCorrection += correction * weights[i];
                    validNodes++;
                }
            }

            if (validNodes == 0)
            {
                isOnValidRoad = false;
                return 0f;
            }

            // NODE EXTRAPOLATION: Project the path beyond the farthest node for smoother curves
            // This is the NPC AI's fNodeExtrapolationDist concept - anticipate the road direction
            if (validNodes >= 2)
            {
                float extrapolationCorrection = totalCorrection / validNodes;
                totalCorrection += extrapolationCorrection * 0.15f; // Small boost for projected direction
            }

            isOnValidRoad = true;
            return totalCorrection;
        }

        /// <summary>
        /// Finds the nearest road node going the same direction as the vehicle.
        /// Returns the node position, heading, and distance. Returns false if no valid node found.
        /// </summary>
        private bool FindNearestSameDirectionRoadNode(Vehicle playerVeh, out GTA.Math.Vector3 nodePos, out float nodeHeading, out float distance)
        {
            nodePos = GTA.Math.Vector3.Zero;
            nodeHeading = 0f;
            distance = 999f;

            GTA.Math.Vector3 playerPos = playerVeh.Position;
            float vehicleHeading = playerVeh.Heading;

            // Search in expanding radius for a valid node
            for (int nodeIndex = 1; nodeIndex <= 10; nodeIndex++)
            {
                OutputArgument outPos = new OutputArgument();
                OutputArgument outHeading = new OutputArgument();
                OutputArgument outLanes = new OutputArgument();

                bool foundNode = Function.Call<bool>(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                    playerPos.X, playerPos.Y, playerPos.Z, nodeIndex, outPos, outHeading, outLanes, 1, 3.0f, 0f);

                if (foundNode)
                {
                    GTA.Math.Vector3 testPos = outPos.GetResult<GTA.Math.Vector3>();
                    float testHeading = outHeading.GetResult<float>();

                    float headingDiff = testHeading - vehicleHeading;
                    while (headingDiff > 180f) headingDiff -= 360f;
                    while (headingDiff < -180f) headingDiff += 360f;

                    // Only accept nodes closely aligned with our direction (within 45 degrees)
                    if (Math.Abs(headingDiff) <= 45f)
                    {
                        nodePos = testPos;
                        nodeHeading = testHeading;
                        distance = World.GetDistance(playerPos, testPos);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Teleports the vehicle to the nearest valid road node (same direction).
        /// Adds slight Z offset to prevent clipping through ground.
        /// </summary>
        private bool TeleportToNearestRoad(Vehicle playerVeh)
        {
            GTA.Math.Vector3 nodePos;
            float nodeHeading;
            float distance;

            if (FindNearestSameDirectionRoadNode(playerVeh, out nodePos, out nodeHeading, out distance))
            {
                // Add slight Z offset to prevent clipping (0.5m above road)
                GTA.Math.Vector3 teleportPos = new GTA.Math.Vector3(nodePos.X, nodePos.Y, nodePos.Z + 0.5f);

                // Teleport vehicle
                playerVeh.Position = teleportPos;
                playerVeh.Heading = nodeHeading;

                // Preserve some forward momentum but reduce speed
                float currentSpeed = playerVeh.Speed;
                float newSpeed = Math.Max(5f, currentSpeed * 0.5f); // At least 5 m/s, or half current speed
                playerVeh.Velocity = playerVeh.ForwardVector * newSpeed;

                // Reset tracking
                wasCloseToRoad = true;
                offRoadStartTicks = 0;
                lastValidRoadDistance = 0f;

                Tolk.Speak("Teleported to road", true);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Finds the best road node to recover toward — no strict heading filter.
        /// Used for wrong-way spawn and off-road drift. Scores candidates by
        /// distance + alignment penalty (1 m ≈ 6° of heading penalty) so a near
        /// well-aligned node beats a far perfectly-aligned one, but a far well-
        /// aligned node beats a near opposite-direction one. Always returns
        /// something if any node is streamed in.
        /// </summary>
        private bool FindBestRecoveryNode(Vehicle playerVeh,
            out GTA.Math.Vector3 nodePos, out float nodeHeading,
            out float distance, out float headingDelta)
        {
            nodePos = GTA.Math.Vector3.Zero;
            nodeHeading = 0f;
            distance = 999f;
            headingDelta = 0f;

            GTA.Math.Vector3 playerPos = playerVeh.Position;
            float vehicleHeading = playerVeh.Heading;
            GTA.Math.Vector3 forwardVec = playerVeh.ForwardVector;

            // PRIMARY: static node graph. The 67k-node dump has every node
            // regardless of streaming, so true off-road / fresh-spawn cases
            // always find something. The native version (below) sometimes
            // returns nothing in those cases because the surrounding region
            // hasn't streamed in yet.
            if (NodeGraph.IsLoaded)
            {
                int nearIdx = NodeGraph.FindNearestNode(playerPos, STATIC_NODE_SEARCH_RADIUS);
                if (nearIdx >= 0)
                {
                    GTA.Math.Vector3 p = NodeGraph.GetPosition(nearIdx);
                    // Derive a heading from the highest-forward-lane outgoing
                    // neighbour — that's the direction the AI would travel
                    // through this node.
                    float derivedHeading = vehicleHeading;
                    int lc = NodeGraph.GetLinkCount(nearIdx);
                    int bestFwdLanes = -1;
                    for (int j = 0; j < lc; j++)
                    {
                        int tgt; int fwd; int bwd;
                        NodeGraph.GetLink(nearIdx, j, out tgt, out fwd, out bwd);
                        if (fwd <= 0) continue; // one-way against this direction
                        if (fwd <= bestFwdLanes) continue;
                        GTA.Math.Vector3 tp = NodeGraph.GetPosition(tgt);
                        float dxn = tp.X - p.X, dyn = tp.Y - p.Y;
                        if (dxn * dxn + dyn * dyn < 0.01f) continue;
                        // GTA heading convention: 0 = north (+Y), increases
                        // counter-clockwise. atan2(-x, y) maps a world delta
                        // to that convention.
                        derivedHeading = (float)(Math.Atan2(-dxn, dyn) * 57.29578);
                        if (derivedHeading < 0) derivedHeading += 360f;
                        bestFwdLanes = fwd;
                    }
                    float d = World.GetDistance(playerPos, p);
                    float hd = derivedHeading - vehicleHeading;
                    while (hd > 180f) hd -= 360f;
                    while (hd < -180f) hd += 360f;
                    // Z-GUARD: don't recover onto a stacked road deck. If the
                    // nearest static node is far in Z, fall through to the
                    // native scan (scored with a Z penalty) instead.
                    if (Math.Abs(p.Z - playerPos.Z) <= PATH_NODE_MAX_Z_DELTA * 2f)
                    {
                        nodePos = p;
                        nodeHeading = derivedHeading;
                        distance = d;
                        headingDelta = hd;
                        return true;
                    }
                }
            }

            // FALLBACK: native scan. Omnidirectional — no 20 m forward bias
            // since that biased recovery away from spawns that point the
            // wrong way. Scoring still penalises heading mismatch.
            float bestScore = float.MaxValue;
            bool found = false;
            for (int n = 1; n <= ALIGN_SCAN_NODE_COUNT; n++)
            {
                OutputArgument outP = new OutputArgument();
                OutputArgument outH = new OutputArgument();
                bool ok = Function.Call<bool>((Hash)0x45905BE8654AE067,
                    playerPos.X, playerPos.Y, playerPos.Z,
                    playerPos.X, playerPos.Y, playerPos.Z,
                    n, outP, outH, 1, 3.0f, 0f);
                if (!ok) continue;

                GTA.Math.Vector3 p = outP.GetResult<GTA.Math.Vector3>();
                float h = outH.GetResult<float>();
                float d = World.GetDistance(playerPos, p);
                float hd = h - vehicleHeading;
                while (hd > 180f) hd -= 360f;
                while (hd < -180f) hd += 360f;

                // Z penalty strongly de-prioritises stacked-deck nodes without
                // hard-rejecting them (recovery must always find SOMETHING).
                // iter-13 Patch U: the old /6 heading weight let a 51 m node
                // pointing ~180 deg the wrong way win on distance alone (the
                // airport-freeway failure: car aimed NE, lane wanted SE, and
                // recovery still chased the far/flipped target). Weight heading
                // at full strength, add a hard de-prioritisation for near-180
                // flips, and penalise far targets — all soft (never reject) so a
                // recovery node is always found.
                float headPenalty = Math.Abs(hd);
                float flipPenalty = Math.Abs(hd) > 120f ? 200f : 0f;
                float farPenalty  = d > 30f ? (d - 30f) * 3f : 0f;
                float score = d + headPenalty + flipPenalty + farPenalty
                            + Math.Abs(p.Z - playerPos.Z) * 2f;
                if (score < bestScore)
                {
                    bestScore = score;
                    nodePos = p;
                    nodeHeading = h;
                    distance = d;
                    headingDelta = hd;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// Produces strong steering toward the recovery target. Three cases:
        ///   1. Off the node (distance > RECOVERY_DISTANCE_ENGAGE): pursuit toward
        ///      the node POSITION. If the node is behind us at low speed, engage
        ///      reverse.
        ///   2. At the node but skewed: pure heading correction toward node heading.
        ///   3. Faced wrong way at low speed: engage reverse + counter-steer.
        /// Also outputs a braking request so the car slows enough that a tight
        /// realignment turn is geometrically feasible instead of orbiting.
        /// </summary>
        private void GetAlignmentRecoverySteer(Vehicle playerVeh,
            GTA.Math.Vector3 nodePos, float nodeHeading,
            float distance, float headingDelta,
            out float steerOutput, out bool engageReverse, out float brakeOutput)
        {
            steerOutput = 0f;
            engageReverse = false;
            brakeOutput = 0f;

            float speed = playerVeh.Speed;
            GTA.Math.Vector3 toNode = nodePos - playerVeh.Position;
            float fwdC = GTA.Math.Vector3.Dot(playerVeh.ForwardVector, toNode);
            float rightC = GTA.Math.Vector3.Dot(playerVeh.RightVector, toNode);

            // Speed-based saturation: full authority below ALIGN_LOW_SPEED_SATURATE,
            // decays linearly to 0 at +15 m/s. You don't U-turn at highway speed.
            float lowSpeedKick = 1f - Math.Min(1f, Math.Max(0f,
                (speed - ALIGN_LOW_SPEED_SATURATE) / 15f));

            if (distance > RECOVERY_DISTANCE_ENGAGE)
            {
                // OFF-ROAD: pursue the node position.
                if (fwdC < 1f && speed < REVERSE_UTURN_SPEED)
                {
                    // Node is behind/abreast and we're stopped; reverse out.
                    // Sign inverts because reversing flips perceived steering.
                    engageReverse = true;
                    steerOutput = -Math.Sign(rightC);
                }
                else
                {
                    float alpha = (float)Math.Atan2(rightC, Math.Max(0.5f, fwdC));
                    steerOutput = Math.Max(-1f, Math.Min(1f, alpha / 0.9f));
                    steerOutput *= Math.Max(0.4f, lowSpeedKick + 0.4f);
                }
            }
            else
            {
                // ON ROAD but SKEWED: pure heading correction.
                // U-TURN DIRECTION LATCH: near +/-180 deg the sign of
                // headingDelta flips frame-to-frame as the angle wraps, so the
                // car wiggles instead of committing. Latch one direction for
                // the whole U-turn; release once roughly aligned (hysteresis).
                if (Math.Abs(headingDelta) > 150f)
                {
                    if (recoveryUturnDir == 0)
                        recoveryUturnDir = headingDelta >= 0f ? 1 : -1;
                }
                else if (Math.Abs(headingDelta) < 120f)
                {
                    recoveryUturnDir = 0;
                }
                int turnSign = recoveryUturnDir != 0
                    ? recoveryUturnDir
                    : (headingDelta >= 0f ? 1 : -1);

                if (Math.Abs(headingDelta) > REVERSE_UTURN_ANGLE && speed < REVERSE_UTURN_SPEED)
                {
                    // Facing wrong way, slow — reverse + counter-steer.
                    // Sign inverts when reversing: to rotate the nose right we
                    // input left while going backward.
                    engageReverse = true;
                    steerOutput = -turnSign;
                }
                else
                {
                    // Saturates at 30° delta — anything sharper steers at max.
                    float magnitude = Math.Min(1f, Math.Abs(headingDelta) / 30f);
                    steerOutput = turnSign * magnitude * Math.Max(0.6f, lowSpeedKick + 0.5f);
                }
            }

            // RECOVERY BRAKING: a large heading error cannot be turned out at
            // speed — the car just orbits the target (the stuck loop in the
            // debug log). Brake hard — proportional to heading error — to bring
            // the car down to U-turn speed. No lower speed floor: braking must
            // stay engaged all the way down to REVERSE_UTURN_SPEED or a 3-4 m/s
            // dead-band forms. The reverse U-turn manages its own speed, so skip
            // braking once it is engaged.
            if (!engageReverse && speed > REVERSE_UTURN_SPEED)
            {
                float headErrMag = Math.Min(1f, Math.Abs(headingDelta) / 90f);
                if (headErrMag > 0.3f)
                    brakeOutput = Math.Min(1f, headErrMag * (speed / 8f));
            }

            // UNINTENDED REVERSE: the car is rolling backward but this is not a
            // commanded reverse U-turn. Request a brake — ApplyCachedSteeringInputs
            // routes it to forward throttle (control 71), arresting the backward
            // roll and restoring forward travel toward the recovery target.
            if (isReversing && !engageReverse)
                brakeOutput = Math.Max(brakeOutput, 0.7f);

            // REVERSE-MOTION SIGN: the steer above assumes forward travel. When
            // the car is actually rolling backward (and this is not a commanded
            // reverse U-turn), the same input rotates the nose the wrong way —
            // the controller would fight the car. Invert to match real motion.
            if (isReversing && !engageReverse)
                steerOutput = -steerOutput;

            steerOutput = Math.Max(-1f, Math.Min(1f, steerOutput));
        }

        /// <summary>
        /// Checks road distance and handles auto-teleport logic.
        /// Should be called from ProcessSteeringAssist when drive assist is active.
        /// </summary>
        private void CheckRoadTeleport(Vehicle playerVeh, bool isFullMode, float threatTTC)
        {
            // Only works if setting is enabled and drive assist is on
            if (getSetting("roadTeleport") != 1) return;

            // Gradual recovery is engaged and a reachable target exists — give
            // it the full ROAD_TELEPORT_DELAY_TICKS window before teleporting.
            if (currentDriveMode == DriveMode.RecoveringToRoad
                && hasRecoveryTarget && recoveryTargetDistance < 50f)
            {
                return;
            }

            long now = DateTime.Now.Ticks;

            // Enforce cooldown between teleports to prevent rapid-fire teleporting
            if ((now - lastTeleportTicks) < ROAD_TELEPORT_COOLDOWN_TICKS)
            {
                return;
            }

            GTA.Math.Vector3 nodePos;
            float nodeHeading;
            float currentRoadDistance;

            bool foundNode = FindNearestSameDirectionRoadNode(playerVeh, out nodePos, out nodeHeading, out currentRoadDistance);
            lastValidRoadDistance = foundNode ? currentRoadDistance : 999f;

            // Iter-12 Patch Q: surface-aware off-road override. The nav-node
            // distance above can be small even in pure desert because GTA V's
            // nav graph includes dirt-road nodes in Sandy Shores and other
            // off-road regions — the dirt node is physically on the sand, not
            // raised pavement, so its distance never exceeds roadFarThreshold.
            // driveassist-2026-05-27-182748 ran 10 minutes with extensive
            // off-road driving and ZERO offroad-timeout teleports as a
            // result.
            //
            // GET_STREET_NAME_AT_COORD returns streetHash=0 anywhere off a
            // named street (dirt or paved). Sustained streetHash=0 for
            // >OFF_NAMED_STREET_HYSTERESIS_MS forces currentRoadDistance to a
            // large value so the existing distance gates (conditions 2 and 3
            // below) fire correctly. Hysteresis avoids tripping in parking
            // lots and on momentary nav-mesh frames.
            bool onNamedStreet = IsOnNamedStreet(playerVeh.Position);
            if (onNamedStreet)
            {
                offNamedStreetSinceTicks = 0;
                offNamedStreetLogged = false;
            }
            else if (offNamedStreetSinceTicks == 0)
            {
                offNamedStreetSinceTicks = now;
            }
            bool sustainedOffNamedStreet = offNamedStreetSinceTicks > 0
                && (now - offNamedStreetSinceTicks) / 10000 > OFF_NAMED_STREET_HYSTERESIS_MS;
            if (sustainedOffNamedStreet)
            {
                // Force the downstream distance gates to see us as off-road.
                // 50 m is well above the largest roadFarThreshold (20 m at
                // max speed scale) so conditions 2 and 3 will trip cleanly.
                if (currentRoadDistance < 50f) currentRoadDistance = 50f;
                lastValidRoadDistance = 50f;
                if (!offNamedStreetLogged && driveLogger != null && driveLogger.IsRunning)
                {
                    driveLogger.Write("[F" + driveLogFrameCount
                        + "] EVENT off-named-street: sustainedMs="
                        + ((now - offNamedStreetSinceTicks) / 10000));
                    offNamedStreetLogged = true;
                }
            }

            // Scale thresholds based on vehicle speed - faster driving = more tolerance
            // At 15 m/s (~33 mph), add 50% to thresholds; at 30 m/s (~67 mph), add 100%
            float vehicleSpeed = playerVeh.Speed;
            float speedScale = 1f + Math.Min(vehicleSpeed / 30f, 1f); // 1.0 to 2.0 multiplier

            float roadCloseThreshold = ROAD_CLOSE_THRESHOLD_BASE * speedScale;
            float roadFarThreshold = ROAD_FAR_THRESHOLD_BASE * speedScale;

            // If no valid node found at all (heading filter rejected everything), don't teleport
            // This prevents teleporting on sharp curves where heading differs significantly
            if (!foundNode)
            {
                // Only teleport if we ALSO can't find ANY node (even with relaxed heading)
                // Try finding any node within 90 degrees as a sanity check
                bool anyNodeNearby = false;
                for (int i = 1; i <= 5; i++)
                {
                    OutputArgument outPos = new OutputArgument();
                    OutputArgument outHeading = new OutputArgument();
                    OutputArgument outLanes = new OutputArgument();

                    if (Function.Call<bool>(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                        playerVeh.Position.X, playerVeh.Position.Y, playerVeh.Position.Z,
                        i, outPos, outHeading, outLanes, 1, 3.0f, 0f))
                    {
                        float dist = World.GetDistance(playerVeh.Position, outPos.GetResult<GTA.Math.Vector3>());
                        if (dist < roadCloseThreshold * 1.5f)
                        {
                            anyNodeNearby = true;
                            break;
                        }
                    }
                }

                // If there's any road node nearby, we're probably on a curve - don't teleport.
                // Iter-10 Patch C: don't wipe the off-road accumulator on
                // every flicker into the close-band; require sustained
                // proximity. See OFFROAD_RESET_HYSTERESIS_MS.
                if (anyNodeNearby)
                {
                    if (closeToRoadConfirmTicks == 0)
                        closeToRoadConfirmTicks = now;
                    else if ((now - closeToRoadConfirmTicks) / 10000 > OFFROAD_RESET_HYSTERESIS_MS)
                    {
                        wasCloseToRoad = true;
                        offRoadStartTicks = 0;
                    }
                    return;
                }
                else
                {
                    // Genuinely not near any node — clear the confirm timer so
                    // a future flicker into the close-band has to re-accumulate.
                    closeToRoadConfirmTicks = 0;
                }
            }

            // Condition 1: Collision imminent AND far from road - teleport immediately
            // Only if threat is VERY imminent (< 0.3s) and we're genuinely far from road
            if (threatTTC < 0.3f && currentRoadDistance > roadFarThreshold * 1.5f)
            {
                if (driveLogger != null && driveLogger.IsRunning)
                    driveLogger.Write("[F" + driveLogFrameCount + "] EVENT teleport: reason=collision-imminent"
                        + " threatTTC=" + threatTTC.ToString("F2")
                        + " roadDist=" + currentRoadDistance.ToString("F1"));
                RecordDriveDecision("teleport: reason=collision-imminent roadDist="
                    + currentRoadDistance.ToString("F1"));
                if (TeleportToNearestRoad(playerVeh))
                {
                    lastTeleportTicks = now;
                    ResetForTeleport("collision-imminent");
                }
                return;
            }

            // Condition 2: Extremely far from any valid road node (scaled by speed)
            if (currentRoadDistance > roadCloseThreshold * 1.5f)
            {
                if (driveLogger != null && driveLogger.IsRunning)
                    driveLogger.Write("[F" + driveLogFrameCount + "] EVENT teleport: reason=far-from-road"
                        + " roadDist=" + currentRoadDistance.ToString("F1")
                        + " threshold=" + (roadCloseThreshold * 1.5f).ToString("F1"));
                RecordDriveDecision("teleport: reason=far-from-road roadDist="
                    + currentRoadDistance.ToString("F1"));
                if (TeleportToNearestRoad(playerVeh))
                {
                    lastTeleportTicks = now;
                    ResetForTeleport("far-from-road");
                }
                return;
            }

            // Condition 3: Moderately far from road for 5+ seconds
            if (currentRoadDistance > roadFarThreshold)
            {
                // Iter-10 Patch C: clear the close-to-road accumulator the
                // moment we're confirmed off-road — a future flicker back into
                // the close-band has to re-accumulate from zero before the
                // 5 s off-road timer resets.
                closeToRoadConfirmTicks = 0;
                if (wasCloseToRoad)
                {
                    // Just went off-road, start timer
                    offRoadStartTicks = now;
                    wasCloseToRoad = false;
                }
                else if (offRoadStartTicks > 0 && (now - offRoadStartTicks) > ROAD_TELEPORT_DELAY_TICKS)
                {
                    // Been off-road for 5+ seconds
                    if (driveLogger != null && driveLogger.IsRunning)
                        driveLogger.Write("[F" + driveLogFrameCount + "] EVENT teleport: reason=offroad-timeout"
                            + " roadDist=" + currentRoadDistance.ToString("F1")
                            + " offRoadMs=" + ((now - offRoadStartTicks) / 10000f).ToString("F0"));
                    RecordDriveDecision("teleport: reason=offroad-timeout roadDist="
                        + currentRoadDistance.ToString("F1"));
                    if (TeleportToNearestRoad(playerVeh))
                    {
                        lastTeleportTicks = now;
                        ResetForTeleport("offroad-timeout");
                    }
                    return;
                }
            }
            else
            {
                // Close to road - reset tracking only after sustained proximity
                // (iter-10 Patch C). A single frame of "close" no longer wipes
                // the 5 s off-road accumulator — a shoulder-clip during an
                // off-road drift used to spuriously reset and leave the car
                // stuck off-road for 30 s+ (driveassist-2026-05-25-231603 F5397).
                if (closeToRoadConfirmTicks == 0)
                    closeToRoadConfirmTicks = now;
                else if ((now - closeToRoadConfirmTicks) / 10000 > OFFROAD_RESET_HYSTERESIS_MS)
                {
                    wasCloseToRoad = true;
                    offRoadStartTicks = 0;
                }
            }
        }

        /// <summary>
        /// Four-layer stuck-recovery escalation. Runs from ProcessSteeringAssist
        /// (Full mode only). Detects a recovery/alignment mode that is steering
        /// the car but making no physical progress — the "wedged against an
        /// obstacle, ramming forever" failure — and escalates: upright the car,
        /// reverse it out, hand to auto-drive, finally teleport. Layers 3-4 run
        /// in MonitorStuckAutodrive because engaging auto-drive disables the
        /// assist (and therefore this method).
        /// </summary>
        private void UpdateStuckRecovery(Vehicle playerVeh, bool isFullMode)
        {
            // Autonomous get-unstuck maneuvers only make sense in Full mode,
            // where the assist already has full authority. In Assisted mode the
            // player still holds the wheel and can free the car themselves.
            // Layer 3+ is owned by MonitorStuckAutodrive once auto-drive is on.
            if (!isFullMode || playerVeh == null || stuckAutodriveEngaged)
                return;

            long now = DateTime.Now.Ticks;
            float speed = playerVeh.Speed;
            bool inRecovery = currentDriveMode != DriveMode.LaneKeeping;
            float skewMag = Math.Abs(roadHeadingDelta);

            // iter-13 Patch S: mode-agnostic no-progress arm. Independent of the
            // LaneKeeping<->Recovery oscillation that defeats the recovery-gated
            // detector below. Keys off physical reality: off-lane/skewed (or in
            // recovery) AND below the stall speed AND no real planar movement.
            // The anchor only resets on >3 m of actual travel, so flipping modes
            // can't restart the clock.
            bool offLaneOrSkewed = skewMag > NOPROGRESS_SKEW_ANGLE || inRecovery;
            bool noProgressArm = false;
            if (offLaneOrSkewed && speed < STUCK_SPEED_THRESHOLD)
            {
                if (noProgressSinceTicks == 0)
                {
                    noProgressSinceTicks = now;
                    noProgressAnchorPos = playerVeh.Position;
                }
                else if (PlanarDist(playerVeh.Position, noProgressAnchorPos) > NOPROGRESS_MOVE_M)
                {
                    noProgressSinceTicks = now;      // genuine progress — restart the clock
                    noProgressAnchorPos = playerVeh.Position;
                }
                else if (now - noProgressSinceTicks > NOPROGRESS_DETECT_TICKS)
                {
                    noProgressArm = true;
                }
            }
            else
            {
                noProgressSinceTicks = 0;
            }

            // Already escalating: exit once the car has physically moved clear of
            // the wedge OR rotated back into alignment. While a pivot is running
            // we must NOT exit just because the mode briefly flipped to
            // LaneKeeping (that flip is the trap itself) — stay engaged until the
            // skew is actually resolved or the car has moved.
            if (stuckLayer > 0)
            {
                bool movedClear = PlanarDist(playerVeh.Position, stuckPosition)
                                    > STUCK_FREED_DISTANCE;
                bool alignedNow = skewMag < PIVOT_DONE_ANGLE;
                bool stillStuck = inRecovery || noProgressArm || pivotEscapeActive;
                if (movedClear || alignedNow || !stillStuck)
                {
                    if (pivotEscapeActive && driveLogger != null && driveLogger.IsRunning)
                        driveLogger.Write("[F" + driveLogFrameCount + "] EVENT pivot-done:"
                            + " reason=" + (movedClear ? "movedClear" : alignedNow ? "aligned" : "modeExit")
                            + " finalSkew=" + roadHeadingDelta.ToString("F1")
                            + " elapsedMs=" + ((now - pivotStartTicks) / 10000));
                    Tolk.Speak("Vehicle freed.", true);
                    pivotEscapeActive = false;
                    ResetStuckRecovery();
                    return;
                }
            }
            else
            {
                // Not yet stuck — watch for a stall in a recovery mode OR the
                // mode-agnostic no-progress arm (Patch S). noProgressArm fires
                // on its own 3 s timer, so it can enter Layer 1 directly.
                if ((inRecovery && speed < STUCK_SPEED_THRESHOLD) || noProgressArm)
                {
                    if (stuckSinceTicks == 0)
                        stuckSinceTicks = now;
                    else if (now - stuckSinceTicks > STUCK_DETECT_TICKS || noProgressArm)
                    {
                        // Enter Layer 1.
                        stuckLayer = 1;
                        stuckPosition = playerVeh.Position;
                        stuckLayerSinceTicks = now;
                        if (driveLogger != null && driveLogger.IsRunning)
                            driveLogger.Write("[F" + driveLogFrameCount + "] EVENT stuck-arm:"
                                + " mode=" + currentDriveMode
                                + " skew=" + roadHeadingDelta.ToString("F1")
                                + " speed=" + speed.ToString("F2")
                                + " arming=" + (noProgressArm ? "noprogress" : "recovery")
                                + " noProgressMs=" + (noProgressSinceTicks > 0 ? (now - noProgressSinceTicks) / 10000 : 0));
                    }
                }
                else
                {
                    stuckSinceTicks = 0; // moving fine / not stalled
                }
                if (stuckLayer == 0) return;
            }

            // ---- LAYER 1: force the vehicle upright on all four wheels ----
            if (stuckLayer == 1)
            {
                if (!playerVeh.IsOnAllWheels)
                {
                    Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, playerVeh, 5.0f);
                    Tolk.Speak("Vehicle stuck. Righting vehicle.", true);
                }
                // iter-13 Patch T: when the car is skewed it needs to ROTATE, not
                // just back straight out (a straight reverse re-wedges at the same
                // angle — the Sandy Shores failure). Begin the pivot shuffle.
                if (skewMag > PIVOT_SKEW_ANGLE && speed < PIVOT_SPEED_GATE)
                {
                    pivotEscapeActive = true;
                    pivotPhaseForward = true;
                    pivotPhaseSinceTicks = now;
                    pivotStartTicks = now;
                    // Rotate the nose toward the target lane direction. Sign
                    // convention matches GetAlignmentRecoverySteer: a positive
                    // heading delta wants a positive (right) forward-steer input.
                    float pivotDelta = hasRecoveryTarget ? recoveryHeadingDelta : roadHeadingDelta;
                    pivotTurnSign = pivotDelta >= 0f ? 1 : -1;
                    Tolk.Speak("Drive assist stuck. Turning to align.", true);
                    if (driveLogger != null && driveLogger.IsRunning)
                        driveLogger.Write("[F" + driveLogFrameCount + "] EVENT pivot-begin:"
                            + " turnSign=" + pivotTurnSign
                            + " headingDelta=" + pivotDelta.ToString("F1"));
                }
                else
                {
                    Tolk.Speak("Drive assist stuck. Backing up.", true);
                    if (driveLogger != null && driveLogger.IsRunning)
                        driveLogger.Write("[F" + driveLogFrameCount + "] EVENT stuck-layer:"
                            + " layer=2 reason=reverse skew=" + roadHeadingDelta.ToString("F1"));
                }
                stuckLayer = 2;
                stuckLayerSinceTicks = now;
            }

            // ---- LAYER 2: pivot-in-place (skewed) OR straight reverse-out ----
            if (stuckLayer == 2)
            {
                if (pivotEscapeActive)
                {
                    // 3-point shuffle through the existing recovery machinery.
                    // Forward bite: full lock toward the target (the recovery
                    // forward-crawl in ApplyCachedSteeringInputs propels it).
                    // Reverse bite: alignmentEngageReverse backs the car up; the
                    // OPPOSITE steer sign keeps rotating the nose the same way
                    // (reversing inverts perceived steering). Alternate phases on
                    // a timer until aligned or the hard cap.
                    if (now - pivotPhaseSinceTicks > PIVOT_PHASE_TICKS)
                    {
                        pivotPhaseForward = !pivotPhaseForward;
                        pivotPhaseSinceTicks = now;
                        if (driveLogger != null && driveLogger.IsRunning)
                            driveLogger.Write("[F" + driveLogFrameCount + "] EVENT pivot-phase:"
                                + " phase=" + (pivotPhaseForward ? "forward" : "reverse")
                                + " skew=" + roadHeadingDelta.ToString("F1")
                                + " elapsedMs=" + ((now - pivotStartTicks) / 10000));
                    }

                    // Force a recovery mode so the downstream steer uses recovery
                    // authority + the forward-crawl runs (both gate on
                    // currentDriveMode != LaneKeeping). Does not re-stamp
                    // lastModeChangeTicks (already stamped earlier this pass).
                    currentDriveMode = DriveMode.AligningHeading;
                    isOnValidRoad = true;
                    alignmentEngageReverse = !pivotPhaseForward;
                    roadSteerCorrection = pivotPhaseForward ? pivotTurnSign : -pivotTurnSign;

                    if (now - pivotStartTicks > PIVOT_MAX_TICKS)
                    {
                        // Pivot couldn't align (boxed in on all sides) — hand off
                        // to the auto-drive wander as the last resort.
                        pivotEscapeActive = false;
                        if (driveLogger != null && driveLogger.IsRunning)
                            driveLogger.Write("[F" + driveLogFrameCount + "] EVENT pivot-done:"
                                + " reason=timeout finalSkew=" + roadHeadingDelta.ToString("F1")
                                + " elapsedMs=" + ((now - pivotStartTicks) / 10000));
                        EngageStuckAutodrive(playerVeh);
                    }
                }
                else
                {
                    // Straight reverse-out (low-skew wedge). Reuse the alignment
                    // reverse machinery; higher layers handle a failed reverse.
                    alignmentEngageReverse = true;
                    roadSteerCorrection = 0f;
                    isOnValidRoad = true;

                    if (now - stuckLayerSinceTicks > STUCK_LAYER2_TICKS)
                    {
                        if (driveLogger != null && driveLogger.IsRunning)
                            driveLogger.Write("[F" + driveLogFrameCount + "] EVENT stuck-layer:"
                                + " layer=3 reason=reverse-timeout");
                        EngageStuckAutodrive(playerVeh);
                    }
                }
            }
        }

        /// <summary>Planar (XY-only) distance — Z is ignored so a car climbing or
        /// dropping a slope while wedged still reads as "no progress".</summary>
        private static float PlanarDist(GTA.Math.Vector3 a, GTA.Math.Vector3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Layer 3 entry: engage the auto-drive wander task so the game
        /// AI drives the car out. Disables the assist (see the !isAutodriving
        /// gate in onTick); MonitorStuckAutodrive watches for recovery.</summary>
        private void EngageStuckAutodrive(Vehicle playerVeh)
        {
            Ped driver = (guardDriverActive && bodyguards.Count > 0
                            && bodyguards[0] != null && bodyguards[0].IsAlive)
                ? bodyguards[0] : Game.Player.Character;
            int drivingStyle = GetDrivingStyleFromFlags();
            Function.Call(Hash.SET_DRIVER_ABILITY, driver, 1.0f);
            Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, 0.5f);
            Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, driver, playerVeh,
                autodriveSpeed, drivingStyle);

            isAutodriving = true;
            autodriveWanderMode = true;
            autonavMode = "drive";
            autodriveCheckTicks = DateTime.Now.Ticks;

            alignmentEngageReverse = false;
            stuckLayer = 3;
            stuckAutodriveEngaged = true;
            stuckLayerSinceTicks = DateTime.Now.Ticks;
            // stuckPosition stays anchored so MonitorStuckAutodrive can measure
            // progress against where the car was wedged.
            Tolk.Speak("Drive assist still stuck. Auto-drive taking over.", true);
        }

        /// <summary>Layers 3-4: runs every tick from onTick while a stuck-recovery
        /// auto-drive is engaged (the assist itself is disabled in that state).
        /// Hands control back when the car is on a road again, or teleports if
        /// auto-drive cannot make progress.</summary>
        private void MonitorStuckAutodrive()
        {
            // Player cancelled auto-drive (or it ended) — abandon the recovery.
            if (!isAutodriving) { ResetStuckRecovery(); return; }

            // Player left the vehicle — end the recovery auto-drive cleanly.
            Ped ply = Game.Player.Character;
            if (ply == null || !ply.IsInVehicle()) { DisengageStuckAutodrive(null); return; }
            Vehicle veh = ply.CurrentVehicle;
            if (veh == null || !veh.Exists()) { DisengageStuckAutodrive(null); return; }

            long now = DateTime.Now.Ticks;
            float movedDist = World.GetDistance(veh.Position, stuckPosition);

            // SUCCESS: auto-drive carried the car clear of the wedge and back
            // onto a road node — hand control back (the player's chosen assist
            // mode resumes automatically once isAutodriving clears).
            if (movedDist > STUCK_FREED_DISTANCE)
            {
                GTA.Math.Vector3 np; float nh, nd;
                if (FindNearestSameDirectionRoadNode(veh, out np, out nh, out nd) && nd < 8f)
                {
                    DisengageStuckAutodrive("Auto-drive ending. You have control.");
                    return;
                }
            }

            // LAYER 4: auto-drive made no real progress — teleport to the road.
            if (now - stuckLayerSinceTicks > STUCK_LAYER3_TICKS
                && movedDist < STUCK_FREED_DISTANCE)
            {
                Tolk.Speak("Auto-drive stuck. Teleporting to nearest road.", true);
                TeleportToNearestRoad(veh);
                DisengageStuckAutodrive("You have control.");
            }
        }

        /// <summary>Ends the stuck-recovery auto-drive and returns control.</summary>
        private void DisengageStuckAutodrive(string announce)
        {
            isAutodriving = false;
            autodriveWanderMode = false;
            try { Game.Player.Character.Task.ClearAll(); } catch { }
            if (announce != null) Tolk.Speak(announce, true);
            ResetStuckRecovery();
        }

        /// <summary>Clears all stuck-recovery state so the machine can re-arm.</summary>
        private void ResetStuckRecovery()
        {
            stuckLayer = 0;
            stuckSinceTicks = 0;
            stuckLayerSinceTicks = 0;
            stuckAutodriveEngaged = false;
            stuckPosition = GTA.Math.Vector3.Zero;
            pivotEscapeActive = false;
            noProgressSinceTicks = 0;
        }

        /// <summary>
        /// Calculates and CACHES steering/braking values. Does NOT apply inputs directly.
        /// Inputs must be applied every tick via ApplyCachedSteeringInputs().
        /// </summary>
        /// <param name="steerTTC">Time to collision for steering (includes side threats)</param>
        /// <param name="brakeTTC">Time to collision for braking (ONLY ahead threats)</param>
        /// <param name="brakeDistance">Distance to the closest braking threat</param>
        /// <param name="hasSteerThreat">Whether there's a steering threat (any direction)</param>
        /// <param name="hasBrakeThreat">Whether there's a braking threat (ahead only)</param>
        /// <param name="needsHandbrakeTurn">Whether a handbrake turn should be applied</param>
        /// <param name="handbrakeSteerDir">Direction for handbrake turn (-1=left, 1=right)</param>
        private void ApplySteeringAssist(Vehicle playerVeh, float steerTTC, float brakeTTC, float brakeDistance, int avoidDir, bool isFullMode, float steerThreshold, float brakeThreshold, bool hasSteerThreat, bool hasBrakeThreat, bool needsHandbrakeTurn = false, float handbrakeSteerDir = 0f)
        {
            float smoothFactor = Math.Min(1.0f, STEER_SMOOTHING_RATE * deltaTime);
            float targetSteer = 0f;
            float brakeMag = 0f;
            float handbrakeMag = 0f;

            // STEERING: Responds to threats from ANY direction (uses steerTTC)
            if (hasSteerThreat)
            {
                // NPC AI-inspired lateral offset avoidance: compute proportional steering
                // based on actual clearance deficit rather than binary left/right
                float lateralAvoid = 0f;
                float obstDist = closestThreatPosition != GTA.Math.Vector3.Zero
                    ? World.GetDistance(playerVeh.Position, closestThreatPosition)
                    : 15f;
                if (closestThreatPosition != GTA.Math.Vector3.Zero)
                {
                    lateralAvoid = CalculateLateralAvoidance(playerVeh, closestThreatPosition,
                        threatType, obstDist);
                }

                // Fall back to binary direction if lateral avoidance returns zero
                // (e.g., obstacle is from raycast with no clear position)
                if (Math.Abs(lateralAvoid) < 0.05f && avoidDir != 0)
                {
                    lateralAvoid = avoidDir * 0.5f; // Reduced fallback magnitude
                }

                float steerUrgency = Math.Max(0f, 1f - (steerTTC / steerThreshold));
                float maxSteer = isFullMode ? 1.0f : 0.75f;
                // Linear urgency (not squared) for smoother response curve
                targetSteer = lateralAvoid * steerUrgency * maxSteer;

                // STATIC OBSTACLES (rails/walls): snap to target with no smoothing
                // lag — rails are stationary so the target is stable, and the user
                // reported side collisions because the smoothed avoid took too long
                // to ramp up. Moving threats keep the smoothing to avoid jitter.
                if (threatType == "obstacle")
                {
                    smoothedSteerCorrection = targetSteer;
                }
                else
                {
                    smoothedSteerCorrection += (targetSteer - smoothedSteerCorrection) * smoothFactor;
                }
            }
            else
            {
                // No steer threat - decay obstacle avoidance
                smoothedSteerCorrection *= (1f - smoothFactor);
            }

            // BRAKING: ONLY responds to threats AHEAD (uses brakeTTC, separate from steering)
            // This is the KEY fix - side obstacles no longer cause braking!
            if (hasBrakeThreat)
            {
                float brakeUrgency = Math.Max(0f, 1f - (brakeTTC / brakeThreshold));
                // Iter-11 Patch K: speed-blended brake magnitude. The
                // squared-urgency curve is the right comfort shape at city
                // speed (gentle taper, low-urgency threats barely register)
                // but it's catastrophic at highway speed — urgency=0.4 squared
                // is only 0.16 brake, exactly the F6786 (-143.6 health)
                // catastrophic-impact pattern. Blend squared (low speed) into
                // linear (highway) so urgency=0.4 becomes 0.4 brake at >=20 m/s.
                float pSpeed = playerVeh.Speed;
                float speedBlend = Math.Min(1f, Math.Max(0f, (pSpeed - 8f) / 12f));
                float squaredMag = brakeUrgency * brakeUrgency;
                float linearMag = brakeUrgency;
                brakeMag = squaredMag * (1f - speedBlend) + linearMag * speedBlend;
            }
            // No else needed - brakeMag stays at 0 if no brake threat

            // HANDBRAKE TURN ASSISTANCE (Full mode only)
            if (needsHandbrakeTurn && isFullMode)
            {
                // Apply handbrake with corresponding steering for a controlled drift turn
                handbrakeMag = 0.8f; // Strong but not full handbrake

                // Boost steering in the direction of the handbrake turn
                float handbrakeSteerBoost = handbrakeSteerDir * 1.0f; // Full steering in turn direction
                smoothedSteerCorrection = smoothedSteerCorrection * 0.3f + handbrakeSteerBoost * 0.7f;

                // Light braking during handbrake turn
                brakeMag = Math.Max(brakeMag, 0.3f);
            }
            else
            {
                // Decay handbrake when not needed
                handbrakeMag = cachedHandbrakeMagnitude * (1f - smoothFactor * 2f);
            }

            // ROAD FOLLOWING: Apply road / alignment guidance.
            // Alignment and recovery modes use higher authority and faster
            // smoothing so the car actually rotates / pursues the recovery node
            // instead of nudging gently like normal lane-keeping.
            if (isOnValidRoad)
            {
                float roadStrength;
                float roadSmoothFactor;
                if (currentDriveMode != DriveMode.LaneKeeping)
                {
                    roadStrength = isFullMode ? 1.0f : 0.75f;
                    roadSmoothFactor = Math.Min(1.0f, STEER_SMOOTHING_RATE * 1.5f * deltaTime);
                    // Dampen obstacle avoidance during alignment so it can't fight
                    // the alignment steer (e.g. swerving away from a nearby wall
                    // while we're trying to U-turn out of trouble) — UNLESS a
                    // static obstacle (rail/wall) is an active threat, in which
                    // case avoidance must stay full strength so the car does not
                    // brush the rail while recovering.
                    if (!(hasSteerThreat && threatType == "obstacle"))
                        smoothedSteerCorrection *= 0.4f;
                }
                else
                {
                    // Tighter lane discipline in Full mode (was 0.65). User reported
                    // gradual drift off the road over time; 0.9 keeps the car planted
                    // in lane on long freeway stretches. Assist mode raised from
                    // 0.35 -> 0.6: at 0.35 the lane-keep correction was too weak to
                    // counter a developing skew (see fix A in the debug analysis).
                    roadStrength = isFullMode ? 0.9f : 0.6f;
                    // Speed the smoothing up as the heading error grows so the
                    // correction tracks Stanley's demand instead of lagging ~0.4 s.
                    // Gentle 0.5x rate on straights; up to 1.5x once skewed >=30 deg.
                    float skewMag = Math.Min(1f, Math.Abs(roadHeadingDelta) / 30f);
                    roadSmoothFactor = Math.Min(1.0f,
                        STEER_SMOOTHING_RATE * (0.5f + skewMag) * deltaTime);
                }
                float targetRoadSteer = roadSteerCorrection * roadStrength;
                smoothedRoadCorrection += (targetRoadSteer - smoothedRoadCorrection) * roadSmoothFactor;
            }
            else
            {
                // No valid road - decay road correction
                smoothedRoadCorrection *= (1f - smoothFactor);
            }

            // NPC AI-inspired CORNERING DAMPING: when actively following a road curve,
            // reduce obstacle avoidance aggression to prevent fighting the road following
            // (from fCorneringBrakeMultiplier / fCorneringSteerMultiplier concepts)
            if (isOnValidRoad && Math.Abs(roadSteerCorrection) > 0.3f)
            {
                float corneringDamping = 1.0f - Math.Min(0.4f, Math.Abs(roadSteerCorrection) * 0.5f);
                smoothedSteerCorrection *= corneringDamping;
            }

            // COMBINE (v3): road following is the BASE; obstacle avoidance is a
            // bounded additive correction. Previously the combine logic blended the
            // two by tier — under heavy raycast input that surfaced the "rail
            // attraction" failure mode (the obstacle layer dominated, then steered
            // off the rail INTO the rail). Now path-node lane-center wins by default,
            // and only an imminent threat with no adjacent latch can override it.
            float threatUrgency = hasSteerThreat ? Math.Max(0f, Math.Min(1f, 1f - (steerTTC / steerThreshold))) : 0f;
            // RAIL LANE-CONFLICT: closest steer threat is a STATIC obstacle and
            // the avoid direction is opposite the lane-keep pull. This is the
            // "drifting into the rail while lane-keep pulls toward it" pattern.
            // When detected, lift the avoid cap so the swerve actually wins.
            bool railLaneConflict = hasSteerThreat
                && threatType == "obstacle"
                && Math.Sign(smoothedSteerCorrection) != 0
                && Math.Sign(smoothedRoadCorrection) != 0
                && Math.Sign(smoothedSteerCorrection) != Math.Sign(smoothedRoadCorrection);
            float combinedSteer;
            if (isOnValidRoad)
            {
                float baseSteer = smoothedRoadCorrection;
                // Cap the avoidance contribution so it can never fully reverse the
                // lane-keep — UNLESS a rail-lane conflict is active, in which case
                // let the swerve go to ±1.0 and halve the road pull.
                float avoidCap = railLaneConflict ? 1.0f : 0.6f;
                float avoid = Math.Max(-avoidCap, Math.Min(avoidCap, smoothedSteerCorrection));

                // Bypass the additive combine when the threat is imminent OR we
                // detected a rail-conflict — but still respect adjacent-vehicle
                // suppression so we don't swerve into another car.
                bool canFullyOverride = (threatUrgency > 0.85f || railLaneConflict)
                    && !(avoid > 0 && adjacentLatchedRight)
                    && !(avoid < 0 && adjacentLatchedLeft);
                if (canFullyOverride)
                {
                    combinedSteer = smoothedSteerCorrection;
                }
                else
                {
                    float roadWeight = railLaneConflict ? 0.5f : 1.0f;
                    combinedSteer = baseSteer * roadWeight + avoid;
                }
            }
            else
            {
                // No valid road — obstacle avoidance is all we have, but still
                // honor the adjacent-vehicle suppression.
                float avoid = smoothedSteerCorrection;
                if (avoid > 0 && adjacentLatchedRight) avoid = 0f;
                if (avoid < 0 && adjacentLatchedLeft) avoid = 0f;
                combinedSteer = hasSteerThreat ? avoid : avoid * 0.5f;
            }

            combinedSteer = Math.Max(-1f, Math.Min(1f, combinedSteer));

            // STEERING RATE LIMIT — history-aware slew to dampen the
            // "avoidance jolt" cluster from the failure audit.
            // Three rates (per second, deltaTime-scaled so frame-rate-independent):
            //   normal       — gentle, used when there is no urgent threat.
            //   emergency    — fast enough to reach ~0.8 in ~200 ms (avoids
            //                  the F11613 case where a slow clamp steered
            //                  INTO an obstacle) but no longer a single-frame
            //                  slam (audit C1: drivers felt the jolt and
            //                  marked it as a lane-keep failure).
            //   railconflict — keeps the prior single-frame priority for
            //                  rail-mounted vehicles where flip-around is
            //                  the only escape.
            // Fatigue damping: if history shows we've already been swinging
            // hard in the last ~200 ms, pull the slew down. Repeated big
            // swings ARE the oscillation pattern.
            const float STEER_SLEW_NORMAL_PER_SEC       = MAX_STEER_RATE * 3f;
            const float STEER_SLEW_EMERGENCY_PER_SEC    = 4.0f;
            const float STEER_SLEW_RAILCONFLICT_PER_SEC = 6.0f;

            bool emergencySwerve = threatUrgency > 0.85f;
            float slewPerSec =
                railLaneConflict ? STEER_SLEW_RAILCONFLICT_PER_SEC :
                emergencySwerve  ? STEER_SLEW_EMERGENCY_PER_SEC    :
                                   STEER_SLEW_NORMAL_PER_SEC;
            if (MaxAbsSteerCmdInLastMs(HISTORY_WINDOW_MS) > 0.6f)
                slewPerSec *= 0.6f;

            float maxSteerDelta = slewPerSec * deltaTime;
            float steerDelta = combinedSteer - previousFrameSteer;
            bool steerRateClamped = false;
            // Persistent-skew escalation: if we've been pinned >50 deg off-axis
            // for >2 s, comfort no longer matters. Let the saturated correction
            // execute in one frame so the car can actually un-skew.
            if (Math.Abs(steerDelta) > maxSteerDelta && !bypassSteerRateClampThisFrame)
            {
                combinedSteer = previousFrameSteer + Math.Sign(steerDelta) * maxSteerDelta;
                steerRateClamped = true;
            }
            previousFrameSteer = combinedSteer;

            // Emergency stop - ONLY for brake threats (ahead), not side threats
            // Also require the obstacle to be close enough that braking makes sense
            if (hasBrakeThreat && brakeTTC < 0.5f && brakeDistance < 15f)
            {
                brakeMag = 1.0f;
            }

            // Cache values for per-tick application - use combined steering
            cachedSteerCorrection = combinedSteer;
            cachedBrakeMagnitude = brakeMag;
            cachedAvoidDirection = avoidDir;
            cachedHandbrakeMagnitude = handbrakeMag;
            // Track if system is braking - used to block player throttle in full mode
            cachedIsBraking = (brakeMag > 0.1f || handbrakeMag > 0.1f);

            if (driveLogger != null && driveLogger.IsRunning)
                driveLogger.Write("[F" + driveLogFrameCount + "] EVENT apply-assist:"
                    + " steerTTC=" + FmtF(steerTTC) + " brakeTTC=" + FmtF(brakeTTC)
                    + " brakeDist=" + brakeDistance.ToString("F1")
                    + " hasSteerThreat=" + hasSteerThreat + " hasBrakeThreat=" + hasBrakeThreat
                    + " avoidDir=" + avoidDir + " railConflict=" + railLaneConflict
                    + " combinedSteer=" + combinedSteer.ToString("F3")
                    + " rateClamped=" + steerRateClamped
                    + " brakeMag=" + brakeMag.ToString("F3")
                    + " handbrakeMag=" + handbrakeMag.ToString("F3")
                    + " mode=" + currentDriveMode + " fullMode=" + isFullMode);

            // Audio/speech feedback (only for threats, not lane keeping)
            bool hasThreat = hasSteerThreat || hasBrakeThreat;
            if ((hasThreat || needsHandbrakeTurn) && DateTime.Now.Ticks - lastAssistAnnounceTicks > 20000000)
            {
                lastAssistAnnounceTicks = DateTime.Now.Ticks;

                float steerUrgency = Math.Max(0f, 1f - (steerTTC / steerThreshold));
                outSteerAssist.Stop();
                steerAssistBeep.Frequency = 400 + (int)(steerUrgency * 600);
                var sample = steerAssistBeep.Take(TimeSpan.FromSeconds(0.08));
                outSteerAssist.Init(sample);
                outSteerAssist.Play();

                if (handbrakeMag > 0.5f)
                {
                    string turnDir = handbrakeSteerDir > 0 ? "right" : "left";
                    Tolk.Speak("Handbrake turn " + turnDir, true);
                }
                else if (brakeMag > 0.5f || Math.Abs(smoothedSteerCorrection) > 0.3f)
                {
                    string action = brakeMag > 0.5f ? "Braking" : "Steering";
                    Tolk.Speak(action + ", " + threatType + " " + threatDirection, true);
                }
            }
        }

        /// <summary>
        /// Applies cached steering/braking inputs. MUST be called every tick for inputs to work!
        /// GTA V's SET_CONTROL_NORMAL only applies for a single frame.
        /// </summary>
        /// <summary>Adaptive cruise control. Runs once per detection scan; updates
        /// accThrottleOut / accBrakeOut which ApplyCachedSteeringInputs applies
        /// every tick. PID-controls headway behind the nav-assist lead vehicle.
        /// Comfort-level only — emergency braking stays with the threat path.</summary>
        private void ComputeACC(Vehicle veh, bool isFullMode)
        {
            bool haveLead = navAssistVehicleCenter != null && navAssistVehicleCenter.Exists();
            if (!isFullMode || getSetting("adaptiveCruise") != 1 || !haveLead)
            {
                accIntegral = 0f;
                accPrevError = 0f;
                accPrevTicks = 0;
                accThrottleOut = 0f;
                accBrakeOut = 0f;
                lastAccLeadHandle = 0;
                return;
            }

            long now = DateTime.Now.Ticks;
            int leadHandle = navAssistVehicleCenter.Handle;
            // New lead vehicle — reset PID state to avoid a throttle spike on handoff.
            if (leadHandle != lastAccLeadHandle)
            {
                accIntegral = 0f;
                accPrevError = 0f;
                accPrevTicks = 0;
                lastAccLeadHandle = leadHandle;
            }

            float speed = veh.Speed;
            float desiredGap = Math.Max(ACC_MIN_GAP_M, speed * ACC_TIME_GAP);
            float error = navAssistDistCenter - desiredGap; // +ve = too far, accelerate

            float dt = accPrevTicks == 0 ? 0.05f : (now - accPrevTicks) / 10000000f;
            if (dt < 0.016f) dt = 0.016f;
            else if (dt > 0.2f) dt = 0.2f;

            accIntegral += error * dt;
            if (accIntegral > ACC_I_CLAMP) accIntegral = ACC_I_CLAMP;
            else if (accIntegral < -ACC_I_CLAMP) accIntegral = -ACC_I_CLAMP;
            float deriv = (error - accPrevError) / dt;
            float u = ACC_KP * error + ACC_KI * accIntegral + ACC_KD * deriv;
            accPrevError = error;
            accPrevTicks = now;

            if (u >= 0f)
            {
                accThrottleOut = Math.Min(1f, u);
                accBrakeOut = 0f;
            }
            else
            {
                accThrottleOut = 0f;
                accBrakeOut = Math.Min(1f, -u);
            }

            // Speed cap: never accelerate past the configured cruise speed.
            if (speed >= autodriveSpeed) accThrottleOut = 0f;
        }

        private void ApplyCachedSteeringInputs(Vehicle playerVeh)
        {
            // _SET_CONTROL_NORMAL: 0xE8A25867FBA3B05E
            // Control IDs: 59=Steer, 71=Accelerate, 72=Brake, 76=Handbrake

            long nowStamp = DateTime.Now.Ticks;

            // Fire any deferred rumble pulses (e.g. the second half of a
            // right-side double pulse) that have come due since last tick.
            DrainRumbleQueue();

            // REVERSE-AWARE BRAKING: GTA control 72 (VEH_BRAKE) only brakes a
            // car moving FORWARD — on a car rolling backward it is the reverse
            // throttle and ACCELERATES the backward motion. When the car is
            // reversing and this is NOT a commanded reverse maneuver, every
            // brake request must instead arrest the backward roll via control
            // 71 (accelerate) + the direction-agnostic handbrake (76).
            bool brakingAReversingCar = isReversing && !alignmentEngageReverse;

            // ---- PER-FRAME BRAKE RECOMPUTE ----
            // If we have a recent brake threat, recompute its TTC against the LIVE
            // vehicle speed/position. This is what the user asked for: "really inform
            // the drive assist with the current MPH speed and apply adjustments every
            // frame." Between full ProcessSteeringAssist scans (~50 ms cadence), the
            // player's speed can change dramatically — without this recompute, the
            // brake input would lag a full detection cycle.
            float liveBrakeTarget = cachedBrakeMagnitude;
            float liveTtc = float.MaxValue;
            // Iter-11 Patch J: speed-scaled cache validity — at highway speed
            // a 250 ms-old threat pos is 4-6 m off, exactly the F2894 (-102.6
            // health) error magnitude.
            long brakeCacheValidTicks = ThreatCacheValidTicksForSpeed(playerVeh.Speed);
            bool hasLiveBrakeThreat = cachedBrakeThreatPos != GTA.Math.Vector3.Zero
                && (nowStamp - cachedBrakeThreatStamp) < brakeCacheValidTicks;
            if (hasLiveBrakeThreat)
            {
                liveTtc = CalculateTTC(playerVeh, cachedBrakeThreatPos, cachedBrakeThreatVel);
                // Map TTC → brake magnitude: <0.4s = full, <1.5s = ramped, >1.5s = none.
                if (liveTtc < 0.4f)
                    liveBrakeTarget = 1.0f;
                else if (liveTtc < 1.5f)
                    liveBrakeTarget = 1.0f - ((liveTtc - 0.4f) / 1.1f);
                else
                    liveBrakeTarget = 0f;
            }

            // ---- BRAKE HYSTERESIS (arm/release) ----
            // Once armed, stay armed until TTC clears comfortably above the release
            // threshold — prevents single-frame brake taps when TTC oscillates.
            // Arm-TTC scales with speed: fixed 0.8s gave only 24 m of arming
            // distance at 30 m/s, which can't beat reaction + stopping.
            //   v=8  -> 0.8s / 1.6s   (city, matches old behavior)
            //   v=20 -> 1.1s / 2.2s
            //   v=30 -> 1.35s / 2.7s
            //   v>=56 -> 2.0s / 4.0s  (capped)
            float liveSpeed = playerVeh.Speed;
            float armTtc = Math.Min(2.0f, Math.Max(BRAKE_ARM_TTC, 0.025f * liveSpeed + 0.6f));
            float releaseTtc = armTtc * 2.0f;
            if (hasLiveBrakeThreat && liveTtc <= armTtc) brakeArmed = true;
            else if (!hasLiveBrakeThreat || liveTtc >= releaseTtc) brakeArmed = false;

            // Emergency latch overrides hysteresis.
            if (emergencyBrakeActive) { brakeArmed = true; liveBrakeTarget = 1.0f; }

            // ---- PRE-IMPACT WARNING BEEP ----
            // Saw-wave middle-C that ramps with TTC. Plays in the window before the
            // brake actually engages so the user can react (or brace). Suppressed
            // during handbrake-turn assists since that maneuver is user-requested.
            bool warningOk = hasLiveBrakeThreat
                && liveTtc < BRAKE_WARN_TTC_MAX
                && liveTtc > 0f
                && cachedHandbrakeMagnitude < 0.1f
                && steeringAssistActive;
            if (warningOk) PlayBrakeWarning(liveTtc);

            // Disarmed = no autobrake (gates failed). Hard zero the target so the
            // ramp decays even if cachedBrakeMagnitude was set by a stale scan.
            if (!brakeArmed && !emergencyBrakeActive) liveBrakeTarget = 0f;

            // ---- RECOVERY BRAKING ----
            // Recovery braking is NOT threat-gated — the car must slow to make a
            // tight realignment turn feasible. Applied after the disarm zero so
            // the threat-brake gates can't cancel it. Still goes through the ramp.
            if (recoveryBrakeRequest > liveBrakeTarget) liveBrakeTarget = recoveryBrakeRequest;

            // ---- CURVE BRAKING ----
            // Pre-emptive slow-down before a sharp bend (Full mode only). Like
            // recovery braking it is not threat-gated; it rides the same ramp.
            if (cachedIsFullMode && curveBrakeRequest > liveBrakeTarget)
                liveBrakeTarget = curveBrakeRequest;

            // ---- ACC BRAKING ----
            // Gentle comfort braking to hold headway. accBrakeOut is non-zero
            // only when ComputeACC is active, so no extra gating is needed
            // beyond avoiding conflict with handbrake turns / reverse recovery.
            if (accBrakeOut > liveBrakeTarget && !alignmentEngageReverse
                && cachedHandbrakeMagnitude < 0.1f)
                liveBrakeTarget = accBrakeOut;

            // ---- POST-TELEPORT BRAKE HOLD (iter-9 Patch C) ----
            // ResetForTeleport sets postTeleportBrakeHoldUntilTicks to a
            // 500 ms-from-now timestamp. While the hold is active we floor
            // liveBrakeTarget at POST_TELEPORT_BRAKE_FLOOR (0.4). This stops
            // the car from immediately closing on a fresh obstacle at the new
            // location while the next 1-2 scans rebuild threat data after a
            // CheckRoadTeleport jump. Has no effect outside the hold window.
            if (postTeleportBrakeHoldUntilTicks > 0
                && DateTime.Now.Ticks < postTeleportBrakeHoldUntilTicks
                && liveBrakeTarget < POST_TELEPORT_BRAKE_FLOOR)
                liveBrakeTarget = POST_TELEPORT_BRAKE_FLOOR;

            // ---- BRAKE RAMP ----
            // The brake control input lerps toward the target rather than snapping.
            // This is the "no more jerky brake" fix. With BRAKE_RAMP_RATE = 5.0/sec,
            // a 0 → 1 transition takes ~200 ms, which feels firm but physical.
            // Iter-11 Patch M: asymmetric urgent-up path. When the brake
            // target jumps ahead of the ramped value by >0.3 AND the car is
            // moving fast enough that 200 ms ramp latency means meters of
            // unbraked closing distance, use BRAKE_RAMP_RATE_URGENT (12/sec)
            // for that frame's UP-ramp only. Down-ramp stays at the comfort
            // rate so brake release doesn't lurch.
            float effRampUpRate = BRAKE_RAMP_RATE;
            if (liveBrakeTarget - rampedBrakeInput > 0.3f && playerVeh.Speed > 10f)
                effRampUpRate = BRAKE_RAMP_RATE_URGENT;
            float rampStepUp = effRampUpRate * Math.Max(deltaTime, 0.016f);
            float rampStepDown = BRAKE_RAMP_RATE * Math.Max(deltaTime, 0.016f);
            if (liveBrakeTarget > rampedBrakeInput)
                rampedBrakeInput = Math.Min(liveBrakeTarget, rampedBrakeInput + rampStepUp);
            else
                rampedBrakeInput = Math.Max(liveBrakeTarget, rampedBrakeInput - rampStepDown);

            // ---- MAJOR-INTERVENTION INPUT REFUSAL (Full mode only) ----
            // When the mod is actively braking, in recovery/alignment, or running a
            // handbrake/reverse/stuck-recovery maneuver, the player's raw control
            // reads must not reach the game — otherwise continued throttle/brake/
            // steer fight the mod's _SET_CONTROL_NORMAL writes below.
            //
            // CRITICAL: DISABLE_CONTROL_ACTION must be called BEFORE the mod's own
            // SetControlNormal writes for this frame, otherwise a later DISABLE
            // zeroes out the simulated value the mod just wrote. Placing it here
            // (after BRAKE RAMP, before STEER URGENCY / APPLY STEERING) means all
            // subsequent SetControlNormal calls in this function correctly
            // override the disabled state.
            //
            // The composite deliberately does NOT trigger on steer correction
            // magnitude alone — smoothedRoadCorrection is routinely ±0.5-0.9 in
            // normal curved-road cruising, and using it as a trigger caps speed
            // at ~5 mph (driveassist-2026-05-25-115625 frames 100-2451 show this
            // exactly: stable 1.9-2.2 m/s with mode=LaneKeeping and no brake).
            // The genuine "intervention" signals below are sufficient.
            // Three intervention layers, each disabling only the controls
            // that would actually fight the mod:
            //   brakeIntervening: mod is braking or armed — lock throttle so
            //                     player can't override; lock brake so player
            //                     can't lift it during the emergency.
            //   totalTakeover:    handbrake turn / reverse U-turn / stuck
            //                     autodrive — mod owns everything.
            //   inRecoveryMode:   currentDriveMode != LaneKeeping but no
            //                     active braking. Lock steering (mod is
            //                     pursuing a recovery target) but ALLOW
            //                     throttle and brake. Iteration-5 didn't
            //                     gate throttle on drive mode at all; iter-6
            //                     did and capped median speed at 2.6 m/s vs
            //                     iter-5's 8.8 (cross-log trend in
            //                     driveassist-2026-05-25-121415 audit).
            //                     Recovery forward-crawl writes 0.5 throttle;
            //                     letting the player add their own throttle
            //                     on top lets the car actually progress
            //                     toward the recovery target.
            bool brakeIntervening = cachedIsFullMode && (
                emergencyBrakeActive
                || brakeArmed
                || rampedBrakeInput > 0.05f
                || liveBrakeTarget > 0.05f);
            bool totalTakeover = cachedIsFullMode && (
                cachedHandbrakeMagnitude > 0.1f
                || alignmentEngageReverse
                || stuckAutodriveEngaged);
            bool inRecoveryMode = cachedIsFullMode && currentDriveMode != DriveMode.LaneKeeping;

            // Steering: any time the mod is owning the wheel.
            if (brakeIntervening || totalTakeover || inRecoveryMode)
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 59, true); // VEH_MOVE_LEFT_RIGHT

            // Brake / handbrake: only when the mod is actively braking.
            if (brakeIntervening || totalTakeover)
            {
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 72, true); // VEH_BRAKE
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 76, true); // VEH_HANDBRAKE
            }

            // Throttle: when actively braking or total-takeover. Plain recovery
            // mode does NOT lock throttle — the player should be able to add
            // forward power on top of the recovery forward-crawl. On a
            // reversing car, control 71 IS the brake (the existing throttle-
            // lockout block honors this), so skip the disable there.
            if ((brakeIntervening || totalTakeover) && !brakingAReversingCar)
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 71, true); // VEH_ACCELERATE

            // ---- iter-13 Patch W: TAKEOVER UX CUE ----
            // totalTakeover = the assist is actively driving (reverse U-turn,
            // pivot reverse bite, handbrake turn, stuck autodrive) and has locked
            // the throttle. A blind player otherwise feels only a dead pedal with
            // no explanation (the Fort Zancudo "wouldn't let me control the
            // throttle" complaint). Announce on the transition only (latched),
            // with a distinct double-pulse rumble, and announce the hand-back on
            // release. Routine braking keeps its own "Hard braking!" cue.
            bool throttleTakenNow = totalTakeover && !brakingAReversingCar;
            if (throttleTakenNow && !throttleTakeoverAnnounced)
            {
                string takeoverKind = stuckAutodriveEngaged ? "autodrive"
                    : pivotEscapeActive ? "pivot"
                    : alignmentEngageReverse ? "reverse" : "maneuver";
                Tolk.Speak("Assist driving. Hands off throttle.", true);
                TriggerRumble(0.5f, 120, true); // double pulse = distinct from threat rumble
                throttleTakeoverAnnounced = true;
                if (driveLogger != null && driveLogger.IsRunning)
                    driveLogger.Write("[F" + driveLogFrameCount + "] EVENT takeover:"
                        + " kind=" + takeoverKind + " throttleLocked=true");
            }
            else if (!throttleTakenNow && throttleTakeoverAnnounced)
            {
                Tolk.Speak("You have throttle.", true);
                throttleTakeoverAnnounced = false;
                if (driveLogger != null && driveLogger.IsRunning)
                    driveLogger.Write("[F" + driveLogFrameCount + "] EVENT takeover-release:"
                        + " throttleLocked=false");
            }

            // ---- PER-FRAME STEER URGENCY RECOMPUTE ----
            // Re-scale the cached steering correction by live steer-threat urgency.
            // If the threat became less imminent (speed dropped, or threat moved
            // away), urgency falls and we steer less — preventing over-correction
            // between detection cycles.
            float liveSteer = cachedSteerCorrection;
            // Iter-11 Patch J: same speed-scaled validity for the steer cache —
            // a steer threat 250 ms old at 25 m/s is 6 m off-axis and the
            // pure-pursuit steer would chase a phantom.
            if (cachedSteerThreatPos != GTA.Math.Vector3.Zero
                && (nowStamp - cachedSteerThreatStamp) < ThreatCacheValidTicksForSpeed(playerVeh.Speed))
            {
                float liveSteerTtc = CalculateTTC(playerVeh, cachedSteerThreatPos, cachedSteerThreatVel);
                float steerThr = cachedIsFullMode ? STEER_THRESHOLD_FULL : STEER_THRESHOLD_ASSIST;
                float liveUrgency = Math.Max(0f, Math.Min(1f, 1f - (liveSteerTtc / steerThr)));
                // Blend live urgency with cached direction: keep the side decision
                // from the last scan but rescale magnitude by current urgency.
                if (Math.Abs(cachedSteerCorrection) > 0.05f)
                {
                    float sign = Math.Sign(cachedSteerCorrection);
                    liveSteer = sign * Math.Abs(cachedSteerCorrection) * liveUrgency;
                }
            }

            // ---- APPLY STEERING ----
            if (Math.Abs(liveSteer) > 0.05f)
            {
                if (cachedIsFullMode)
                {
                    Function.Call((Hash)0xE8A25867FBA3B05E, 0, 59, liveSteer);
                }
                else
                {
                    float playerSteer = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, 59);
                    float blended = Math.Max(-1f, Math.Min(1f, playerSteer + liveSteer * 1.05f));
                    Function.Call((Hash)0xE8A25867FBA3B05E, 0, 59, blended);
                }
            }

            // ---- THROTTLE LOCKOUT (Full mode, the moment a brake is requested) ----
            // The previous threshold (rampedBrakeInput > 0.3) gave the player ~100 ms
            // window where they could fight the brake with throttle and cause the
            // collision anyway. Lock throttle as SOON AS the brake-arm gate triggers,
            // or any ramped brake input exists, or an emergency brake is latched.
            // When braking a reversing car, control 71 IS the brake — the
            // lockout must not zero it.
            bool throttleLockoutActive = cachedIsFullMode
                && !brakingAReversingCar
                && (brakeArmed || emergencyBrakeActive || rampedBrakeInput > 0.05f);
            if (throttleLockoutActive)
            {
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 71, true);
                Function.Call((Hash)0xE8A25867FBA3B05E, 0, 71, 0f);
            }

            // ---- ADAPTIVE CRUISE CONTROL THROTTLE ----
            // Apply ACC's throttle only when nothing else owns the pedal: no
            // threat lockout, no reverse recovery, no handbrake turn, no curve
            // brake. accThrottleOut is non-zero only when ACC is active.
            if (accThrottleOut > 0.01f && !throttleLockoutActive && !brakingAReversingCar
                && !alignmentEngageReverse && cachedHandbrakeMagnitude < 0.1f
                && curveBrakeRequest < 0.05f)
            {
                Function.Call((Hash)0xE8A25867FBA3B05E, 0, 71, accThrottleOut);
            }

            // ---- APPLY BRAKING (ramped) ----
            if (rampedBrakeInput > 0.05f)
            {
                if (brakingAReversingCar)
                {
                    // Car is rolling backward unintentionally: press accelerate
                    // (forward) + handbrake to stop it. Control 72 here would
                    // only deepen the reverse.
                    Function.Call((Hash)0xE8A25867FBA3B05E, 0, 71, rampedBrakeInput);
                    Function.Call((Hash)0xE8A25867FBA3B05E, 0, 76, Math.Min(1f, rampedBrakeInput));
                }
                else
                {
                    Function.Call((Hash)0xE8A25867FBA3B05E, 0, 72, rampedBrakeInput);
                }
            }

            // ---- RECOVERY FORWARD CRAWL (Full mode) ----
            // Full mode has no propulsion of its own. After an unintended
            // reverse is arrested (or any time a recovery/alignment mode leaves
            // the car stopped), nudge it forward so it pursues the recovery
            // target instead of sitting dead. Gated hard: no brake request, no
            // live brake threat, not reversing, not a commanded reverse.
            if (cachedIsFullMode
                && currentDriveMode != DriveMode.LaneKeeping
                && !isReversing && !brakingAReversingCar && !alignmentEngageReverse
                && rampedBrakeInput < 0.05f && cachedHandbrakeMagnitude < 0.1f
                && !hasLiveBrakeThreat
                && playerVeh.Speed < 4f)
            {
                // iter-13 Patch W: let the player add throttle on top of the
                // crawl (max, not override) so they can speed an escape/pivot
                // forward bite instead of fighting a fixed 0.5. Throttle is not
                // locked in this branch (totalTakeover is false here), so the
                // player's read is live.
                float playerThrottle = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, 71);
                float crawl = Math.Max(0.5f, playerThrottle);
                Function.Call((Hash)0xE8A25867FBA3B05E, 0, 71, crawl);
            }

            // ---- HANDBRAKE FOR CORRECTIVE TURNS (Full only) ----
            if (cachedHandbrakeMagnitude > 0.1f && cachedIsFullMode)
            {
                Function.Call((Hash)0xE8A25867FBA3B05E, 0, 76, cachedHandbrakeMagnitude);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 71, true);
                Function.Call((Hash)0xE8A25867FBA3B05E, 0, 71, 0f);
            }

            // ---- REVERSE U-TURN (alignment recovery) ----
            // GetAlignmentRecoverySteer sets alignmentEngageReverse when the car
            // is faced the wrong way at low speed.
            if (alignmentEngageReverse)
            {
                if (cachedIsFullMode)
                {
                    // Full mode: set forward speed directly to a small negative
                    // value to back the car up — control 72 alone would just
                    // brake. Once moving backward the (sign-inverted) alignment
                    // steering rotates the nose.
                    Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 71, true);
                    Function.Call((Hash)0xE8A25867FBA3B05E, 0, 71, 0f);
                    if (playerVeh.Speed < 3f)
                    {
                        playerVeh.ForwardSpeed = -3f;
                    }
                }
                else
                {
                    // Assist mode: can't override velocity without yanking the
                    // car away from the player. Hold the brake/reverse control
                    // instead — once the car stops this backs it up, which is
                    // what the reverse U-turn needs.
                    Function.Call((Hash)0xE8A25867FBA3B05E, 0, 72, 1.0f);
                }
            }

            // ---- IMMINENT-COLLISION HANDBRAKE BACKUP ----
            // When the ramped brake is at max, also tap the handbrake for max stop
            // power. NO velocity-multiplication anymore — the game's actual brake
            // physics handle the deceleration curve, which is what produces a
            // realistic feel instead of teleporting the speed down.
            if (rampedBrakeInput >= 0.95f)
            {
                Function.Call((Hash)0xE8A25867FBA3B05E, 0, 76, 1.0f);
                // Don't zero control 71 when it is being used as the
                // reverse-arrest brake (see brakingAReversingCar above).
                if (!brakingAReversingCar)
                    Function.Call((Hash)0xE8A25867FBA3B05E, 0, 71, 0f);

                if (cachedIsFullMode && DateTime.Now.Ticks - lastAssistAnnounceTicks > 30000000)
                {
                    Tolk.Speak("Hard braking!", true);
                    lastAssistAnnounceTicks = DateTime.Now.Ticks;
                }
            }

            // ---- HAPTIC RUMBLE (advisory feedback; both modes) ----
            // Each source latches so it rumbles once on threat onset, not every
            // tick. Brake-threat outranks lane-edge drift if both fire together.
            bool brakeRumbleNow = hasLiveBrakeThreat && liveTtc > 0f && liveTtc < 1.5f;
            if (brakeRumbleNow && !rumbleBrakeArmed)
            {
                float intensity = 0.3f + 0.7f * (1f - liveTtc / 1.5f);
                // Steering left (negative) means the obstacle is on the right —
                // signal that with a double pulse.
                bool rightSide = liveSteer < -0.05f;
                TriggerRumble(intensity, 90, rightSide);
            }
            if (brakeRumbleNow) rumbleBrakeArmed = true; else rumbleBrakeArmed = false;

            bool edgeRumbleNow = Math.Abs(lastLaneLateralError) > LANE_EDGE_RUMBLE_M;
            if (edgeRumbleNow && !rumbleEdgeArmed && !brakeRumbleNow)
                TriggerRumble(0.25f, 200);
            if (edgeRumbleNow) rumbleEdgeArmed = true; else rumbleEdgeArmed = false;
        }

        /// <summary>
        /// Pre-impact warning beep. Sawtooth at middle C; gain rises and inter-beep
        /// interval shrinks as TTC drops. The user requested this so they can act
        /// before the autobrake fires — a predictable cue, not a surprise slam.
        /// </summary>
        private void PlayBrakeWarning(float ttc)
        {
            // Normalize TTC into [0,1] where 1 = imminent.
            float t = (BRAKE_WARN_TTC_MAX - ttc) / Math.Max(0.001f, BRAKE_WARN_TTC_MAX - BRAKE_WARN_TTC_MIN);
            if (t < 0f) t = 0f; if (t > 1f) t = 1f;

            // Interval shrinks from 350 ms (gentle warning) to 60 ms (immediate).
            long intervalTicks = (long)((350.0 - (350.0 - 60.0) * t) * 10000.0);
            long now = DateTime.Now.Ticks;
            if (now - lastBrakeWarnTicks < intervalTicks) return;
            lastBrakeWarnTicks = now;

            // Gain rises from 0.05 → 0.30 across the same range.
            brakeWarnTone.Gain = 0.05 + 0.25 * t;
            double durationMs = Math.Min(120.0, (intervalTicks / 10000.0) - 20.0);
            if (durationMs < 30.0) durationMs = 30.0;

            try
            {
                outBrakeWarn.Stop();
                var sample = brakeWarnTone.Take(TimeSpan.FromMilliseconds(durationMs));
                outBrakeWarn.Init(sample);
                outBrakeWarn.Play();
            }
            catch { /* audio device contention is non-fatal */ }
        }

        // ============================================
        // HAPTIC RUMBLE FEEDBACK (Drive Assist)
        // ============================================
        // SET_PAD_RUMBLE has no left/right motor selector, so obstacle side is
        // conveyed by pulse pattern: left = a single short pulse, right = a
        // double pulse, road edge = a single long low buzz. Purely additive
        // feedback — degrades silently if the native is unavailable.
        private long lastRumbleTicks = 0;
        // Pending deferred pulses: each entry is { fireAtTicks, intensityBytes, durationMs }.
        private readonly List<long[]> rumbleQueue = new List<long[]>();

        private void FireRumble(int intensityBytes, int durationMs)
        {
            try
            {
                // SET_PAD_RUMBLE: 0x14D29BB12D47F68C (padIndex, durationMs, intensity)
                Function.Call((Hash)0x14D29BB12D47F68C, 0, durationMs, intensityBytes);
            }
            catch { /* native unavailable — silently degrade to no rumble */ }
        }

        /// <summary>Fires a controller rumble pulse, gated on the hapticFeedback
        /// setting and an active gamepad. When doublePulse is set a second pulse
        /// is queued ~150 ms later (used to signal an obstacle on the right).</summary>
        private void TriggerRumble(float intensity01, int durationMs, bool doublePulse = false)
        {
            if (getSetting("hapticFeedback") != 1) return;
            if (Game.LastInputMethod != InputMethod.GamePad) return;
            long now = DateTime.Now.Ticks;
            if (now - lastRumbleTicks < 1200000) return; // 120 ms min gap, no machine-gunning
            lastRumbleTicks = now;
            int bytes = (int)(Math.Max(0f, Math.Min(1f, intensity01)) * 255f);
            FireRumble(bytes, durationMs);
            if (doublePulse)
                rumbleQueue.Add(new long[] { now + 1500000, bytes, durationMs }); // +150 ms
        }

        /// <summary>Drains queued deferred rumble pulses. Called every tick.</summary>
        private void DrainRumbleQueue()
        {
            if (rumbleQueue.Count == 0) return;
            long now = DateTime.Now.Ticks;
            for (int i = rumbleQueue.Count - 1; i >= 0; i--)
            {
                if (now >= rumbleQueue[i][0])
                {
                    FireRumble((int)rumbleQueue[i][1], (int)rumbleQueue[i][2]);
                    rumbleQueue.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Speaks the current setting value with proper handling for multi-value settings
        /// </summary>
        private void SpeakCurrentSetting()
        {
            if (settingsMenu[settingsMenuIndex].id == "detectionRadius")
            {
                int radiusIndex = settingsMenu[settingsMenuIndex].value;
                if (radiusIndex < 0 || radiusIndex >= detectionRadiusOptions.Length)
                    radiusIndex = 1;
                Tolk.Speak("Detection Radius: " + (int)detectionRadiusOptions[radiusIndex] + " meters");
            }
            else if (settingsMenu[settingsMenuIndex].id == "steeringAssist")
            {
                int mode = settingsMenu[settingsMenuIndex].value;
                string modeText = mode == 0 ? "Off" : (mode == 1 ? "Assistive" : "Full");
                Tolk.Speak("Steering Assist Mode: " + modeText);
            }
            else
            {
                string toggle = settingsMenu[settingsMenuIndex].value == 0 ? "Off" : "On";
                Tolk.Speak(settingsMenu[settingsMenuIndex].displayName + toggle);
            }
        }

        string idToName(string id)
        {
            string result = "None";

            if (id == "godMode")
                result = "God Mode. ;";
            if (id == "radioOff")
                result = "Always Disable vehicle radios. ";
            if (id == "warpInsideVehicle")
                result = "Teleport player inside newly spawned vehicles. ";
            if (id == "onscreen")
                result = "Announce only visible nearby items. ";
            if (id == "speed")
                result = "Announce current vehicle speed. ";

            if (id == "policeIgnore")
                result = "Police Ignore Player. ";
            if (id == "vehicleGodMode")
                result = "Make Current vehicle indestructable. ";
            if (id == "amphibiousMode")
                result = "Amphibious Vehicle (drive underwater). ";
            if (id == "altitudeIndicator")
                result = "audible Altitude Indicator. ";
            if (id == "targetPitchIndicator")
                result = "audible Targetting Pitch Indicator. ";
            if (id == "navigationAssist")
                result = "Navigation Assist (Obstacle Detection). ";
            if (id == "navAssistBeeps")
                result = "Navigation Assist Audio Beeps. ";
            if (id == "pickupDetection")
                result = "Pickup/Item Detection. ";
            if (id == "coverDetection")
                result = "Cover Detection (Combat). ";
            if (id == "waterHazardDetection")
                result = "Water/Hazard Detection. ";
            if (id == "vehicleHealthFeedback")
                result = "Vehicle Health Announcements. ";
            if (id == "staminaFeedback")
                result = "Stamina/Sprint Warnings. ";
            if (id == "interactableDetection")
                result = "Interactable Object Detection. ";
            if (id == "trafficAwareness")
                result = "Traffic Awareness Warnings. ";
            if (id == "wantedLevelDetails")
                result = "Wanted Level Details. ";
            if (id == "slopeTerrainFeedback")
                result = "Slope/Terrain Feedback. ";
            if (id == "turnByTurnNavigation")
                result = "Turn-by-Turn Navigation. ";
            if (id == "serviceProximity")
                result = "Service/Building Proximity Announcements. ";
            if (id == "detectionRadius")
                result = "Detection Radius: ";
            if (id == "infiniteAmmo")
                result = "Unlimitted Ammo. ";
            if (id == "neverWanted")
                result = "Wanted Level Never Increases. ";
            if (id == "superJump")
                result = "Super Jump. ";
            if (id == "runFaster")
                result = "Run Faster. ";
            if (id == "swimFaster")
                result = "Fast Swimming. ";
            if (id == "exsplosiveAmmo")
                result = "Explosive Ammo. ";
            if (id == "fireAmmo")
                result = "Fire Ammo. ";
            if (id == "explosiveMelee")
                result = "Explosive Melee. ";
            if (id == "aimAutolock")
                result = "Aim Autolock & Target Tracking. ";
            if (id == "steeringAssist")
                result = "Steering Assist Mode: ";
            if (id == "shapeCasting")
                result = "Enhanced Obstacle Detection (Shape Casting). ";
            if (id == "roadTeleport")
                result = "Auto-Teleport to Road (Drive Assist). ";
            if (id == "waypointDriveAssist")
                result = "Waypoint-Aware Drive Assist. ";
            if (id == "hapticFeedback")
                result = "Controller Haptic Feedback (Drive Assist). ";
            if (id == "adaptiveCruise")
                result = "Adaptive Cruise Control (Full Mode). ";
            if (id == "bodyguardAutoRespawn")
                result = "Bodyguard Auto-Respawn. ";
            if (id == "driveAssistDebugLog")
                result = "Drive Assist Debug Logging (writes log file). ";
            if (id == "announceTime")
                result = "Time of Day Announcements. ";
            if (id == "announceHeadings")
                result = "Heading Change Announcements. ";
            if (id == "announceZones")
                result = "Street and Zone Change Announcements. ";

            //if (id == )
            return result;


        }

        // Undoes the underwater protections applied by amphibious mode. The collision/
        // explosion/fire and wheel/tire flags are left to the vehicleGodMode block when
        // that mode owns them; otherwise they are restored to normal here.
        private void RestoreAmphibiousProtections(Vehicle vehicle)
        {
            Function.Call(Hash.SET_VEHICLE_ENGINE_CAN_DEGRADE, vehicle, true);
            Function.Call(Hash.SET_DISABLE_VEHICLE_PETROL_TANK_DAMAGE, vehicle, false);
            Function.Call(Hash.SET_DISABLE_VEHICLE_PETROL_TANK_FIRES, vehicle, false);
            if (getSetting("vehicleGodMode") != 1)
            {
                vehicle.IsCollisionProof = false;
                vehicle.IsExplosionProof = false;
                vehicle.IsFireProof = false;
                vehicle.CanWheelsBreak = true;
                vehicle.CanTiresBurst = true;
            }
            amphibiousProtectionActive = false;
        }

        int getSetting(string id)
        {
            int result = -1;
            for (int i = 0; i < settingsMenu.Count; i++)
            {
                if (settingsMenu[i].id == id)
                    result = settingsMenu[i].value;
            }
            return result;
        }

        // Helper to get current detection radius from setting
        private float GetDetectionRadius()
        {
            int index = getSetting("detectionRadius");
            if (index < 0 || index >= detectionRadiusOptions.Length)
                index = 1; // Default to 25m
            return detectionRadiusOptions[index];
        }

        // Helper method to get frequency based on entity type and distance
        private float GetFrequencyForType(string type, float normalizedDistance)
        {
            // Closer = higher pitch (lower normalizedDistance = higher frequency)
            switch (type)
            {
                case "ped":
                    // Peds: warmer tone, 150-400 Hz
                    return 400 - (normalizedDistance * 250);
                case "vehicle":
                    // Vehicles: urgent tone, 300-800 Hz
                    return 800 - (normalizedDistance * 500);
                default: // world, prop
                         // World: standard tone, 200-600 Hz
                    return 600 - (normalizedDistance * 400);
            }
        }

        // Helper method to get waveform based on entity type
        private SignalGeneratorType GetWaveformForType(string type)
        {
            switch (type)
            {
                case "ped":
                    return SignalGeneratorType.Triangle; // Soft, warm
                case "vehicle":
                    return SignalGeneratorType.SawTooth; // Harsh, urgent
                default: // world, prop
                    return SignalGeneratorType.Square; // Standard
            }
        }

        // ============================================
        // SHAPE CASTING HELPER METHODS
        // ============================================

        /// <summary>
        /// Performs a multi-ray "shape cast" that simulates sphere/capsule collision detection.
        /// Returns the closest hit distance, or -1 if no hit.
        /// </summary>
        // Real swept-capsule shape test against world geometry. Replaces the previous
        // multi-ray fan (which fired up to 15 raycasts per call). One native does both:
        //  - Volumetric clearance (radius = effective vehicle width)
        //  - Continuous sweep from start → end (no gaps between ray samples)
        //
        // START_SHAPE_TEST_SWEPT_SPHERE is async in theory but completes in the same
        // frame in practice; GET_SHAPE_TEST_RESULT returns ready immediately.
        //
        // The `_rightVec` parameter is kept for API compatibility but no longer used.
        private float PerformShapeCast(GTA.Math.Vector3 startPos, GTA.Math.Vector3 forwardVec,
            GTA.Math.Vector3 _rightVec, float maxRange, IntersectFlags flags, Entity exclude,
            out GTA.Math.Vector3 hitPosition, out GTA.Math.Vector3 hitNormal, float vehicleSpeed = 0f)
        {
            hitPosition = GTA.Math.Vector3.Zero;
            hitNormal = GTA.Math.Vector3.Zero;

            // Capsule radius scales continuously with speed. At freeway speed the
            // vehicle wanders laterally and a binary 1.0/1.8 step under-detects:
            //   v=5  -> 1.0m  (~car half-width)
            //   v=15 -> 1.4m  (suburban, 0.4m margin past body)
            //   v=30 -> 2.0m  (highway, full width + lateral wander)
            //   v>=42 -> 2.5m (capped; wider phantom-brakes parked cars)
            // Old binary SHAPE_CAST_SPEED_THRESHOLD constant retained but unused.
            float radius = Math.Min(2.5f, Math.Max(1.0f, 0.04f * vehicleSpeed + 0.8f));

            GTA.Math.Vector3 endPos = startPos + forwardVec * maxRange;
            int excludeHandle = (exclude != null && exclude.Exists()) ? exclude.Handle : 0;

            // START_SHAPE_TEST_SWEPT_SPHERE: 0xE6AC6C45FBE83004
            //   (x1,y1,z1, x2,y2,z2, radius, flags, ignoreEntity, p9)
            int handle = Function.Call<int>(
                (Hash)0xE6AC6C45FBE83004,
                startPos.X, startPos.Y, startPos.Z,
                endPos.X, endPos.Y, endPos.Z,
                radius, (int)flags, excludeHandle, 7);

            // GET_SHAPE_TEST_RESULT: 0x3D87450E15D98694
            //   returns 0=null, 1=ready, 2=not_ready; writes didHit/hitPos/normal/hitEntity
            OutputArgument oDidHit = new OutputArgument();
            OutputArgument oHitPos = new OutputArgument();
            OutputArgument oNormal = new OutputArgument();
            OutputArgument oHitEnt = new OutputArgument();
            Function.Call(
                (Hash)0x3D87450E15D98694,
                handle, oDidHit, oHitPos, oNormal, oHitEnt);

            if (!oDidHit.GetResult<bool>())
                return -1f;

            hitPosition = oHitPos.GetResult<GTA.Math.Vector3>();
            hitNormal = oNormal.GetResult<GTA.Math.Vector3>();
            return World.GetDistance(startPos, hitPosition);
        }

        // Long-range capsule sweep against STATIC MAP GEOMETRY ONLY.
        // Walls/buildings/terrain don't move, so we can plan further ahead than the
        // main fan (which has to react to traffic). Splitting flags also prevents
        // small objects from masking large static walls behind them.
        //
        // Tuned 2026-05-25 after the F10015-class phantom-brake failures: the
        // previous floor of 1.5 m radius caused the capsule to bite into roadside
        // curbs/embankments at any speed under 6 m/s. The 0.9 m floor matches a
        // typical vehicle half-width and stops sidewalk-curb hits while still
        // covering normal lane width. Range also pulled back from v*5 to v*4 so
        // we don't pick up walls 80+ m ahead that the player has plenty of time
        // to handle without an emergency brake.
        //   range: min(180, max(20, v*4.0))
        //     v=15 -> 60m,  v=30 -> 120m,  v>=45 -> 180m (cap)
        //   radius: min(2.2, max(0.9, 0.04*v + 0.7))
        //     v=5 -> 0.9m,  v=15 -> 1.3m,  v=30 -> 1.9m,  v>=38 -> 2.2m (cap)
        // Returns hit distance or -1 if no hit.
        private float PerformStaticWallCast(GTA.Math.Vector3 startPos,
            GTA.Math.Vector3 forwardVec, float vehicleSpeed, Entity exclude,
            out GTA.Math.Vector3 hitPosition, out GTA.Math.Vector3 hitNormal,
            out float outRange, out float outRadius)
        {
            hitPosition = GTA.Math.Vector3.Zero;
            hitNormal = GTA.Math.Vector3.Zero;

            outRange  = Math.Min(180f, Math.Max(20f, vehicleSpeed * 4.0f));
            outRadius = Math.Min(2.2f, Math.Max(0.9f, 0.04f * vehicleSpeed + 0.7f));

            GTA.Math.Vector3 endPos = startPos + forwardVec * outRange;
            int excludeHandle = (exclude != null && exclude.Exists()) ? exclude.Handle : 0;

            int handle = Function.Call<int>(
                (Hash)0xE6AC6C45FBE83004,
                startPos.X, startPos.Y, startPos.Z,
                endPos.X, endPos.Y, endPos.Z,
                outRadius, (int)IntersectFlags.Map, excludeHandle, 7);

            OutputArgument oDidHit = new OutputArgument();
            OutputArgument oHitPos = new OutputArgument();
            OutputArgument oNormal = new OutputArgument();
            OutputArgument oHitEnt = new OutputArgument();
            Function.Call(
                (Hash)0x3D87450E15D98694,
                handle, oDidHit, oHitPos, oNormal, oHitEnt);

            if (!oDidHit.GetResult<bool>())
                return -1f;

            hitPosition = oHitPos.GetResult<GTA.Math.Vector3>();
            hitNormal = oNormal.GetResult<GTA.Math.Vector3>();
            return World.GetDistance(startPos, hitPosition);
        }

        // ============================================
        // BODYGUARD / AI COMPANION SYSTEM - METHODS
        // ============================================

        void InitializeBodyguardMenu()
        {
            bodyguardMenu.Add("Toggle Bodyguard System");           // 0
            bodyguardMenu.Add("Spawn Primary Guard (Butler)");      // 1
            bodyguardMenu.Add("Spawn Additional Guard");            // 2
            bodyguardMenu.Add("Dismiss Last Guard");                // 3
            bodyguardMenu.Add("Dismiss All Guards");                // 4
            bodyguardMenu.Add("Recall All Guards");                 // 5
            bodyguardMenu.Add("Ground Extraction");                 // 6
            bodyguardMenu.Add("Helicopter Extraction");             // 7
            bodyguardMenu.Add("Guard Model");                       // 8
            bodyguardMenu.Add("Weapon (All Guards)");               // 9
            bodyguardMenu.Add("Reload Weapon Config");              // 10
            bodyguardMenu.Add("Combat Style");                      // 11
            bodyguardMenu.Add("Formation");                         // 12
            bodyguardMenu.Add("Formation Spacing");                 // 13
            bodyguardMenu.Add("Guard God Mode");                    // 14
            bodyguardMenu.Add("Auto-Respawn");                      // 15
            bodyguardMenu.Add("Guard Armor");                       // 16
            bodyguardMenu.Add("Auto-Patrol");                       // 17
            bodyguardMenu.Add("Guard Callouts");                    // 18
            bodyguardMenu.Add("Butler Beacon");                     // 19
            bodyguardMenu.Add("Butler POI Narration");              // 20
            bodyguardMenu.Add("Send Guards to Waypoint");           // 21
            bodyguardMenu.Add("Hold Position");                     // 22
            bodyguardMenu.Add("Follow Me");                         // 23
            bodyguardMenu.Add("Attack My Target");                  // 24
            bodyguardMenu.Add("Cease Fire");                        // 25
            bodyguardMenu.Add("Guard Status");                      // 26
            bodyguardMenu.Add("Ground Extraction Distance");         // 27
            bodyguardMenu.Add("Helicopter Extraction Distance");     // 28
            bodyguardMenu.Add("Land");                                  // 29
            bodyguardMenu.Add("Park at Nearest Safe Spot");             // 30
            bodyguardMenu.Add("Proactive Detection");                    // 31
            bodyguardMenu.Add("Armed Ped Alert");                        // 32
        }

        void InitializeWeaponNameMap()
        {
            WEAPON_NAME_MAP["Pistol"] = WeaponHash.Pistol;
            WEAPON_NAME_MAP["APPistol"] = WeaponHash.APPistol;
            WEAPON_NAME_MAP["CombatPistol"] = WeaponHash.CombatPistol;
            WEAPON_NAME_MAP["HeavyPistol"] = WeaponHash.HeavyPistol;
            WEAPON_NAME_MAP["Pistol50"] = WeaponHash.Pistol50;
            WEAPON_NAME_MAP["MicroSMG"] = WeaponHash.MicroSMG;
            WEAPON_NAME_MAP["SMG"] = WeaponHash.SMG;
            WEAPON_NAME_MAP["CombatPDW"] = WeaponHash.CombatPDW;
            WEAPON_NAME_MAP["AssaultRifle"] = WeaponHash.AssaultRifle;
            WEAPON_NAME_MAP["CarbineRifle"] = WeaponHash.CarbineRifle;
            WEAPON_NAME_MAP["SpecialCarbine"] = WeaponHash.SpecialCarbine;
            WEAPON_NAME_MAP["AdvancedRifle"] = WeaponHash.AdvancedRifle;
            WEAPON_NAME_MAP["PumpShotgun"] = WeaponHash.PumpShotgun;
            WEAPON_NAME_MAP["AssaultShotgun"] = WeaponHash.AssaultShotgun;
            WEAPON_NAME_MAP["RPG"] = WeaponHash.RPG;
            WEAPON_NAME_MAP["Minigun"] = WeaponHash.Minigun;
            WEAPON_NAME_MAP["GrenadeLauncher"] = WeaponHash.GrenadeLauncher;
            WEAPON_NAME_MAP["SniperRifle"] = WeaponHash.SniperRifle;
            WEAPON_NAME_MAP["HeavySniper"] = WeaponHash.HeavySniper;
            WEAPON_NAME_MAP["Knife"] = WeaponHash.Knife;
            WEAPON_NAME_MAP["Bat"] = WeaponHash.Bat;
        }

        void SetupGuardGroup()
        {
            if (bodyguardGroupId >= 0)
            {
                Function.Call(Hash.REMOVE_GROUP, bodyguardGroupId);
            }
            bodyguardGroupId = Function.Call<int>(Hash.CREATE_GROUP, 0);
            Function.Call(Hash.SET_PED_AS_GROUP_LEADER, Game.Player.Character, bodyguardGroupId);
            Function.Call(Hash.SET_GROUP_FORMATION, bodyguardGroupId, FORMATION_TYPES[guardFormationIndex].id >= 0 ? FORMATION_TYPES[guardFormationIndex].id : 0);
            float spacing = FORMATION_SPACING_OPTIONS[guardFormationSpacingIndex];
            Function.Call(Hash.SET_GROUP_FORMATION_SPACING, bodyguardGroupId, spacing, spacing, spacing);

            foreach (Ped guard in bodyguards)
            {
                if (guard != null && guard.Exists())
                {
                    Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, guard, bodyguardGroupId);
                }
            }
        }

        void CleanupGuardGroup()
        {
            if (bodyguardGroupId >= 0)
            {
                Function.Call(Hash.REMOVE_GROUP, bodyguardGroupId);
                bodyguardGroupId = -1;
            }
        }

        void SetupGuardRelationship(Ped guard)
        {
            int playerRelGroup = Function.Call<int>(Hash.GET_PED_RELATIONSHIP_GROUP_HASH, Game.Player.Character);
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, guard, playerRelGroup);
        }

        void SetupGuardAttributes(Ped guard, int guardIndex)
        {
            guard.IsPersistent = true;
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, guard, true, true);

            // Infinite ammo
            Function.Call(Hash.SET_PED_INFINITE_AMMO, guard, true);
            Function.Call(Hash.SET_PED_INFINITE_AMMO_CLIP, guard, true);

            // Flee prevention
            Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, guard, 0, false);

            // Persistence flags
            guard.AlwaysKeepTask = true;
            guard.BlockPermanentEvents = true;

            // Cannot be dragged out of vehicles (prevents player carjacking Butler)
            guard.CanBeDraggedOutOfVehicle = false;

            if (guardIndex == 0)
            {
                // Butler: defensive/protective -- Secret Service mode
                Function.Call(Hash.SET_PED_COMBAT_ABILITY, guard, 2);       // Professional
                Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, guard, 1);      // Defensive
                Function.Call(Hash.SET_PED_COMBAT_RANGE, guard, 0);         // Near
                Function.Call(Hash.SET_PED_ACCURACY, guard, 80);
                Function.Call(Hash.SET_PED_TARGET_LOSS_RESPONSE, guard, 0); // Don't search
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 5, true);  // Use cover
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 13, false); // NOT aggressive
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 1, true);  // Can use vehicles
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 2, true);  // Can do drivebys
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 3, false); // Cannot leave vehicle (prevents exiting to fight during extraction/driving)
                Function.Call(Hash.SET_PED_FIRING_PATTERN, guard, unchecked((uint)0xD6FF6D61)); // Default
            }
            else
            {
                // Guards 1-6: offensive combat
                ApplyCombatStyle(guard, guardCombatStyleIndex);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 46, true); // Can fight armed when unarmed
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 1, true);  // Can use vehicles
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 2, true);  // Can do drivebys
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 3, true);  // Can leave vehicle
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 20, true); // Can taunt in vehicle
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 14, true); // Can investigate

                // Enhanced sensing for proactive detection
                Function.Call(Hash.SET_PED_SEEING_RANGE, guard, 150f);
                Function.Call(Hash.SET_PED_HEARING_RANGE, guard, 80f);
                Function.Call(Hash.SET_PED_ALERTNESS, guard, 3); // Maximum alertness
            }

            // Set weapon
            if (guardWeaponIndex >= 0 && guardWeaponIndex < GUARD_WEAPONS.Length)
            {
                guard.Weapons.Give(GUARD_WEAPONS[guardWeaponIndex].hash, 9999, true, true);
            }

            // Set armor
            Function.Call(Hash.SET_PED_ARMOUR, guard, ARMOR_LEVEL_VALUES[guardArmorIndex]);

            // God mode if enabled
            if (guardGodMode)
            {
                guard.IsInvincible = true;
                guard.CanBeKnockedOffBike = false;
                guard.CanBeShotInVehicle = false;
                guard.CanFlyThroughWindscreen = false;
            }
        }

        void SpawnPrimaryGuard()
        {
            if (bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].Exists() && bodyguards[0].IsAlive)
            {
                Tolk.Speak("Butler already active.");
                return;
            }

            Model model = new Model(GUARD_MODELS[guardModelIndex].hash);
            model.Request(5000);
            if (!model.IsLoaded)
            {
                Tolk.Speak("Model failed to load. Try again.");
                return;
            }

            GTA.Math.Vector3 spawnPos = Game.Player.Character.Position + Game.Player.Character.ForwardVector * 2f;
            Ped guard = World.CreatePed(model, spawnPos, Game.Player.Character.Heading);
            model.MarkAsNoLongerNeeded();

            if (guard == null)
            {
                Tolk.Speak("Could not spawn guard. Too many entities.");
                return;
            }

            if (bodyguards.Count == 0)
                bodyguards.Add(guard);
            else
                bodyguards[0] = guard;

            SetupGuardAttributes(guard, 0);
            SetupGuardRelationship(guard);
            Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, guard, bodyguardGroupId);

            // Reset health tracking for index 0
            guardDeathAnnounced[0] = false;
            for (int t = 0; t < 3; t++) guardHealthWarnings[0, t] = false;

            Tolk.Speak("Butler spawned. " + GUARD_MODELS[guardModelIndex].name + ".");
        }

        void SpawnAdditionalGuard()
        {
            if (bodyguards.Count >= 7)
            {
                Tolk.Speak("Maximum 7 guards reached.");
                return;
            }

            Model model = new Model(GUARD_MODELS[guardModelIndex].hash);
            model.Request(5000);
            if (!model.IsLoaded)
            {
                Tolk.Speak("Model failed to load. Try again.");
                return;
            }

            int i = bodyguards.Count;
            float angleOffset = Game.Player.Character.Heading + 180f + (i * 60f);
            float radians = angleOffset * (float)(Math.PI / 180.0);
            GTA.Math.Vector3 spawnPos = Game.Player.Character.Position + new GTA.Math.Vector3(
                (float)Math.Sin(radians) * 3f,
                (float)Math.Cos(radians) * 3f,
                0f);
            Ped guard = World.CreatePed(model, spawnPos, Game.Player.Character.Heading);
            model.MarkAsNoLongerNeeded();

            if (guard == null)
            {
                Tolk.Speak("Could not spawn guard. Too many entities.");
                return;
            }

            bodyguards.Add(guard);
            SetupGuardAttributes(guard, i);
            SetupGuardRelationship(guard);
            Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, guard, bodyguardGroupId);

            // Reset health tracking
            if (i < 7)
            {
                guardDeathAnnounced[i] = false;
                for (int t = 0; t < 3; t++) guardHealthWarnings[i, t] = false;
            }

            Tolk.Speak("Guard " + i + " spawned. " + bodyguards.Count + " of 7 active.");
            AssignGuardRoles();
        }

        void DismissGuard(int index)
        {
            if (index < 0 || index >= bodyguards.Count) return;
            Ped guard = bodyguards[index];
            if (guard != null && guard.Exists())
            {
                guard.Task.ClearAll();
                guard.IsPersistent = false;
                guard.MarkAsNoLongerNeeded();
                guard.Delete();
            }
            bodyguards.RemoveAt(index);

            if (index < 7)
            {
                guardDeathAnnounced[index] = false;
                for (int t = 0; t < 3; t++) guardHealthWarnings[index, t] = false;
            }

            if (index == 0)
            {
                guardDriverActive = false;
            }

            string label = (index == 0) ? "Butler" : "Guard " + index;
            Tolk.Speak(label + " dismissed. " + bodyguards.Count + " remaining.");
            AssignGuardRoles();
        }

        void DismissAllGuards()
        {
            for (int i = bodyguards.Count - 1; i >= 0; i--)
            {
                Ped guard = bodyguards[i];
                if (guard != null && guard.Exists())
                {
                    guard.Task.ClearAll();
                    guard.IsPersistent = false;
                    guard.MarkAsNoLongerNeeded();
                    guard.Delete();
                }
            }
            bodyguards.Clear();
            guardDriverActive = false;
            convoyActive = false;
            heliGroundConvoyActive = false;
            heliGroundConvoyTarget = GTA.Math.Vector3.Zero;
            guardsPatrolling = false;
            extractionInProgress = false;
            heliLandingPhase = false;
            CleanupExtractionVehicle();
            guardCurrentTarget.Clear();

            // Clean up convoy vehicles
            DismissConvoyVehicles();

            guardHealthWarnings = new bool[7, 3];
            guardDeathAnnounced = new bool[7];

            CleanupGuardGroup();
            Tolk.Speak("All guards dismissed.");
        }

        void DismissConvoyVehicles()
        {
            // Eject guards from convoy vehicles before deleting them
            foreach (Vehicle v in convoyVehicles)
            {
                if (v != null && v.Exists())
                {
                    // Clear tasks for any bodyguards inside this convoy vehicle
                    for (int i = 1; i < bodyguards.Count; i++)
                    {
                        Ped guard = bodyguards[i];
                        if (guard == null || !guard.Exists() || guard.IsDead) continue;
                        if (guard.IsInVehicle() && guard.CurrentVehicle == v)
                        {
                            guard.Task.LeaveVehicle();
                        }
                    }

                    // Remove NPC drivers that aren't our guards
                    Ped driver = v.Driver;
                    if (driver != null && driver.Exists() && !bodyguards.Contains(driver))
                    {
                        driver.Delete();
                    }

                    v.IsPersistent = false;
                    v.MarkAsNoLongerNeeded();
                    v.Delete();
                }
            }
            convoyVehicles.Clear();
            convoyActive = false;
            heliGroundConvoyActive = false;
            heliGroundConvoyTarget = GTA.Math.Vector3.Zero;

            // Re-add guards to follow group
            for (int i = 1; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                if (guard == null || !guard.Exists() || guard.IsDead) continue;
                if (bodyguardGroupId >= 0)
                    Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, guard, bodyguardGroupId);
                guard.AlwaysKeepTask = true;
                guard.BlockPermanentEvents = true;
            }
        }

        void ApplyCombatStyle(Ped guard, int styleIndex)
        {
            switch (styleIndex)
            {
                case 0: // Aggressive
                    Function.Call(Hash.SET_PED_ACCURACY, guard, 90);
                    Function.Call(Hash.SET_PED_COMBAT_ABILITY, guard, 2);
                    Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, guard, 3);
                    Function.Call(Hash.SET_PED_COMBAT_RANGE, guard, 2);
                    Function.Call(Hash.SET_PED_FIRING_PATTERN, guard, unchecked((uint)0xC6EE6B4C)); // FULL_AUTO
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 5, false); // No cover
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 13, true); // Always charge
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 52, true); // Proximity firing rate
                    break;
                case 1: // Balanced
                    Function.Call(Hash.SET_PED_ACCURACY, guard, 70);
                    Function.Call(Hash.SET_PED_COMBAT_ABILITY, guard, 2);
                    Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, guard, 2);
                    Function.Call(Hash.SET_PED_COMBAT_RANGE, guard, 1);
                    Function.Call(Hash.SET_PED_FIRING_PATTERN, guard, unchecked((uint)0xD6FF6D61)); // DEFAULT
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 5, true);  // Use cover
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 21, true); // Can flank
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 14, true); // Can investigate
                    break;
                case 2: // Defensive
                    Function.Call(Hash.SET_PED_ACCURACY, guard, 60);
                    Function.Call(Hash.SET_PED_COMBAT_ABILITY, guard, 1);
                    Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, guard, 1);
                    Function.Call(Hash.SET_PED_COMBAT_RANGE, guard, 0);
                    Function.Call(Hash.SET_PED_FIRING_PATTERN, guard, unchecked((uint)0xD6FF6D61)); // DEFAULT
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 5, true);  // Use cover
                    Function.Call(Hash.SET_PED_ALERTNESS, guard, 2);
                    break;
                case 3: // Sniper Overwatch
                    Function.Call(Hash.SET_PED_ACCURACY, guard, 95);
                    Function.Call(Hash.SET_PED_COMBAT_ABILITY, guard, 2);
                    Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, guard, 0); // Stationary
                    Function.Call(Hash.SET_PED_COMBAT_RANGE, guard, 2);    // Far
                    Function.Call(Hash.SET_PED_FIRING_PATTERN, guard, unchecked((uint)0x6D353C56)); // SINGLE_SHOT
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 5, true);  // Use cover
                    Function.Call(Hash.SET_PED_SEEING_RANGE, guard, 200f);
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 13, false); // Don't charge
                    break;
                case 4: // Close Protection
                    Function.Call(Hash.SET_PED_ACCURACY, guard, 75);
                    Function.Call(Hash.SET_PED_COMBAT_ABILITY, guard, 2);
                    Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, guard, 2);
                    Function.Call(Hash.SET_PED_COMBAT_RANGE, guard, 0);    // Near
                    Function.Call(Hash.SET_PED_FIRING_PATTERN, guard, unchecked((uint)0x914E786F)); // SHORT_BURSTS
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 5, true);  // Use cover
                    Function.Call(Hash.SET_PED_TARGET_LOSS_RESPONSE, guard, 0);     // Stay near, don't chase
                    break;
                case 5: // Flanker
                    Function.Call(Hash.SET_PED_ACCURACY, guard, 80);
                    Function.Call(Hash.SET_PED_COMBAT_ABILITY, guard, 2);
                    Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, guard, 3); // Aggressive movement
                    Function.Call(Hash.SET_PED_COMBAT_RANGE, guard, 1);    // Medium
                    Function.Call(Hash.SET_PED_FIRING_PATTERN, guard, unchecked((uint)0x7D864A85)); // BURST_IN_COVER
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 5, true);  // Use cover
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, guard, 21, true); // Can flank
                    break;
            }
        }

        void SetGuardCombatStyle(int styleIndex)
        {
            guardCombatStyleIndex = styleIndex;
            AssignGuardRoles();
            Tolk.Speak("Combat style: " + COMBAT_STYLE_NAMES[styleIndex] + ".");
        }

        // Auto-mix role assignment based on guard count
        // Specialist roles (Close Protection, Flanker, Sniper) are auto-assigned;
        // remaining guards get the user-selected base combat style.
        void AssignGuardRoles()
        {
            guardAssignedProfile.Clear();
            int guardCount = bodyguards.Count - 1; // exclude Butler at index 0
            if (guardCount <= 0) return;

            // Build role assignments: guard index (1-based) -> profile index
            // Priority: 1=Close Protection, 2=Flanker (if 3+), 3=Sniper Overwatch (if 5+)
            int[] roles = new int[guardCount];
            for (int i = 0; i < guardCount; i++)
                roles[i] = guardCombatStyleIndex; // default: user-selected base style

            if (guardCount >= 1) roles[0] = guardCombatStyleIndex; // single guard: just use base style
            if (guardCount >= 2) { roles[0] = 4; } // Close Protection
            if (guardCount >= 3) { roles[2] = 5; } // Flanker
            if (guardCount >= 5) { roles[3] = 3; } // Sniper Overwatch

            for (int i = 0; i < guardCount; i++)
            {
                int guardIndex = i + 1; // skip Butler
                if (guardIndex >= bodyguards.Count) break;
                Ped guard = bodyguards[guardIndex];
                if (guard != null && guard.Exists() && guard.IsAlive)
                {
                    ApplyCombatStyle(guard, roles[i]);
                    guardAssignedProfile[guard.Handle] = roles[i];
                    guardCombatState[guard.Handle] = GuardCombatState.Idle;
                }
            }
        }

        int GetGuardProfile(Ped guard)
        {
            if (guardAssignedProfile.ContainsKey(guard.Handle))
                return guardAssignedProfile[guard.Handle];
            return guardCombatStyleIndex; // fallback to base style
        }

        void SetGuardWeapon(WeaponHash weapon)
        {
            foreach (Ped guard in bodyguards)
            {
                if (guard != null && guard.Exists() && guard.IsAlive)
                {
                    guard.Weapons.Give(weapon, 9999, true, true);
                }
            }
            Tolk.Speak("All guards armed with " + GUARD_WEAPONS[guardWeaponIndex].name + ".");
        }

        void SetGuardArmor(int armorIndex)
        {
            guardArmorIndex = armorIndex;
            foreach (Ped guard in bodyguards)
            {
                if (guard != null && guard.Exists() && guard.IsAlive)
                {
                    Function.Call(Hash.SET_PED_ARMOUR, guard, ARMOR_LEVEL_VALUES[armorIndex]);
                }
            }
            Tolk.Speak("Guard armor: " + ARMOR_LEVEL_NAMES[armorIndex] + ".");
        }

        void ToggleGuardGodMode()
        {
            guardGodMode = !guardGodMode;
            foreach (Ped guard in bodyguards)
            {
                if (guard != null && guard.Exists())
                {
                    guard.IsInvincible = guardGodMode;
                    guard.CanBeDraggedOutOfVehicle = false; // Always false for guards
                    guard.CanBeKnockedOffBike = !guardGodMode;
                    guard.CanBeShotInVehicle = !guardGodMode;
                    guard.CanFlyThroughWindscreen = !guardGodMode;
                }
            }
            Tolk.Speak("Guard god mode " + (guardGodMode ? "on" : "off") + ".");
        }

        void GuardRecall()
        {
            int recalled = 0;
            for (int i = 0; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                if (guard != null && guard.Exists() && guard.IsAlive)
                {
                    float angle = Game.Player.Character.Heading + 180f + (i * 45f);
                    float rad = angle * (float)(Math.PI / 180.0);
                    GTA.Math.Vector3 offset = new GTA.Math.Vector3(
                        (float)Math.Sin(rad) * 2f, (float)Math.Cos(rad) * 2f, 0f);
                    guard.Position = Game.Player.Character.Position + offset;
                    recalled++;
                }
            }
            Tolk.Speak(recalled + " guards recalled.");
        }

        void GuardAttackTarget()
        {
            Entity target = Game.Player.TargetedEntity;
            if (target == null || !target.Exists())
            {
                Ped[] nearbyPeds = World.GetNearbyPeds(Game.Player.Character.Position, 100f);
                foreach (Ped p in nearbyPeds)
                {
                    if (p != null && p.Exists() && p.IsAlive && !bodyguards.Contains(p) && p != Game.Player.Character)
                    {
                        int rel = Function.Call<int>(Hash.GET_RELATIONSHIP_BETWEEN_PEDS, p, Game.Player.Character);
                        if (rel == 4 || rel == 5)
                        {
                            target = p;
                            break;
                        }
                    }
                }
            }

            if (target == null || !target.Exists())
            {
                Tolk.Speak("No target found.");
                return;
            }

            guardTaskMode = "attack";
            int attackCount = 0;
            for (int i = 0; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                if (guard != null && guard.Exists() && guard.IsAlive)
                {
                    // Don't pull butler out of driving/extraction tasks
                    if (i == 0 && (guardDriverActive || extractionInProgress))
                        continue;

                    guard.Task.ClearAllImmediately();
                    if (target is Ped targetPed)
                    {
                        guard.Task.FightAgainst(targetPed);
                    }
                    else
                    {
                        guard.Task.ShootAt(target.Position, 30000);
                    }
                    guard.AlwaysKeepTask = true;
                    guard.BlockPermanentEvents = true;
                    attackCount++;
                }
            }
            Tolk.Speak(attackCount + " guards attacking!");
        }

        void GuardCeaseFire()
        {
            guardTaskMode = "ceasefire";
            guardCurrentTarget.Clear();
            for (int i = 0; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                if (guard != null && guard.Exists() && guard.IsAlive)
                {
                    // Don't clear butler's driving/extraction task
                    if (i == 0 && (guardDriverActive || extractionInProgress))
                        continue;

                    guard.Task.ClearAll();
                    Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, guard, bodyguardGroupId);
                }
            }
            guardCurrentTarget.Clear();
            Tolk.Speak("Guards standing down.");
        }

        void GuardFollowPlayer()
        {
            guardTaskMode = "follow";
            guardsPatrolling = false;
            foreach (Ped guard in bodyguards)
            {
                if (guard != null && guard.Exists() && guard.IsAlive)
                {
                    guard.Task.ClearAll();
                    Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, guard, bodyguardGroupId);
                }
            }
            Tolk.Speak("Guards following.");
        }

        void GuardHoldPosition()
        {
            guardTaskMode = "hold";
            foreach (Ped guard in bodyguards)
            {
                if (guard != null && guard.Exists() && guard.IsAlive)
                {
                    guard.Task.ClearAllImmediately();
                    Function.Call(Hash.TASK_GUARD_CURRENT_POSITION, guard,
                        guard.Position.X, guard.Position.Y, guard.Position.Z,
                        guard.Heading, 15f, true);
                    guard.AlwaysKeepTask = true;
                    guard.BlockPermanentEvents = true;
                }
            }
            Tolk.Speak("Guards holding position.");
        }

        void GuardSendToWaypoint()
        {
            bool hasWaypoint = Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE);
            if (!hasWaypoint)
            {
                Tolk.Speak("No waypoint set.");
                return;
            }

            int waypointBlip = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
            GTA.Math.Vector3 waypointPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, waypointBlip);
            float groundZ = World.GetGroundHeight(new GTA.Math.Vector2(waypointPos.X, waypointPos.Y));
            if (groundZ > 0) waypointPos.Z = groundZ;

            guardTaskMode = "waypoint";
            int sent = 0;
            foreach (Ped guard in bodyguards)
            {
                if (guard != null && guard.Exists() && guard.IsAlive)
                {
                    guard.Task.ClearAllImmediately();
                    Function.Call(Hash.TASK_GO_TO_COORD_ANY_MEANS, guard,
                        waypointPos.X, waypointPos.Y, waypointPos.Z,
                        2.0f, 0, 0, 0, 0f);
                    guard.AlwaysKeepTask = true;
                    guard.BlockPermanentEvents = true;
                    sent++;
                }
            }

            float dist = World.GetDistance(Game.Player.Character.Position, waypointPos);
            Tolk.Speak(sent + " guards sent to waypoint. " + (int)dist + " meters.");
        }

        string GetGuardStatusText()
        {
            if (bodyguards.Count == 0) return "No guards active.";

            int alive = 0, dead = 0;
            string details = "";
            for (int i = 0; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                string label = (i == 0) ? "Butler" : "Guard " + i;

                if (guard == null || !guard.Exists())
                {
                    details += label + ": missing. ";
                    continue;
                }
                if (guard.IsDead)
                {
                    dead++;
                    details += label + ": dead. ";
                }
                else
                {
                    alive++;
                    float hp = (guard.MaxHealth > 0) ? (float)guard.Health / guard.MaxHealth * 100f : 0f;
                    details += label + ": " + (int)hp + " percent. ";
                    if (guard.IsInCombat) details += "In combat. ";
                }
            }

            return alive + " guards active, " + dead + " down. " + details;
        }

        // ============================================
        // STATUS MENU
        // ============================================

        private static readonly int[] STATUS_SECTION_HEADERS = { 0, 23, 57, 63, 80 };

        string HeadingToCompass(float heading)
        {
            heading = ((heading % 360f) + 360f) % 360f;
            string[] dirs = { "North", "Northeast", "East", "Southeast", "South", "Southwest", "West", "Northwest" };
            int idx = (int)Math.Round(heading / 45.0) % 8;
            return dirs[idx];
        }

        string GetVehicleClassName(int classId)
        {
            switch (classId)
            {
                case 0: return "Compacts";
                case 1: return "Sedans";
                case 2: return "SUVs";
                case 3: return "Coupes";
                case 4: return "Muscle";
                case 5: return "Sports Classics";
                case 6: return "Sports";
                case 7: return "Super";
                case 8: return "Motorcycles";
                case 9: return "Off-Road";
                case 10: return "Industrial";
                case 11: return "Utility";
                case 12: return "Vans";
                case 13: return "Cycles";
                case 14: return "Boats";
                case 15: return "Helicopters";
                case 16: return "Planes";
                case 17: return "Service";
                case 18: return "Emergency";
                case 19: return "Military";
                case 20: return "Commercial";
                case 21: return "Trains";
                case 22: return "Open Wheel";
                default: return "Unknown";
            }
        }

        string GetPlayerStateString()
        {
            Ped p = Game.Player.Character;
            if (p.IsInVehicle()) return "In Vehicle";
            if (p.IsSwimmingUnderWater) return "Swimming Underwater";
            if (p.IsSwimming) return "Swimming";
            if (p.IsFalling) return "Falling";
            if (p.IsRagdoll) return "Ragdoll";
            if (Game.Player.IsClimbing) return "Climbing";
            if (p.IsOnFire) return "On Fire";
            if (Game.Player.IsAiming) return "Aiming";
            if (p.IsSprinting) return "Sprinting";
            if (p.IsRunning) return "Running";
            if (Function.Call<bool>(Hash.IS_PED_WALKING, p)) return "Walking";
            return "Standing";
        }

        string GetVehicleTypeString(Vehicle veh)
        {
            if (veh.IsHelicopter) return "Helicopter";
            if (veh.IsPlane) return "Plane";
            if (veh.IsBicycle) return "Bicycle";
            if (veh.IsMotorcycle) return "Motorcycle";
            if (veh.IsBoat) return "Boat";
            if (veh.IsTrain) return "Train";
            if (veh.IsSubmarine) return "Submarine";
            if (veh.IsAmphibious) return "Amphibious";
            if (veh.IsTrailer) return "Trailer";
            if (veh.IsAutomobile) return "Automobile";
            return "Vehicle";
        }

        string GetStatusMenuText(int index)
        {
            Ped player = Game.Player.Character;
            bool inVehicle = player.IsInVehicle();
            Vehicle veh = inVehicle ? player.CurrentVehicle : null;

            switch (index)
            {
                // ---- PLAYER STATUS ----
                case 0: return "Player Status";
                case 1: return "Health: " + player.Health + " of " + player.MaxHealth;
                case 2: return "Armor: " + player.Armor;
                case 3:
                    int wanted = Game.Player.WantedLevel;
                    return "Wanted Level: " + (wanted > 0 ? wanted + " star" + (wanted > 1 ? "s" : "") : "None");
                case 4: return "Money: $" + Game.Player.Money.ToString("N0");
                case 5:
                    float stamina = Function.Call<float>(Hash.GET_PLAYER_SPRINT_STAMINA_REMAINING, Game.Player);
                    return "Sprint Stamina: " + (int)stamina + "%";
                case 6:
                    float underwaterTime = Game.Player.RemainingUnderwaterTime;
                    return "Remaining Underwater Time: " + (int)underwaterTime + " seconds";
                case 7: return "Player State: " + GetPlayerStateString();
                case 8:
                    float heading = player.Heading;
                    return "Heading: " + (int)heading + " degrees, facing " + HeadingToCompass(heading);
                case 9:
                    GTA.Math.Vector3 pos = player.Position;
                    return "Position: X " + pos.X.ToString("F1") + ", Y " + pos.Y.ToString("F1") + ", Z " + pos.Z.ToString("F1");
                case 10: return "Height Above Ground: " + player.HeightAboveGround.ToString("F1") + " meters";
                case 11:
                    float speed = player.Velocity.Length() * 2.23694f;
                    return "Speed: " + (speed < 0.5f ? "Stationary" : speed.ToString("F1") + " mph");
                case 12:
                    Weapon wep = player.Weapons.Current;
                    string weapName = wep.Hash.ToString();
                    if (hashes.ContainsKey(weapName))
                        weapName = hashes[weapName];
                    return "Current Weapon: " + weapName;
                case 13:
                {
                    Weapon w = player.Weapons.Current;
                    if (w.Hash == WeaponHash.Unarmed)
                        return "Ammo: Unarmed";
                    OutputArgument outAmmo = new OutputArgument();
                    bool success = Function.Call<bool>(Hash.GET_AMMO_IN_CLIP, player, (int)w.Hash, outAmmo);
                    int clipAmmo = success ? outAmmo.GetResult<int>() : 0;
                    int totalAmmo = Function.Call<int>(Hash.GET_AMMO_IN_PED_WEAPON, player, (int)w.Hash);
                    int reserve = Math.Max(0, totalAmmo - clipAmmo);
                    return "Ammo: " + clipAmmo + " in clip, " + reserve + " reserve";
                }
                case 14: return "In Vehicle: " + (inVehicle ? "Yes" : "No");
                case 15: return "In Water: " + (player.IsInWater ? "Yes" : "No");
                case 16: return "In Air: " + (player.IsInAir ? "Yes" : "No");
                case 17: return "On Fire: " + (player.IsOnFire ? "Yes" : "No");
                case 18:
                    float sub = player.SubmersionLevel;
                    return "Submersion Level: " + (int)(sub * 100) + "%";
                case 19:
                    int interior = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, player);
                    return "In Interior: " + (interior != 0 ? "Yes" : "No");
                case 20:
                    if (Game.Player.IsSpecialAbilityActive) return "Special Ability: Active";
                    if (Game.Player.IsSpecialAbilityEnabled) return "Special Ability: Ready";
                    return "Special Ability: Disabled";
                case 21: return "Aiming: " + (Game.Player.IsAiming ? "Yes" : "No");
                case 22:
                    if (!Game.Player.IsTargetingAnything) return "Targeting: No";
                    Entity targeted = Game.Player.TargetedEntity;
                    if (targeted == null || !targeted.Exists()) return "Targeting: Yes";
                    if (targeted is Ped) return "Targeting: Pedestrian";
                    if (targeted is Vehicle) return "Targeting: Vehicle";
                    return "Targeting: Entity";

                // ---- VEHICLE STATUS ----
                case 23: return "Vehicle Status";
                case 24:
                    if (!inVehicle || veh == null) return "Vehicle: Not in vehicle";
                    string vName = veh.LocalizedName;
                    if (string.IsNullOrEmpty(vName) || vName == "NULL") vName = veh.DisplayName;
                    return "Vehicle: " + vName;
                case 25:
                    if (!inVehicle || veh == null) return "Vehicle Type: Not in vehicle";
                    return "Vehicle Type: " + GetVehicleTypeString(veh);
                case 26:
                    if (!inVehicle || veh == null) return "Vehicle Class: Not in vehicle";
                    int vClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, veh);
                    return "Vehicle Class: " + GetVehicleClassName(vClass);
                case 27:
                    if (!inVehicle || veh == null) return "Speed mph: Not in vehicle";
                    return "Speed: " + (int)Math.Round(veh.Speed * 2.23694f) + " mph";
                case 28:
                    if (!inVehicle || veh == null) return "Speed km/h: Not in vehicle";
                    return "Speed: " + (int)Math.Round(veh.Speed * 3.6f) + " km/h";
                case 29:
                    if (!inVehicle || veh == null) return "RPM: Not in vehicle";
                    return "RPM: " + (veh.CurrentRPM * 10000).ToString("F0");
                case 30:
                    if (!inVehicle || veh == null) return "Current Gear: Not in vehicle";
                    return "Current Gear: " + veh.CurrentGear;
                case 31:
                    if (!inVehicle || veh == null) return "Next Gear: Not in vehicle";
                    return "Next Gear: " + veh.NextGear;
                case 32:
                    if (!inVehicle || veh == null) return "Total Gears: Not in vehicle";
                    return "Total Gears: " + veh.HighGear;
                case 33:
                    if (!inVehicle || veh == null) return "Throttle: Not in vehicle";
                    return "Throttle: " + (int)(veh.Throttle * 100) + "%";
                case 34:
                    if (!inVehicle || veh == null) return "Brake Power: Not in vehicle";
                    return "Brake Power: " + (int)(veh.BrakePower * 100) + "%";
                case 35:
                    if (!inVehicle || veh == null) return "Clutch: Not in vehicle";
                    return "Clutch: " + (int)(veh.Clutch * 100) + "%";
                case 36:
                    if (!inVehicle || veh == null) return "Turbo: Not in vehicle";
                    return "Turbo: " + veh.Turbo.ToString("F2");
                case 37:
                    if (!inVehicle || veh == null) return "Acceleration: Not in vehicle";
                    return "Acceleration: " + veh.Acceleration.ToString("F2");
                case 38:
                    if (!inVehicle || veh == null) return "Steering Angle: Not in vehicle";
                    return "Steering Angle: " + veh.SteeringAngle.ToString("F1") + " degrees";
                case 39:
                    if (!inVehicle || veh == null) return "Engine Health: Not in vehicle";
                    return "Engine Health: " + (int)veh.EngineHealth + " of 1000";
                case 40:
                    if (!inVehicle || veh == null) return "Body Health: Not in vehicle";
                    return "Body Health: " + (int)veh.BodyHealth + " of 1000";
                case 41:
                    if (!inVehicle || veh == null) return "Fuel Tank Health: Not in vehicle";
                    return "Fuel Tank Health: " + (int)veh.PetrolTankHealth + " of 1000";
                case 42:
                    if (!inVehicle || veh == null) return "Engine Temperature: Not in vehicle";
                    return "Engine Temperature: " + veh.EngineTemperature.ToString("F1");
                case 43:
                    if (!inVehicle || veh == null) return "Oil Level: Not in vehicle";
                    return "Oil Level: " + veh.OilLevel.ToString("F2");
                case 44:
                    if (!inVehicle || veh == null) return "Fuel Level: Not in vehicle";
                    return "Fuel Level: " + veh.FuelLevel.ToString("F1");
                case 45:
                    if (!inVehicle || veh == null) return "Engine: Not in vehicle";
                    if (veh.IsEngineRunning) return "Engine: Running";
                    if (veh.IsEngineStarting) return "Engine: Starting";
                    return "Engine: Off";
                case 46:
                    if (!inVehicle || veh == null) return "Vehicle Heading: Not in vehicle";
                    float vHeading = veh.Heading;
                    return "Vehicle Heading: " + (int)vHeading + " degrees, facing " + HeadingToCompass(vHeading);
                case 47:
                    if (!inVehicle || veh == null) return "Altitude: Not in vehicle";
                    return "Altitude: " + (int)veh.Position.Z + " meters";
                case 48:
                    if (!inVehicle || veh == null) return "Height Above Ground: Not in vehicle";
                    return "Height Above Ground: " + veh.HeightAboveGround.ToString("F1") + " meters";
                case 49:
                    if (!inVehicle || veh == null) return "On All Wheels: Not in vehicle";
                    return "On All Wheels: " + (veh.IsOnAllWheels ? "Yes" : "No");
                case 50:
                    if (!inVehicle || veh == null) return "Lights: Not in vehicle";
                    if (veh.AreHighBeamsOn) return "Lights: High Beams";
                    if (veh.AreLightsOn) return "Lights: On";
                    return "Lights: Off";
                case 51:
                    if (!inVehicle || veh == null) return "Interior Light: Not in vehicle";
                    return "Interior Light: " + (veh.IsInteriorLightOn ? "On" : "Off");
                case 52:
                    if (!inVehicle || veh == null) return "Indicators: Not in vehicle";
                    bool left = veh.IsLeftIndicatorLightOn;
                    bool right = veh.IsRightIndicatorLightOn;
                    if (left && right) return "Indicators: Both";
                    if (left) return "Indicators: Left";
                    if (right) return "Indicators: Right";
                    return "Indicators: Off";
                case 53:
                    if (!inVehicle || veh == null) return "Siren: Not in vehicle";
                    if (!veh.HasSiren) return "Siren: Not available";
                    if (veh.IsSirenActive) return "Siren: Active";
                    return "Siren: Off";
                case 54:
                    if (!inVehicle || veh == null) return "Radio: Not in vehicle";
                    return "Radio: " + Game.RadioStation.ToString().Replace("Radio", "").Replace("_", " ").Trim();
                case 55:
                {
                    if (!inVehicle || veh == null) return "Passengers: Not in vehicle";
                    int maxPass = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, veh);
                    int occupied = 0;
                    for (int seat = 0; seat < maxPass; seat++)
                    {
                        if (!Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, veh, seat))
                            occupied++;
                    }
                    return "Passengers: " + occupied + " of " + maxPass;
                }
                case 56:
                    if (!inVehicle || veh == null) return "Stolen: Not in vehicle";
                    return "Stolen: " + (veh.IsStolen ? "Yes" : "No");

                // ---- AIRCRAFT STATUS ----
                case 57: return "Aircraft Status";
                case 58:
                    if (!inVehicle || veh == null || !veh.IsAircraft) return "Heli Engine Health: Not in aircraft";
                    return "Heli Engine Health: " + (int)veh.HeliEngineHealth;
                case 59:
                    if (!inVehicle || veh == null || !veh.IsAircraft) return "Main Rotor Health: Not in aircraft";
                    return "Main Rotor Health: " + (int)veh.HeliMainRotorHealth;
                case 60:
                    if (!inVehicle || veh == null || !veh.IsAircraft) return "Tail Rotor Health: Not in aircraft";
                    return "Tail Rotor Health: " + (int)veh.HeliTailRotorHealth;
                case 61:
                    if (!inVehicle || veh == null || !veh.IsAircraft) return "Blade Speed: Not in aircraft";
                    return "Blade Speed: " + veh.HeliBladesSpeed.ToString("F2");
                case 62:
                {
                    if (!inVehicle || veh == null || !veh.IsAircraft) return "Landing Gear: Not in aircraft";
                    int gearState = Function.Call<int>(Hash.GET_LANDING_GEAR_STATE, veh);
                    if (gearState == 0) return "Landing Gear: Deployed";
                    if (gearState == 1) return "Landing Gear: Retracting";
                    if (gearState == 3) return "Landing Gear: Deploying";
                    return "Landing Gear: Retracted";
                }

                // ---- WORLD AND ENVIRONMENT ----
                case 63: return "World and Environment";
                case 64:
                    int hours = Function.Call<int>(Hash.GET_CLOCK_HOURS);
                    int minutes = Function.Call<int>(Hash.GET_CLOCK_MINUTES);
                    return "Game Time: " + hours.ToString("D2") + ":" + minutes.ToString("D2");
                case 65:
                {
                    int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
                    int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
                    int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
                    string[] monthNames = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
                    string mName = (month >= 0 && month < 12) ? monthNames[month] : "Unknown";
                    return "Game Date: " + mName + " " + day + ", " + year;
                }
                case 66:
                {
                    int dow = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_WEEK);
                    string[] dayNames = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
                    return "Day of Week: " + (dow >= 0 && dow < 7 ? dayNames[dow] : "Unknown");
                }
                case 67: return "Weather: " + World.Weather.ToString();
                case 68: return "Next Weather: " + World.NextWeather.ToString();
                case 69:
                {
                    GTA.Math.Vector3 pPos = player.Position;
                    string street = World.GetStreetName(pPos);
                    return "Street: " + (string.IsNullOrEmpty(street) ? "Unknown" : street);
                }
                case 70:
                {
                    GTA.Math.Vector3 pPos = player.Position;
                    string zone = World.GetZoneLocalizedName(pPos);
                    return "Zone: " + (string.IsNullOrEmpty(zone) ? "Unknown" : zone);
                }
                case 71: return "Game Speed: " + World.MillisecondsPerGameMinute + " ms per game minute";
                case 72: return "Gravity Level: " + World.GravityLevel.ToString("F2");
                case 73: return "Nearby Peds: " + World.PedCount;
                case 74: return "Nearby Vehicles: " + World.VehicleCount;
                case 75: return "FPS: " + Game.FPS;
                case 76: return "Frame Time: " + (Game.LastFrameTime * 1000f).ToString("F1") + " ms";
                case 77: return "Game Timer: " + (Game.GameTime / 1000) + " seconds";
                case 78: return "Night Vision: " + (Game.IsNightVisionActive ? "On" : "Off");
                case 79: return "Thermal Vision: " + (Game.IsThermalVisionActive ? "On" : "Off");

                // ---- NAVIGATION ----
                case 80: return "Navigation";
                case 81:
                {
                    bool wpActive = Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE);
                    if (!wpActive) return "Waypoint: Not set";
                    int wpBlip = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
                    GTA.Math.Vector3 wpPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, wpBlip);
                    float dist = World.GetDistance(player.Position, wpPos);
                    return "Waypoint: Active, " + (int)dist + " meters away";
                }
                case 82:
                    return "Compass: " + HeadingToCompass(player.Heading);
                case 83:
                    return "Tracked Enemies: " + trackedEnemies.Count;
                case 84:
                {
                    GTA.Math.Vector3 pPos = player.Position;
                    string st = World.GetStreetName(pPos);
                    string zn = World.GetZoneLocalizedName(pPos);
                    return "Location: " + (string.IsNullOrEmpty(st) ? "" : st + ", ") + (string.IsNullOrEmpty(zn) ? "Unknown" : zn);
                }

                default: return "Unknown status item";
            }
        }

        void TickStatusMonitor()
        {
            if (statusMonitoredItems.Count == 0) return;
            if (DateTime.Now.Ticks - statusMonitorTicks < 100000000) return; // 10 seconds
            statusMonitorTicks = DateTime.Now.Ticks;

            // Build a sorted list of monitored indices for stable rotation
            List<int> monitored = new List<int>(statusMonitoredItems);
            monitored.Sort();

            if (monitored.Count == 0) return;

            statusMonitorRotation = statusMonitorRotation % monitored.Count;
            int itemIndex = monitored[statusMonitorRotation];
            Tolk.Speak(GetStatusMenuText(itemIndex), true);
            statusMonitorRotation = (statusMonitorRotation + 1) % monitored.Count;
        }

        string GetBodyguardMenuText(int index)
        {
            int aliveCount = 0;
            foreach (Ped g in bodyguards)
            {
                if (g != null && g.Exists() && g.IsAlive) aliveCount++;
            }

            switch (index)
            {
                case 0: return "Toggle Bodyguard System: " + (bodyguardSystemEnabled ? "On" : "Off");
                case 1: return "Spawn Primary Guard (Butler)";
                case 2: return "Spawn Additional Guard. " + bodyguards.Count + " of 7 active.";
                case 3: return "Dismiss Last Guard. " + bodyguards.Count + " active.";
                case 4: return "Dismiss All Guards";
                case 5: return "Recall All Guards";
                case 6: return "Ground Extraction";
                case 7: return "Helicopter Extraction";
                case 8: return "Guard Model: " + GUARD_MODELS[guardModelIndex].name;
                case 9: return "Weapon (All Guards): " + GUARD_WEAPONS[guardWeaponIndex].name;
                case 10: return "Reload Weapon Config";
                case 11: return "Combat Style: " + COMBAT_STYLE_NAMES[guardCombatStyleIndex];
                case 12: return "Formation: " + FORMATION_TYPES[guardFormationIndex].name;
                case 13: return "Formation Spacing: " + FORMATION_SPACING_OPTIONS[guardFormationSpacingIndex];
                case 14: return "Guard God Mode: " + (guardGodMode ? "On" : "Off");
                case 15: return "Auto-Respawn: " + (guardAutoRespawn ? "On" : "Off");
                case 16: return "Guard Armor: " + ARMOR_LEVEL_NAMES[guardArmorIndex];
                case 17: return "Auto-Patrol: " + (guardAutoPatrol ? "On" : "Off");
                case 18: return "Guard Callouts: " + (guardCalloutsEnabled ? "On" : "Off");
                case 19: return "Butler Beacon: " + (butlerBeaconEnabled ? "On" : "Off");
                case 20: return "Butler POI Narration: " + (butlerPOINarrationEnabled ? "On" : "Off");
                case 21: return "Send Guards to Waypoint";
                case 22: return "Hold Position" + (guardTaskMode == "hold" ? " (active)" : "");
                case 23: return "Follow Me" + (guardTaskMode == "follow" ? " (active)" : "");
                case 24: return "Attack My Target";
                case 25: return "Cease Fire";
                case 26: return "Guard Status. " + aliveCount + " alive.";
                case 27: return "Ground Extraction Distance: " + GROUND_EXTRACTION_DISTANCE_NAMES[groundExtractionDistanceIndex];
                case 28: return "Helicopter Extraction Distance: " + HELI_EXTRACTION_DISTANCE_NAMES[heliExtractionDistanceIndex];
                case 29: return "Land";
                case 30: return "Park at Nearest Safe Spot";
                case 31: return "Proactive Detection: " + (proactiveThreatDetection ? "On" : "Off");
                case 32: return "Armed Ped Alert: " + (armedPedAlert ? "On" : "Off");
                default: return bodyguardMenu[index];
            }
        }

        void HandleBodyguardMenuSelect(int index)
        {
            switch (index)
            {
                case 0: // Toggle system
                    bodyguardSystemEnabled = !bodyguardSystemEnabled;
                    if (bodyguardSystemEnabled)
                    {
                        SetupGuardGroup();
                        LoadGuardWeaponConfig();
                        playerStationaryPos = Game.Player.Character.Position;
                        playerStationaryTicks = DateTime.Now.Ticks;
                        Tolk.Speak("Bodyguard system enabled.");
                    }
                    else
                    {
                        DismissAllGuards();
                        Tolk.Speak("Bodyguard system disabled.");
                    }
                    break;

                case 1: // Spawn primary guard
                    if (!bodyguardSystemEnabled) { Tolk.Speak("Enable bodyguard system first."); break; }
                    SpawnPrimaryGuard();
                    break;

                case 2: // Spawn additional guard
                    if (!bodyguardSystemEnabled) { Tolk.Speak("Enable bodyguard system first."); break; }
                    SpawnAdditionalGuard();
                    break;

                case 3: // Dismiss last guard
                    if (bodyguards.Count == 0) { Tolk.Speak("No guards to dismiss."); break; }
                    DismissGuard(bodyguards.Count - 1);
                    break;

                case 4: // Dismiss all
                    DismissAllGuards();
                    break;

                case 5: // Recall all
                    GuardRecall();
                    break;

                case 6: // Ground extraction
                    ButlerGroundExtraction();
                    break;

                case 7: // Helicopter extraction
                    ButlerHelicopterExtraction();
                    break;

                case 8: // Cycle guard model
                    guardModelIndex = (guardModelIndex + 1) % GUARD_MODELS.Length;
                    Tolk.Speak("Guard model: " + GUARD_MODELS[guardModelIndex].name + ". New guards will use this model.");
                    break;

                case 9: // Cycle weapon (all guards)
                    guardWeaponIndex = (guardWeaponIndex + 1) % GUARD_WEAPONS.Length;
                    SetGuardWeapon(GUARD_WEAPONS[guardWeaponIndex].hash);
                    break;

                case 10: // Reload weapon config
                    ReloadGuardWeaponsFromConfig();
                    break;

                case 11: // Cycle combat style
                    guardCombatStyleIndex = (guardCombatStyleIndex + 1) % 6;
                    SetGuardCombatStyle(guardCombatStyleIndex);
                    break;

                case 12: // Cycle formation
                    guardFormationIndex = (guardFormationIndex + 1) % FORMATION_TYPES.Length;
                    if (bodyguardGroupId >= 0 && FORMATION_TYPES[guardFormationIndex].id >= 0)
                    {
                        // Built-in formation
                        Function.Call(Hash.SET_GROUP_FORMATION, bodyguardGroupId, FORMATION_TYPES[guardFormationIndex].id);
                    }
                    else if (FORMATION_TYPES[guardFormationIndex].id < 0)
                    {
                        // Custom formation - will be applied by TickCustomFormation
                        guardCustomFormationTicks = 0; // Force immediate update
                    }
                    Tolk.Speak("Formation: " + FORMATION_TYPES[guardFormationIndex].name + ".");
                    break;

                case 13: // Formation spacing (cycle like detection radius)
                    guardFormationSpacingIndex = (guardFormationSpacingIndex + 1) % FORMATION_SPACING_OPTIONS.Length;
                    float spacing = FORMATION_SPACING_OPTIONS[guardFormationSpacingIndex];
                    guardFormationSpacing = spacing;
                    if (bodyguardGroupId >= 0)
                    {
                        Function.Call(Hash.SET_GROUP_FORMATION_SPACING, bodyguardGroupId, spacing, spacing, spacing);
                    }
                    Tolk.Speak("Formation spacing: " + spacing + ".");
                    break;

                case 14: // God mode
                    ToggleGuardGodMode();
                    break;

                case 15: // Auto-respawn
                    guardAutoRespawn = !guardAutoRespawn;
                    Tolk.Speak("Auto-respawn " + (guardAutoRespawn ? "on" : "off") + ".");
                    break;

                case 16: // Guard armor
                    guardArmorIndex = (guardArmorIndex + 1) % ARMOR_LEVEL_NAMES.Length;
                    SetGuardArmor(guardArmorIndex);
                    break;

                case 17: // Auto-patrol
                    guardAutoPatrol = !guardAutoPatrol;
                    Tolk.Speak("Auto-patrol " + (guardAutoPatrol ? "on" : "off") + ".");
                    break;

                case 18: // Guard callouts
                    guardCalloutsEnabled = !guardCalloutsEnabled;
                    Tolk.Speak("Guard callouts " + (guardCalloutsEnabled ? "on" : "off") + ".");
                    break;

                case 19: // Butler beacon
                    butlerBeaconEnabled = !butlerBeaconEnabled;
                    Tolk.Speak("Butler beacon " + (butlerBeaconEnabled ? "on" : "off") + ".");
                    break;

                case 20: // Butler POI narration
                    butlerPOINarrationEnabled = !butlerPOINarrationEnabled;
                    Tolk.Speak("Butler POI narration " + (butlerPOINarrationEnabled ? "on" : "off") + ".");
                    break;

                case 21: // Send to waypoint
                    GuardSendToWaypoint();
                    break;

                case 22: // Hold position
                    GuardHoldPosition();
                    break;

                case 23: // Follow me
                    GuardFollowPlayer();
                    break;

                case 24: // Attack my target
                    GuardAttackTarget();
                    break;

                case 25: // Cease fire
                    GuardCeaseFire();
                    break;

                case 26: // Guard status
                    Tolk.Speak(GetGuardStatusText());
                    break;

                case 27: // Ground extraction distance
                    groundExtractionDistanceIndex = (groundExtractionDistanceIndex + 1) % GROUND_EXTRACTION_DISTANCE_NAMES.Length;
                    Tolk.Speak("Ground extraction distance: " + GROUND_EXTRACTION_DISTANCE_NAMES[groundExtractionDistanceIndex] + ".");
                    break;

                case 28: // Helicopter extraction distance
                    heliExtractionDistanceIndex = (heliExtractionDistanceIndex + 1) % HELI_EXTRACTION_DISTANCE_NAMES.Length;
                    Tolk.Speak("Helicopter extraction distance: " + HELI_EXTRACTION_DISTANCE_NAMES[heliExtractionDistanceIndex] + ".");
                    break;

                case 29: // Land
                    ExecuteLandCommand();
                    break;

                case 30: // Park at Nearest Safe Spot
                    ExecuteParkCommand();
                    break;

                case 31: // Proactive detection toggle
                    proactiveThreatDetection = !proactiveThreatDetection;
                    Tolk.Speak("Proactive detection " + (proactiveThreatDetection ? "on" : "off") + ".");
                    break;

                case 32: // Armed ped alert toggle
                    armedPedAlert = !armedPedAlert;
                    Tolk.Speak("Armed ped alert " + (armedPedAlert ? "on" : "off") + ".");
                    break;
            }
        }

        void ExecuteLandCommand()
        {
            if (bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive &&
                Game.Player.Character.IsInVehicle())
            {
                Vehicle veh = Game.Player.Character.CurrentVehicle;
                int vehClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, veh);
                if (vehClass == 15 && guardDriverActive)
                {
                    isAutodriving = false;
                    autodriveWanderMode = false;

                    extractionVehicle = veh;
                    extractionInProgress = true;
                    extractionIsHeli = true;
                    heliLandingPhase = true;
                    heliManualLanding = true;
                    heliLandingSearchRadius = 20f;
                    heliLandingSearchPointIndex = 0;
                    heliLandingTargetActive = false;
                    heliLandingTarget = GTA.Math.Vector3.Zero;

                    Function.Call(Hash.REMOVE_PED_FROM_GROUP, bodyguards[0]);
                    Tolk.Speak("Landing.");
                }
                else
                {
                    Tolk.Speak("Not in a helicopter with butler piloting.");
                }
            }
            else
            {
                Tolk.Speak("Not in a vehicle.");
            }
        }

        void ExecuteParkCommand()
        {
            if (!Game.Player.Character.IsInVehicle())
            {
                Tolk.Speak("Not in a vehicle.");
                return;
            }

            Vehicle veh = Game.Player.Character.CurrentVehicle;
            int vehClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, veh);
            if (vehClass == 15 || vehClass == 16)
            {
                Tolk.Speak("Cannot park an aircraft. Use Land instead.");
                return;
            }

            Ped driver = (guardDriverActive && bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive)
                ? bodyguards[0]
                : Game.Player.Character;

            GTA.Math.Vector3 pos = Game.Player.Character.Position;

            // Smart parking: prefer a real garage / gas-station forecourt within
            // 250 m before falling back to the nearest road node (which on a
            // freeway is the breakdown shoulder).
            GTA.Math.Vector3 parkPos = GTA.Math.Vector3.Zero;
            string parkPlaceName = null;
            MapDb.Service nearbyService = MapDb.FindBestParkingService(pos, 250f);
            if (nearbyService != null)
            {
                parkPos = new GTA.Math.Vector3(nearbyService.x, nearbyService.y, nearbyService.z);
                parkPlaceName = nearbyService.name;
            }

            if (parkPos == GTA.Math.Vector3.Zero)
            {
                OutputArgument outPos = new OutputArgument();
                OutputArgument outHeading = new OutputArgument();
                OutputArgument outLanes = new OutputArgument();
                Function.Call(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                    pos.X, pos.Y, pos.Z,
                    0, outPos, outHeading, outLanes, 1, 3.0f, 0f);
                parkPos = outPos.GetResult<GTA.Math.Vector3>();
            }

            if (parkPos == GTA.Math.Vector3.Zero)
            {
                Tolk.Speak("No safe parking spot found.");
                return;
            }

            isAutodriving = false;
            autodriveWanderMode = false;

            parkingInProgress = true;
            parkingDestination = parkPos;
            int style = GetDrivingStyleFromFlags();
            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                driver, veh,
                parkPos.X, parkPos.Y, parkPos.Z,
                4.4704f, style, 5f);  // 10 mph, 5m stop distance
            Tolk.Speak(parkPlaceName != null
                ? "Parking at " + parkPlaceName + "."
                : "Parking.");
        }

        void ExecuteHitchTrailerCommand()
        {
            if (!Game.Player.Character.IsInVehicle())
            {
                Tolk.Speak("Not in a vehicle.");
                return;
            }

            Vehicle currentVeh = Game.Player.Character.CurrentVehicle;

            // Check if vehicle has a trailer hitch bone
            int hitchBone = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, currentVeh, "attach_female");
            if (hitchBone == -1)
            {
                Tolk.Speak("This vehicle does not have a trailer hitch.");
                return;
            }

            // Find nearest trailer within 20 meters
            Vehicle[] nearbyVehicles = World.GetNearbyVehicles(Game.Player.Character.Position, 20f);
            Vehicle nearestTrailer = null;
            float nearestDist = float.MaxValue;

            foreach (Vehicle v in nearbyVehicles)
            {
                if (v == currentVeh) continue;
                // Check if vehicle has a trailer attach point (attach_male bone)
                int trailerBone = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, v, "attach_male");
                if (trailerBone == -1) continue;

                float dist = World.GetDistance(Game.Player.Character.Position, v.Position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestTrailer = v;
                }
            }

            if (nearestTrailer == null)
            {
                Tolk.Speak("No trailer found within 20 meters.");
                return;
            }

            // Attach trailer to vehicle
            Function.Call(Hash.ATTACH_VEHICLE_TO_TRAILER, currentVeh, nearestTrailer, 50f);
            Tolk.Speak("Trailer attached.");
        }

        // Bodyguard system tick (called from onTick)
        void TickBodyguardSystem()
        {
            if (!bodyguardSystemEnabled || bodyguards.Count == 0) return;

            TickGuardHealthMonitor();
            TickGuardPersistence();
            TickGuardTaskValidation();
            TickGuardDriverLogic();
            TickButlerVehicleEntry();
            TickProactiveThreatScan();
            TickAutoEngagement();
            TickCombatTactics();
            TickButlerEvasion();
            if (guardAutoPatrol) TickGuardPatrol();
            TickConvoyManagement();
            TickCustomFormation();
            if (guardCalloutsEnabled) TickGuardCallouts();
            if (butlerBeaconEnabled) TickButlerBeacon();
            if (butlerPOINarrationEnabled) TickButlerPOINarration();
            if (extractionInProgress) TickExtractionMonitor();
        }

        void TickGuardHealthMonitor()
        {
            if (DateTime.Now.Ticks - guardStatusCheckTicks < 30000000) return; // 3 seconds
            guardStatusCheckTicks = DateTime.Now.Ticks;

            for (int i = 0; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                if (guard == null || !guard.Exists()) continue;

                string label = (i == 0) ? "Butler" : "Guard " + i;

                if (guard.IsDead && !guardDeathAnnounced[i])
                {
                    guardDeathAnnounced[i] = true;
                    Tolk.Speak(label + " killed!");
                    if (i == 0) guardDriverActive = false;

                    if (guardAutoRespawn)
                    {
                        guardRespawnTicks[i] = DateTime.Now.Ticks;
                    }
                    continue;
                }

                // Auto-respawn check
                if (guard.IsDead && guardDeathAnnounced[i] && guardAutoRespawn)
                {
                    if (DateTime.Now.Ticks - guardRespawnTicks[i] > 100000000) // 10 seconds
                    {
                        guard.Delete();

                        Model model = new Model(GUARD_MODELS[guardModelIndex].hash);
                        model.Request(5000);
                        if (!model.IsLoaded) continue;

                        // Determine spawn position — use ground road node when player is in aircraft
                        GTA.Math.Vector3 spawnPos;
                        bool playerInAircraft = false;
                        if (i > 0 && Game.Player.Character.IsInVehicle())
                        {
                            Vehicle pVeh = Game.Player.Character.CurrentVehicle;
                            if (pVeh != null)
                            {
                                int vClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, pVeh);
                                playerInAircraft = (vClass == 15 || vClass == 16);
                            }
                        }

                        if (playerInAircraft)
                        {
                            // Spawn on nearest road node below the player's X/Y position
                            GTA.Math.Vector3 pp = Game.Player.Character.Position;
                            OutputArgument outRoadPos = new OutputArgument();
                            OutputArgument outRoadH = new OutputArgument();
                            OutputArgument outRoadL = new OutputArgument();
                            Function.Call(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                                pp.X, pp.Y, pp.Z, 0, outRoadPos, outRoadH, outRoadL, 1, 3.0f, 0f);
                            spawnPos = outRoadPos.GetResult<GTA.Math.Vector3>();
                            if (spawnPos == GTA.Math.Vector3.Zero)
                                spawnPos = new GTA.Math.Vector3(pp.X, pp.Y, 0f);
                        }
                        else
                        {
                            spawnPos = Game.Player.Character.Position + Game.Player.Character.ForwardVector * 2f;
                        }

                        Ped newGuard = World.CreatePed(model, spawnPos, Game.Player.Character.Heading);
                        model.MarkAsNoLongerNeeded();

                        if (newGuard != null)
                        {
                            bodyguards[i] = newGuard;
                            SetupGuardAttributes(newGuard, i);
                            SetupGuardRelationship(newGuard);
                            Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, newGuard, bodyguardGroupId);
                            guardDeathAnnounced[i] = false;
                            for (int t = 0; t < 3; t++) guardHealthWarnings[i, t] = false;
                            Tolk.Speak(label + " respawned.");

                            // If helicopter ground convoy is active, add respawned guard to it
                            if (playerInAircraft && heliGroundConvoyActive
                                && heliGroundConvoyTarget != GTA.Math.Vector3.Zero)
                            {
                                AddGuardToHeliGroundConvoy(newGuard, heliGroundConvoyTarget);
                            }
                        }
                    }
                    continue;
                }

                if (guard.IsDead) continue;

                // Health thresholds
                float healthPercent = (guard.MaxHealth > 0) ? (float)guard.Health / guard.MaxHealth * 100f : 100f;

                if (healthPercent <= 25f && !guardHealthWarnings[i, 2])
                {
                    guardHealthWarnings[i, 2] = true;
                    Tolk.Speak(label + " critical! " + (int)healthPercent + " percent.");
                }
                else if (healthPercent <= 50f && !guardHealthWarnings[i, 1])
                {
                    guardHealthWarnings[i, 1] = true;
                    Tolk.Speak(label + " wounded. " + (int)healthPercent + " percent.");
                }
                else if (healthPercent <= 75f && !guardHealthWarnings[i, 0])
                {
                    guardHealthWarnings[i, 0] = true;
                    Tolk.Speak(label + " taking damage. " + (int)healthPercent + " percent.");
                }
            }
        }

        void TickGuardPersistence()
        {
            if (DateTime.Now.Ticks - guardPersistenceCheckTicks < 20000000) return; // 2 seconds
            guardPersistenceCheckTicks = DateTime.Now.Ticks;

            for (int i = bodyguards.Count - 1; i >= 0; i--)
            {
                Ped guard = bodyguards[i];
                if (guard == null || !guard.Exists())
                {
                    string label = (i == 0) ? "Butler" : "Guard " + i;
                    Tolk.Speak(label + " lost.");
                    bodyguards.RemoveAt(i);
                    if (i == 0) guardDriverActive = false;
                    continue;
                }

                if (guard.IsDead) continue;

                // Skip Butler persistence teleport during active extraction
                // or while Butler is driving any vehicle
                if (i == 0 && (extractionInProgress || guard.IsInVehicle())) continue;

                float dist = World.GetDistance(Game.Player.Character.Position, guard.Position);

                // Teleport back if too far (covers player death/respawn too)
                if (dist > 200f)
                {
                    // Skip non-Butler guards when player is in an aircraft to avoid
                    // teleporting them mid-air where they'd collide with the helicopter
                    if (i > 0 && Game.Player.Character.IsInVehicle())
                    {
                        Vehicle pVeh = Game.Player.Character.CurrentVehicle;
                        if (pVeh != null)
                        {
                            int vehClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, pVeh);
                            if (vehClass == 15 || vehClass == 16) // Helicopter or Plane
                                continue;
                        }
                    }

                    // Don't rip non-Butler guards out of their convoy vehicles — the convoy
                    // systems handle them. Warping a ped in a moving vehicle ejects them
                    // directly in front of the player and gets them run over.
                    if (i > 0 && guard.IsInVehicle()) continue;

                    // Place the guard 8 m behind the player along their direction of travel,
                    // fanned within a ±60° rear arc so we never land in the player's path.
                    GTA.Math.Vector3 backUnit = GetPlayerBackUnit();
                    double baseAngleRad = Math.Atan2(backUnit.X, backUnit.Y); // bearing from +Y axis
                    double fanRad = ((i % 5) - 2) * (60.0 * Math.PI / 180.0 / 4.0); // -60..+60 in 30° steps
                    double finalRad = baseAngleRad + fanRad;
                    GTA.Math.Vector3 offset = new GTA.Math.Vector3(
                        (float)Math.Sin(finalRad) * 8f,
                        (float)Math.Cos(finalRad) * 8f,
                        0f);
                    guard.Position = Game.Player.Character.Position + offset;
                }

                // Re-issue follow if moderately far and task is follow
                if (dist > 50f && guardTaskMode == "follow")
                {
                    Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, guard, bodyguardGroupId);
                }

                // Re-add to group if needed
                int guardGroup = Function.Call<int>(Hash.GET_PED_GROUP_INDEX, guard);
                if (guardGroup != bodyguardGroupId && guardTaskMode == "follow")
                {
                    Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, guard, bodyguardGroupId);
                }
            }
        }

        private long guardTaskValidationTicks = 0;

        void TickGuardTaskValidation()
        {
            // Periodic sanity check every 5 seconds to catch guards in broken states
            if (DateTime.Now.Ticks - guardTaskValidationTicks < 50000000) return; // 5 seconds
            guardTaskValidationTicks = DateTime.Now.Ticks;

            bool playerInVehicle = Game.Player.Character.IsInVehicle();
            Vehicle playerVeh = playerInVehicle ? Game.Player.Character.CurrentVehicle : null;

            for (int i = 0; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                if (guard == null || !guard.Exists() || guard.IsDead) continue;

                // Skip butler if actively driving, extracting, or walking to vehicle
                if (i == 0 && (guardDriverActive || extractionInProgress || butlerWalkingToVehicle)) continue;
                // Skip guards in active combat
                if (guardCurrentTarget.ContainsKey(guard.Handle)) continue;
                // Skip if guards are patrolling or on a hold/waypoint task
                if (guardsPatrolling) continue;
                if (guardTaskMode == "hold" || guardTaskMode == "waypoint") continue;

                // FOLLOW mode validation: ensure guards are actually following
                if (guardTaskMode == "follow" || guardTaskMode == "ceasefire")
                {
                    if (!playerInVehicle)
                    {
                        // Player is on foot — guard should be on foot and in the group
                        float dist = World.GetDistance(guard.Position, Game.Player.Character.Position);

                        // If guard is stuck in a vehicle while player is on foot, get them out
                        if (guard.IsInVehicle() && dist < 200f)
                        {
                            guard.Task.LeaveVehicle();
                            continue;
                        }

                        // If guard is standing idle on foot and not close, re-issue group follow
                        if (!guard.IsInVehicle() && dist > 10f)
                        {
                            int guardGroup = Function.Call<int>(Hash.GET_PED_GROUP_INDEX, guard);
                            bool isIdle = !Function.Call<bool>(Hash.IS_PED_RUNNING, guard)
                                       && !Function.Call<bool>(Hash.IS_PED_WALKING, guard)
                                       && !Function.Call<bool>(Hash.IS_PED_SPRINTING, guard);

                            if (guardGroup != bodyguardGroupId || isIdle)
                            {
                                guard.Task.ClearAllImmediately();
                                if (bodyguardGroupId >= 0)
                                    Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, guard, bodyguardGroupId);
                                guard.AlwaysKeepTask = true;
                                guard.BlockPermanentEvents = true;
                            }
                        }
                    }
                    else if (playerVeh != null)
                    {
                        // Player is in vehicle — guards (non-butler) should be in player's vehicle or a convoy vehicle
                        if (i > 0 && !guard.IsInVehicle())
                        {
                            float dist = World.GetDistance(guard.Position, playerVeh.Position);
                            // Guard is on foot near the player's vehicle — warp them in if there's a seat
                            if (dist < 30f)
                            {
                                int maxPass = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, playerVeh);
                                for (int seat = 0; seat < maxPass; seat++)
                                {
                                    if (Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, playerVeh, seat))
                                    {
                                        guard.SetIntoVehicle(playerVeh, (VehicleSeat)seat);
                                        break;
                                    }
                                }
                                // If no seat in player vehicle, convoy will handle them
                            }
                        }
                    }
                }
            }
        }

        void TickGuardDriverLogic()
        {
            bool currentlyInVeh = Game.Player.Character.IsInVehicle();

            // Proactive entry assist: detect player TRYING to enter a vehicle with butler inside.
            // Butler's BlockPermanentEvents + CanBeDraggedOutOfVehicle=false can block natural entry,
            // so we warp the player into a passenger seat when the attempt is detected.
            if (!currentlyInVeh && bodyguards.Count > 0 && bodyguards[0] != null
                && bodyguards[0].Exists() && bodyguards[0].IsAlive
                && bodyguards[0].IsInVehicle())
            {
                Vehicle tryingToEnter = Function.Call<Vehicle>(
                    Hash.GET_VEHICLE_PED_IS_TRYING_TO_ENTER, Game.Player.Character);
                if (tryingToEnter != null && tryingToEnter.Exists()
                    && bodyguards[0].CurrentVehicle == tryingToEnter)
                {
                    int maxPass = Function.Call<int>(
                        Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, tryingToEnter);
                    if (maxPass >= 1)
                    {
                        Game.Player.Character.Task.ClearAll();
                        VehicleSeat pSeat = FindFirstFreePassengerSeat(tryingToEnter);
                        Game.Player.Character.SetIntoVehicle(tryingToEnter, pSeat);
                        currentlyInVeh = true; // Update so HandleGuardVehicleEntry fires below
                    }
                }
            }

            if (currentlyInVeh && !wasInVehicleForGuard && bodyguards.Count > 0)
            {
                Vehicle veh = Game.Player.Character.CurrentVehicle;
                if (veh != null)
                {
                    HandleGuardVehicleEntry(veh);
                }
            }

            if (!currentlyInVeh && wasInVehicleForGuard)
            {
                if (guardDriverActive || butlerWalkingToVehicle)
                {
                    guardDriverActive = false;
                    if (butlerWalkingToVehicle)
                    {
                        // Cancel butler walk-to-vehicle if player exits
                        butlerWalkingToVehicle = false;
                        butlerTargetVehicle = null;
                        if (bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].Exists())
                            bodyguards[0].Task.ClearAll();
                    }
                    if (isAutodriving)
                    {
                        isAutodriving = false;
                        autodriveWanderMode = false;
                        if (bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].Exists())
                        {
                            bodyguards[0].Task.ClearAll();
                        }
                    }
                }

                // Transition ALL guards back to on-foot follow mode
                for (int i = 0; i < bodyguards.Count; i++)
                {
                    Ped guard = bodyguards[i];
                    if (guard == null || !guard.Exists() || guard.IsDead) continue;
                    // Skip butler if mid-extraction (player may have been warped out)
                    if (i == 0 && extractionInProgress) continue;
                    guard.Task.ClearAllImmediately();
                    if (bodyguardGroupId >= 0)
                        Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, guard, bodyguardGroupId);
                    guard.AlwaysKeepTask = true;
                    guard.BlockPermanentEvents = true;
                }

                // Dismiss convoy vehicles since we're on foot now
                if (convoyActive || heliGroundConvoyActive)
                    DismissConvoyVehicles();

                guardTaskMode = "follow";
                guardsPatrolling = false;
                guardCurrentTarget.Clear();
                Tolk.Speak("Exited vehicle. Guards following.");
            }

            wasInVehicleForGuard = currentlyInVeh;

            // Water detection: spawn boats for guards when player enters water
            bool playerSwimming = Game.Player.Character.IsSwimming;
            if (playerSwimming && !playerInWater)
            {
                playerInWater = true;
                // Try to spawn a boat for Butler
                GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
                Model boatModel = new Model(VehicleHash.Dinghy);
                boatModel.Request(5000);
                if (boatModel.IsLoaded)
                {
                    Vehicle boat = World.CreateVehicle(boatModel, playerPos + Game.Player.Character.ForwardVector * 5f);
                    boatModel.MarkAsNoLongerNeeded();
                    if (boat != null)
                    {
                        boat.IsPersistent = true;
                        convoyVehicles.Add(boat);
                        if (bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive)
                        {
                            bodyguards[0].SetIntoVehicle(boat, VehicleSeat.Driver);
                            VehicleSeat seat = FindFirstFreePassengerSeat(boat);
                            Game.Player.Character.SetIntoVehicle(boat, seat);
                            guardDriverActive = true;
                        }
                        // Seat remaining guards
                        int maxSeats = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, boat);
                        int seatedCount = 0;
                        for (int i = 1; i < bodyguards.Count && seatedCount < maxSeats - 1; i++)
                        {
                            Ped guard = bodyguards[i];
                            if (guard == null || !guard.IsAlive || !guard.Exists()) continue;
                            for (int s = 0; s < maxSeats; s++)
                            {
                                if (Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, boat, s))
                                {
                                    guard.SetIntoVehicle(boat, (VehicleSeat)s);
                                    seatedCount++;
                                    break;
                                }
                            }
                        }
                        Tolk.Speak("Entering water. Guards deploying boat.");
                    }
                    else
                    {
                        Tolk.Speak("Entering water. Guards waiting on shore.");
                    }
                }
                else
                {
                    Tolk.Speak("Entering water. Guards waiting on shore.");
                }
            }
            else if (!playerSwimming && playerInWater)
            {
                playerInWater = false;
                // Clean up boats
                DismissConvoyVehicles();
            }

            // Seat correction backup: if player somehow ended up in driver seat of Butler's vehicle
            if (currentlyInVeh && bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].Exists() && bodyguards[0].IsAlive)
            {
                Vehicle veh = Game.Player.Character.CurrentVehicle;
                if (veh != null && veh.Driver == Game.Player.Character)
                {
                    // Check if Butler is also in this vehicle but not driving
                    if (bodyguards[0].IsInVehicle() && bodyguards[0].CurrentVehicle == veh)
                    {
                        int maxPass = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, veh);
                        if (maxPass >= 1)
                        {
                            VehicleSeat playerSeat = FindFirstFreePassengerSeat(veh);
                            Game.Player.Character.SetIntoVehicle(veh, playerSeat);
                            bodyguards[0].SetIntoVehicle(veh, VehicleSeat.Driver);
                            guardDriverActive = true;
                        }
                    }
                }
            }

            // Waypoint monitoring during wander mode — auto-switch to drive-to-waypoint
            if (isAutodriving && autodriveWanderMode && guardDriverActive &&
                bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive &&
                DateTime.Now.Ticks - waypointMonitorTicks >= 20000000) // every 2 seconds
            {
                waypointMonitorTicks = DateTime.Now.Ticks;
                if (Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE))
                {
                    int blip = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
                    if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, blip))
                    {
                        GTA.Math.Vector3 wp = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blip);
                        Ped driver = bodyguards[0];
                        Vehicle veh = Game.Player.Character.IsInVehicle() ? Game.Player.Character.CurrentVehicle : null;
                        if (veh != null && veh.Exists())
                        {
                            int vehClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, veh);
                            if (vehClass == 15) // Helicopter
                            {
                                float groundZ = World.GetGroundHeight(new GTA.Math.Vector2(wp.X, wp.Y));
                                if (groundZ > 0) wp.Z = groundZ + autopilotAltitude;
                                else wp.Z = autopilotAltitude;
                                autodriveDestination = wp;
                                Function.Call(Hash.TASK_HELI_MISSION,
                                    driver, veh, 0, 0,
                                    wp.X, wp.Y, wp.Z,
                                    4, autodriveSpeed, 20f, -1f,
                                    (int)(wp.Z + 100), (int)(wp.Z - 50),
                                    -1f, 0);
                                autodriveWanderMode = false;
                                autodriveStartDistance = World.GetDistance(Game.Player.Character.Position, wp);
                                Tolk.Speak("Flying to waypoint. " + (int)autodriveStartDistance + " meters.");
                            }
                            else if (vehClass == 16) // Plane
                            {
                                float groundZ = World.GetGroundHeight(new GTA.Math.Vector2(wp.X, wp.Y));
                                if (groundZ > 0) wp.Z = groundZ + autopilotAltitude;
                                else wp.Z = autopilotAltitude;
                                autodriveDestination = wp;
                                Function.Call(Hash.TASK_PLANE_MISSION,
                                    driver, veh, 0, 0,
                                    wp.X, wp.Y, wp.Z,
                                    4, autodriveSpeed, 20f, -1f,
                                    (int)(wp.Z + 100), (int)(wp.Z - 50),
                                    true);
                                autodriveWanderMode = false;
                                autodriveStartDistance = World.GetDistance(Game.Player.Character.Position, wp);
                                Tolk.Speak("Flying to waypoint. " + (int)autodriveStartDistance + " meters.");
                            }
                            else // Ground
                            {
                                float groundZ = World.GetGroundHeight(new GTA.Math.Vector2(wp.X, wp.Y));
                                if (groundZ > 0) wp.Z = groundZ;
                                autodriveDestination = wp;
                                int drivingStyle = GetDrivingStyleFromFlags();
                                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                                    driver, veh, wp.X, wp.Y, wp.Z,
                                    autodriveSpeed, drivingStyle, 20f);
                                autodriveWanderMode = false;
                                autodriveStartDistance = World.GetDistance(Game.Player.Character.Position, wp);
                                int speedMph = (int)Math.Round(autodriveSpeed * 2.23694);
                                Tolk.Speak("Driving to waypoint at " + speedMph + " mph. " + (int)autodriveStartDistance + " meters.");
                            }
                        }
                    }
                }
            }

            // Parking monitor moved out of bodyguard tick — see TickParkingMonitor() called
            // from onTick so player-driven parking (no Butler) also gets arrival detection.
        }

        void HandleGuardVehicleEntry(Vehicle veh)
        {
            if (bodyguards.Count == 0) return;
            Ped primaryGuard = bodyguards[0];
            if (primaryGuard == null || !primaryGuard.Exists() || primaryGuard.IsDead) return;

            int maxPassengers = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, veh);
            if (maxPassengers < 1)
            {
                // Single-seat vehicle: Butler drops to regular guard mode
                Tolk.Speak("No passenger seat. Butler on guard duty.");
                return;
            }

            // Cancel any previous walk-to-vehicle in progress
            if (butlerWalkingToVehicle)
            {
                primaryGuard.Task.ClearAll();
                butlerWalkingToVehicle = false;
                butlerTargetVehicle = null;
            }

            // Remove existing NPC driver if not our guard
            Ped existingDriver = veh.Driver;
            if (existingDriver != null && existingDriver.Exists() && existingDriver != primaryGuard)
            {
                existingDriver.Task.LeaveVehicle();
            }

            // BRANCH 1: Post-extraction = instant warp (butler warps directly into driver seat)
            if (postExtractionAutoEngage)
            {
                VehicleSeat playerSeat = FindFirstFreePassengerSeat(veh);
                Game.Player.Character.SetIntoVehicle(veh, playerSeat);
                primaryGuard.SetIntoVehicle(veh, VehicleSeat.Driver);
                guardDriverActive = true;

                string vehName = veh.LocalizedName;
                if (string.IsNullOrEmpty(vehName) || vehName == "NULL")
                    vehName = veh.DisplayName;
                Tolk.Speak("Butler driving " + vehName + ".", true);

                postExtractionAutoEngage = false;
                int vehClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, veh);

                if (postExtractionIsHeli && (vehClass == 15 || vehClass == 16))
                {
                    // Helicopter: climb to cruise altitude and hover
                    autonavMode = "fly";
                    GTA.Math.Vector3 pos = Game.Player.Character.Position;
                    float cruiseAlt = Math.Max(pos.Z, autopilotAltitude);
                    autodriveDestination = new GTA.Math.Vector3(pos.X, pos.Y, cruiseAlt);

                    Function.Call(Hash.TASK_HELI_MISSION,
                        primaryGuard, veh, 0, 0,
                        pos.X, pos.Y, cruiseAlt,
                        4, autodriveSpeed, 50f, -1f,
                        (int)(cruiseAlt + 100), (int)(cruiseAlt - 50),
                        -1f, 0);

                    isAutodriving = true;
                    autodriveWanderMode = true;
                    autodriveCheckTicks = DateTime.Now.Ticks;
                    int speedMph = (int)Math.Round(autodriveSpeed * 2.23694);
                    Tolk.Speak("Helicopter hovering at " + speedMph + " mph. Set a waypoint for a destination.");
                }
                else
                {
                    // Ground vehicle: wander
                    autonavMode = "drive";
                    int drivingStyle = GetDrivingStyleFromFlags();
                    Function.Call(Hash.SET_DRIVER_ABILITY, primaryGuard, 1.0f);
                    Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, primaryGuard, 0.5f);
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER,
                        primaryGuard, veh, autodriveSpeed, drivingStyle);

                    isAutodriving = true;
                    autodriveWanderMode = true;
                    autodriveCheckTicks = DateTime.Now.Ticks;
                    int speedMph = (int)Math.Round(autodriveSpeed * 2.23694);
                    Tolk.Speak("Wandering at " + speedMph + " mph. Set a waypoint for a destination.");
                }

                // Seat remaining guards in available passenger seats
                for (int i = 1; i < bodyguards.Count; i++)
                {
                    Ped guard = bodyguards[i];
                    if (guard == null || !guard.Exists() || guard.IsDead) continue;

                    for (int seat = 0; seat < maxPassengers; seat++)
                    {
                        if (Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, veh, seat))
                        {
                            guard.SetIntoVehicle(veh, (VehicleSeat)seat);
                            break;
                        }
                    }
                }
                return;
            }

            // BRANCH 2: Natural vehicle entry
            // If butler is already in this vehicle (e.g. during extraction), just warp player to passenger
            if (primaryGuard.IsInVehicle() && primaryGuard.CurrentVehicle == veh)
            {
                VehicleSeat pSeatWarp = FindFirstFreePassengerSeat(veh);
                Game.Player.Character.SetIntoVehicle(veh, pSeatWarp);
                guardDriverActive = true;
                if (veh.Driver != primaryGuard)
                    primaryGuard.SetIntoVehicle(veh, VehicleSeat.Driver);

                string vNameWarp = veh.LocalizedName;
                if (string.IsNullOrEmpty(vNameWarp) || vNameWarp == "NULL")
                    vNameWarp = veh.DisplayName;
                Tolk.Speak("Butler driving " + vNameWarp + ".", true);

                for (int i = 1; i < bodyguards.Count; i++)
                {
                    Ped guard = bodyguards[i];
                    if (guard == null || !guard.Exists() || guard.IsDead) continue;
                    for (int seat = 0; seat < maxPassengers; seat++)
                    {
                        if (Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, veh, seat))
                        {
                            guard.SetIntoVehicle(veh, (VehicleSeat)seat);
                            break;
                        }
                    }
                }
                return;
            }

            // Butler is NOT in the vehicle - distance-based walking entry
            // Move player to passenger seat immediately so driver seat is free for butler
            VehicleSeat pSeat = FindFirstFreePassengerSeat(veh);
            Game.Player.Character.SetIntoVehicle(veh, pSeat);

            float distToVeh = World.GetDistance(primaryGuard.Position, veh.Position);

            if (distToVeh > 25f)
            {
                // Warp butler to ~25m behind player, out of line of sight
                GTA.Math.Vector3 behindPlayer = Game.Player.Character.Position
                    - Game.Player.Character.ForwardVector * 25f;
                primaryGuard.Position = behindPlayer;
            }

            // Issue TASK_ENTER_VEHICLE: butler walks/runs to car and enters driver seat
            // Params: ped, vehicle, timeout_ms, seat (-1 = driver), speed (2.0 = run), flag, p6
            Function.Call(Hash.TASK_ENTER_VEHICLE,
                primaryGuard, veh, 20000, -1, 2.0f, 0, 0);
            primaryGuard.AlwaysKeepTask = true;
            primaryGuard.BlockPermanentEvents = true;

            butlerWalkingToVehicle = true;
            butlerTargetVehicle = veh;
            butlerWalkStartTicks = DateTime.Now.Ticks;

            string vName = veh.LocalizedName;
            if (string.IsNullOrEmpty(vName) || vName == "NULL")
                vName = veh.DisplayName;
            Tolk.Speak("Butler heading to " + vName + ".");
        }

        void TickButlerVehicleEntry()
        {
            if (!butlerWalkingToVehicle) return;
            if (bodyguards.Count == 0 || bodyguards[0] == null || !bodyguards[0].IsAlive)
            {
                butlerWalkingToVehicle = false;
                butlerTargetVehicle = null;
                return;
            }

            Ped butler = bodyguards[0];

            // Check if target vehicle is still valid
            if (butlerTargetVehicle == null || !butlerTargetVehicle.Exists())
            {
                butlerWalkingToVehicle = false;
                butlerTargetVehicle = null;
                butler.Task.ClearAll();
                return;
            }

            // Check if player left the vehicle while butler was walking
            if (!Game.Player.Character.IsInVehicle() ||
                Game.Player.Character.CurrentVehicle != butlerTargetVehicle)
            {
                butlerWalkingToVehicle = false;
                butlerTargetVehicle = null;
                butler.Task.ClearAll();
                return;
            }

            // SUCCESS: Butler entered the vehicle as driver
            if (butler.IsInVehicle() && butler.CurrentVehicle == butlerTargetVehicle)
            {
                if (butlerTargetVehicle.Driver == butler)
                {
                    guardDriverActive = true;

                    string vehName = butlerTargetVehicle.LocalizedName;
                    if (string.IsNullOrEmpty(vehName) || vehName == "NULL")
                        vehName = butlerTargetVehicle.DisplayName;
                    Tolk.Speak("Butler driving " + vehName + ".", true);

                    // Seat remaining guards
                    int maxPass = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, butlerTargetVehicle);
                    for (int i = 1; i < bodyguards.Count; i++)
                    {
                        Ped guard = bodyguards[i];
                        if (guard == null || !guard.Exists() || guard.IsDead) continue;
                        for (int seat = 0; seat < maxPass; seat++)
                        {
                            if (Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, butlerTargetVehicle, seat))
                            {
                                guard.SetIntoVehicle(butlerTargetVehicle, (VehicleSeat)seat);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // Butler got in but not driver seat - warp to driver
                    butler.SetIntoVehicle(butlerTargetVehicle, VehicleSeat.Driver);
                    return; // Let next tick handle the success case
                }

                butlerWalkingToVehicle = false;
                butlerTargetVehicle = null;
                return;
            }

            // TIMEOUT: 25 seconds elapsed, butler hasn't entered - fallback warp
            long elapsed = DateTime.Now.Ticks - butlerWalkStartTicks;
            if (elapsed > 250000000) // 25 seconds (1 tick = 100 nanoseconds)
            {
                butler.Task.ClearAll();
                butler.SetIntoVehicle(butlerTargetVehicle, VehicleSeat.Driver);
                guardDriverActive = true;
                Tolk.Speak("Butler warped to driver seat.");

                // Seat remaining guards
                int maxPass = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, butlerTargetVehicle);
                for (int i = 1; i < bodyguards.Count; i++)
                {
                    Ped guard = bodyguards[i];
                    if (guard == null || !guard.Exists() || guard.IsDead) continue;
                    for (int seat = 0; seat < maxPass; seat++)
                    {
                        if (Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, butlerTargetVehicle, seat))
                        {
                            guard.SetIntoVehicle(butlerTargetVehicle, (VehicleSeat)seat);
                            break;
                        }
                    }
                }

                butlerWalkingToVehicle = false;
                butlerTargetVehicle = null;
            }
        }

        // ============================================
        // PROACTIVE THREAT DETECTION
        // Layered detection: relationship, guard-awareness, armed proximity, combat fallback
        // ============================================
        void TickProactiveThreatScan()
        {
            if (DateTime.Now.Ticks - proactiveScanTicks < 20000000) return; // 2 seconds
            proactiveScanTicks = DateTime.Now.Ticks;

            trackedEnemies.Clear();
            watchedPeds.RemoveAll(p => p == null || p.IsDead || !p.Exists());

            Ped player = Game.Player.Character;
            float scanRadius = proactiveThreatDetection ? 120f : 100f;
            Ped[] nearbyPeds = World.GetNearbyPeds(player.Position, scanRadius);

            // Build a set of guard handles to skip
            HashSet<int> guardHandles = new HashSet<int>();
            foreach (Ped g in bodyguards)
            {
                if (g != null && g.Exists()) guardHandles.Add(g.Handle);
            }

            foreach (Ped ped in nearbyPeds)
            {
                if (ped == player || ped.IsDead || !ped.Exists()) continue;
                if (guardHandles.Contains(ped.Handle)) continue; // skip our own guards

                bool isHostile = false;

                // Layer D (always active): direct combat against player
                if (ped.IsInCombatAgainst(player))
                {
                    isHostile = true;
                }

                // Layer A: relationship-based detection (proactive)
                if (!isHostile && proactiveThreatDetection)
                {
                    int rel = Function.Call<int>(Hash.GET_RELATIONSHIP_BETWEEN_PEDS, ped, player);
                    if (rel == 4 || rel == 5) // Dislike or Hate
                    {
                        isHostile = true;
                    }
                }

                // Police detection: always engage cops during wanted level
                if (!isHostile && Game.Player.WantedLevel > 0)
                {
                    if (Function.Call<int>(Hash.GET_PED_TYPE, ped) == 6) // PED_TYPE_COP
                    {
                        float distToPlayer = World.GetDistance(player.Position, ped.Position);
                        if (distToPlayer < 80f) // only engage cops within reasonable range
                        {
                            isHostile = true;
                        }
                    }
                }

                // Layer B: guard-awareness propagation
                if (!isHostile && proactiveThreatDetection)
                {
                    foreach (Ped guard in bodyguards)
                    {
                        if (guard == null || !guard.Exists() || !guard.IsAlive) continue;
                        if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, guard, ped))
                        {
                            isHostile = true;
                            break;
                        }
                    }
                }

                // Layer C: armed ped proximity alert
                if (!isHostile && armedPedAlert)
                {
                    float distToPlayer = World.GetDistance(player.Position, ped.Position);
                    if (distToPlayer < 30f && Function.Call<bool>(Hash.IS_PED_ARMED, ped, 4)) // 4 = firearm
                    {
                        // Check if this armed ped is aiming or shooting
                        bool isAiming = Function.Call<bool>(Hash.IS_PED_SHOOTING, ped)
                            || Function.Call<bool>(Hash.GET_IS_TASK_ACTIVE, ped, 4); // TASK_AIM_GUN_ON_FOOT
                        if (isAiming)
                        {
                            isHostile = true;
                        }
                        else if (!watchedPeds.Contains(ped))
                        {
                            watchedPeds.Add(ped);
                        }
                    }
                }

                // Promote watched peds that start aiming/shooting
                if (!isHostile && watchedPeds.Contains(ped))
                {
                    if (Function.Call<bool>(Hash.IS_PED_SHOOTING, ped)
                        || ped.IsInCombatAgainst(player)
                        || Function.Call<bool>(Hash.GET_IS_TASK_ACTIVE, ped, 4))
                    {
                        isHostile = true;
                        watchedPeds.Remove(ped);
                    }
                }

                if (isHostile)
                {
                    trackedEnemies.Add(ped);
                }
            }

            // Announcements (delta-only: speak only when count changes)
            if (trackedEnemies.Count != lastAnnouncedEnemyCount)
            {
                if (trackedEnemies.Count > 0)
                {
                    // Check if police are among threats
                    bool policeDetected = false;
                    if (Game.Player.WantedLevel > 0)
                    {
                        foreach (Ped e in trackedEnemies)
                        {
                            if (e != null && e.Exists() && Function.Call<int>(Hash.GET_PED_TYPE, e) == 6) // PED_TYPE_COP
                            {
                                policeDetected = true;
                                break;
                            }
                        }
                    }

                    if (policeDetected)
                        Tolk.Speak(trackedEnemies.Count + " hostile" + (trackedEnemies.Count > 1 ? "s" : "") + " detected. Police closing in.", true);
                    else
                        Tolk.Speak(trackedEnemies.Count + " hostile" + (trackedEnemies.Count > 1 ? "s" : "") + " detected", true);
                }
                else if (lastAnnouncedEnemyCount > 0)
                {
                    Tolk.Speak("All threats neutralized.", true);
                }
                lastAnnouncedEnemyCount = trackedEnemies.Count;
            }
        }

        // ============================================
        // AUTO-ENGAGEMENT (with coordinated targeting and threat prioritization)
        // ============================================
        float ThreatScore(Ped enemy)
        {
            float score = 0;
            Ped player = Game.Player.Character;
            float distToPlayer = World.GetDistance(player.Position, enemy.Position);

            // Distance factor: closer = more dangerous
            score += Math.Max(0, 100 - distToPlayer);

            // Actively in combat against player
            if (enemy.IsInCombatAgainst(player)) score += 50;

            // Armed with firearm
            if (Function.Call<bool>(Hash.IS_PED_ARMED, enemy, 4)) score += 30;

            // Has line of sight to player
            if (Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY, enemy, player, 17)) score += 25;

            // Currently shooting
            if (Function.Call<bool>(Hash.IS_PED_SHOOTING, enemy)) score += 40;

            // In a vehicle (drive-by threat)
            if (enemy.IsInVehicle()) score += 20;

            return score;
        }

        void TickAutoEngagement()
        {
            if (DateTime.Now.Ticks - autoEngagementCheckTicks < 20000000) return; // 2 seconds
            autoEngagementCheckTicks = DateTime.Now.Ticks;

            // Clean up dead/invalid enemies
            trackedEnemies.RemoveAll(p => p == null || p.IsDead || !p.Exists());

            if (trackedEnemies.Count > 0 && guardTaskMode != "ceasefire")
            {
                // Sort enemies by threat score (highest first)
                List<Ped> sortedEnemies = new List<Ped>(trackedEnemies);
                sortedEnemies.Sort((a, b) => ThreatScore(b).CompareTo(ThreatScore(a)));

                // Reset assignment counts
                enemyAssignmentCount.Clear();
                guardToEnemyHandle.Clear();

                // Assign guards 1-6 with coordinated spreading
                for (int i = 1; i < bodyguards.Count; i++)
                {
                    Ped guard = bodyguards[i];
                    if (guard == null || !guard.IsAlive || !guard.Exists()) continue;

                    int profile = GetGuardProfile(guard);

                    // Close Protection guards prioritize enemies near the player
                    Ped bestTarget = null;
                    if (profile == 4) // Close Protection
                    {
                        float bestDist = float.MaxValue;
                        foreach (Ped enemy in sortedEnemies)
                        {
                            if (enemy == null || !enemy.Exists() || enemy.IsDead) continue;
                            float distToPlayer = World.GetDistance(Game.Player.Character.Position, enemy.Position);
                            if (distToPlayer < 15f && distToPlayer < bestDist)
                            {
                                bestDist = distToPlayer;
                                bestTarget = enemy;
                            }
                        }
                        // Fallback: if no enemy near player, pick highest threat
                        if (bestTarget == null && sortedEnemies.Count > 0)
                            bestTarget = sortedEnemies[0];
                    }
                    else
                    {
                        // Coordinated assignment: pick highest-priority enemy with fewest guards assigned
                        float bestScore = -1;
                        foreach (Ped enemy in sortedEnemies)
                        {
                            if (enemy == null || !enemy.Exists() || enemy.IsDead) continue;
                            int assignCount = 0;
                            if (enemyAssignmentCount.ContainsKey(enemy.Handle))
                                assignCount = enemyAssignmentCount[enemy.Handle];

                            // Prefer enemies with fewer guards assigned, weighted by threat score
                            float effectiveScore = ThreatScore(enemy) - (assignCount * 80f);

                            // Sniper overwatch prefers distant targets
                            if (profile == 3)
                            {
                                float distToGuard = World.GetDistance(guard.Position, enemy.Position);
                                if (distToGuard > 30f) effectiveScore += 30f; // bonus for far targets
                            }

                            if (effectiveScore > bestScore)
                            {
                                bestScore = effectiveScore;
                                bestTarget = enemy;
                            }
                        }
                    }

                    if (bestTarget != null)
                    {
                        int guardHandle = guard.Handle;
                        Ped currentTarget = null;
                        if (guardCurrentTarget.ContainsKey(guardHandle))
                            currentTarget = guardCurrentTarget[guardHandle];

                        // Re-engage if target changed or current target dead
                        if (currentTarget == null || !currentTarget.Exists() || currentTarget.IsDead || currentTarget != bestTarget)
                        {
                            guard.Task.ClearAllImmediately();
                            guard.Task.FightAgainst(bestTarget);
                            guard.AlwaysKeepTask = true;
                            guard.BlockPermanentEvents = true;
                            guardCurrentTarget[guardHandle] = bestTarget;
                            if (!guardCombatState.ContainsKey(guardHandle) || guardCombatState[guardHandle] == GuardCombatState.Idle)
                                guardCombatState[guardHandle] = GuardCombatState.Engaging;
                        }

                        // Track assignments for spreading
                        if (!enemyAssignmentCount.ContainsKey(bestTarget.Handle))
                            enemyAssignmentCount[bestTarget.Handle] = 0;
                        enemyAssignmentCount[bestTarget.Handle]++;
                        guardToEnemyHandle[guardHandle] = bestTarget.Handle;
                    }
                }
            }
            else if (trackedEnemies.Count == 0 && guardCurrentTarget.Count > 0)
            {
                // All threats neutralized - resume follow
                guardCurrentTarget.Clear();
                guardToEnemyHandle.Clear();
                enemyAssignmentCount.Clear();
                for (int i = 1; i < bodyguards.Count; i++)
                {
                    Ped guard = bodyguards[i];
                    if (guard == null || !guard.IsAlive || !guard.Exists()) continue;
                    guard.Task.ClearAllImmediately();
                    guard.AlwaysKeepTask = true;
                    guard.BlockPermanentEvents = true;
                    if (guardCombatState.ContainsKey(guard.Handle))
                        guardCombatState[guard.Handle] = GuardCombatState.Idle;
                }
            }
        }

        private long autoEngagementCheckTicks = 0;

        // ============================================
        // COMBAT TACTICS: flanking, suppression, close protection, re-engagement
        // Runs every 3 seconds on top of FightAgainst() to add scripted behavior layers
        // ============================================
        void TickCombatTactics()
        {
            if (DateTime.Now.Ticks - combatTacticsTicks < 30000000) return; // 3 seconds
            combatTacticsTicks = DateTime.Now.Ticks;

            if (trackedEnemies.Count == 0 || guardTaskMode == "ceasefire") return;
            if (guardTaskMode == "hold" || guardTaskMode == "waypoint") return;

            Ped player = Game.Player.Character;

            for (int i = 1; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                if (guard == null || !guard.IsAlive || !guard.Exists()) continue;
                if (guard.IsInVehicle()) continue; // skip vehicle-based guards (they use drive-by behavior)

                int guardHandle = guard.Handle;
                int profile = GetGuardProfile(guard);

                // Get current target
                Ped currentTarget = null;
                if (guardCurrentTarget.ContainsKey(guardHandle))
                    currentTarget = guardCurrentTarget[guardHandle];
                if (currentTarget != null && (!currentTarget.Exists() || currentTarget.IsDead))
                    currentTarget = null;

                // Re-engagement: if target is dead, immediately find next target
                if (currentTarget == null && trackedEnemies.Count > 0)
                {
                    Ped nextTarget = null;
                    float bestScore = -1;
                    foreach (Ped enemy in trackedEnemies)
                    {
                        if (enemy == null || !enemy.Exists() || enemy.IsDead) continue;
                        float score = ThreatScore(enemy);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            nextTarget = enemy;
                        }
                    }
                    if (nextTarget != null)
                    {
                        guard.Task.ClearAllImmediately();
                        guard.Task.FightAgainst(nextTarget);
                        guard.AlwaysKeepTask = true;
                        guard.BlockPermanentEvents = true;
                        guardCurrentTarget[guardHandle] = nextTarget;
                        guardCombatState[guardHandle] = GuardCombatState.Engaging;
                        currentTarget = nextTarget;
                    }
                    continue;
                }

                if (currentTarget == null) continue;

                float distToTarget = World.GetDistance(guard.Position, currentTarget.Position);
                float distToPlayer = World.GetDistance(guard.Position, player.Position);

                // Profile-specific tactical behavior
                switch (profile)
                {
                    case 5: // Flanker -- move to flank position then engage
                        long lastRepos = 0;
                        if (guardLastRepositionTicks.ContainsKey(guardHandle))
                            lastRepos = guardLastRepositionTicks[guardHandle];

                        if (DateTime.Now.Ticks - lastRepos > 80000000) // 8 seconds between repositions
                        {
                            // Calculate flank position: 90 degrees to enemy's right, 15m out
                            GTA.Math.Vector3 enemyForward = currentTarget.ForwardVector;
                            GTA.Math.Vector3 flankDir = new GTA.Math.Vector3(enemyForward.Y, -enemyForward.X, 0f);
                            GTA.Math.Vector3 flankPos = currentTarget.Position + flankDir * 15f;

                            // Check if flank position has LOS to enemy
                            bool hasLOS = Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY, guard, currentTarget, 17);

                            if (distToTarget > 10f) // only reposition if not already close
                            {
                                guard.Task.ClearAllImmediately();
                                Function.Call(Hash.TASK_GO_TO_COORD_ANY_MEANS, guard,
                                    flankPos.X, flankPos.Y, flankPos.Z,
                                    2.0f, // speed (run)
                                    0, 0, 786603, // walking style
                                    0f);
                                guardCombatState[guardHandle] = GuardCombatState.Flanking;
                                guardLastRepositionTicks[guardHandle] = DateTime.Now.Ticks;
                            }
                            else
                            {
                                // Close enough, engage from flank
                                guard.Task.ClearAllImmediately();
                                guard.Task.FightAgainst(currentTarget);
                                guard.AlwaysKeepTask = true;
                                guard.BlockPermanentEvents = true;
                                guardCombatState[guardHandle] = GuardCombatState.Engaging;
                                guardLastRepositionTicks[guardHandle] = DateTime.Now.Ticks;
                            }
                        }
                        else if (guardCombatState.ContainsKey(guardHandle) && guardCombatState[guardHandle] == GuardCombatState.Flanking)
                        {
                            // If flanking and close to target, switch to engage
                            if (distToTarget < 12f)
                            {
                                guard.Task.ClearAllImmediately();
                                guard.Task.FightAgainst(currentTarget);
                                guard.AlwaysKeepTask = true;
                                guard.BlockPermanentEvents = true;
                                guardCombatState[guardHandle] = GuardCombatState.Engaging;
                            }
                        }
                        break;

                    case 4: // Close Protection -- stay near player, engage close threats
                        if (distToPlayer > 10f && distToTarget > 15f)
                        {
                            // Too far from player, return to protection position
                            GTA.Math.Vector3 protectPos = player.Position + player.ForwardVector * -2f; // behind player
                            guard.Task.ClearAllImmediately();
                            Function.Call(Hash.TASK_GO_TO_COORD_ANY_MEANS, guard,
                                protectPos.X, protectPos.Y, protectPos.Z,
                                2.0f, 0, 0, 786603, 0f);
                            guardCombatState[guardHandle] = GuardCombatState.Protecting;
                        }
                        else if (distToPlayer <= 10f || distToTarget <= 15f)
                        {
                            // Near player or enemy is close -- engage
                            if (!guardCombatState.ContainsKey(guardHandle) || guardCombatState[guardHandle] != GuardCombatState.Engaging)
                            {
                                guard.Task.ClearAllImmediately();
                                guard.Task.FightAgainst(currentTarget);
                                guard.AlwaysKeepTask = true;
                                guard.BlockPermanentEvents = true;
                                guardCombatState[guardHandle] = GuardCombatState.Engaging;
                            }
                        }
                        break;

                    case 3: // Sniper Overwatch -- maintain distance, don't close in
                        if (distToTarget < 25f)
                        {
                            // Too close, back off to optimal sniper range
                            GTA.Math.Vector3 toEnemy = currentTarget.Position - guard.Position;
                            GTA.Math.Vector3 backoffDir = GTA.Math.Vector3.Normalize(toEnemy) * -1f;
                            GTA.Math.Vector3 sniperPos = guard.Position + backoffDir * 20f;

                            guard.Task.ClearAllImmediately();
                            Function.Call(Hash.TASK_GO_TO_COORD_ANY_MEANS, guard,
                                sniperPos.X, sniperPos.Y, sniperPos.Z,
                                2.0f, 0, 0, 786603, 0f);
                            guardCombatState[guardHandle] = GuardCombatState.Repositioning;
                            guardLastRepositionTicks[guardHandle] = DateTime.Now.Ticks;
                        }
                        else if (distToTarget >= 25f)
                        {
                            // At good range, shoot from position
                            if (!guardCombatState.ContainsKey(guardHandle) || guardCombatState[guardHandle] != GuardCombatState.Engaging)
                            {
                                guard.Task.ClearAllImmediately();
                                guard.Task.FightAgainst(currentTarget);
                                guard.AlwaysKeepTask = true;
                                guard.BlockPermanentEvents = true;
                                guardCombatState[guardHandle] = GuardCombatState.Engaging;
                            }
                        }
                        break;

                    default: // Aggressive, Balanced, Defensive -- suppressive fire when outnumbering
                        // Suppressive fire: if there are more guards than enemies, excess guards suppress
                        int aliveGuards = 0;
                        for (int g = 1; g < bodyguards.Count; g++)
                        {
                            if (bodyguards[g] != null && bodyguards[g].IsAlive && bodyguards[g].Exists())
                                aliveGuards++;
                        }

                        if (aliveGuards >= 4 && trackedEnemies.Count <= 2)
                        {
                            // Check if this guard should suppress (every other guard)
                            bool shouldSuppress = (i % 2 == 0) && profile != 0; // don't suppress if aggressive (they charge)
                            if (shouldSuppress && distToTarget > 15f)
                            {
                                // Suppressive fire at enemy position
                                GTA.Math.Vector3 targetPos = currentTarget.Position;
                                guard.Task.ClearAllImmediately();
                                Function.Call(Hash.TASK_SHOOT_AT_COORD, guard,
                                    targetPos.X, targetPos.Y, targetPos.Z,
                                    5000, // duration ms
                                    unchecked((uint)0xC6EE6B4C)); // FULL_AUTO
                                guardCombatState[guardHandle] = GuardCombatState.Suppressing;
                            }
                            else
                            {
                                // Assault guards engage directly
                                if (!guardCombatState.ContainsKey(guardHandle) || guardCombatState[guardHandle] == GuardCombatState.Suppressing)
                                {
                                    guard.Task.ClearAllImmediately();
                                    guard.Task.FightAgainst(currentTarget);
                                    guard.AlwaysKeepTask = true;
                                    guard.BlockPermanentEvents = true;
                                    guardCombatState[guardHandle] = GuardCombatState.Engaging;
                                }
                            }
                        }
                        break;
                }
            }
        }

        // ============================================
        // BUTLER EVASIVE DRIVING
        // ============================================
        void TickButlerEvasion()
        {
            if (bodyguards.Count == 0 || bodyguards[0] == null || !bodyguards[0].IsAlive) return;
            if (!guardDriverActive) return;

            Ped butler = bodyguards[0];
            Vehicle veh = butler.CurrentVehicle;
            if (veh == null) return;

            trackedEnemies.RemoveAll(p => p == null || p.IsDead || !p.Exists());

            if (trackedEnemies.Count > 0)
            {
                if (!butlerEvading)
                {
                    butlerEvading = true;
                    Tolk.Speak("Evading!");
                    threatsClearTicks = 0;
                }

                // Re-evaluate escape direction every 3 seconds
                if (DateTime.Now.Ticks - butlerEvadeCheckTicks < 30000000) return;
                butlerEvadeCheckTicks = DateTime.Now.Ticks;

                GTA.Math.Vector3 escapeVector = CalculateEscapeVector();
                GTA.Math.Vector3 escapeDest = Game.Player.Character.Position + escapeVector * 200f;

                float currentSpeed = veh.Speed;
                float evadeSpeed = Math.Min(currentSpeed + 5f, 40f); // Cap at ~90 mph

                butler.Task.ClearAllImmediately();
                // Rushed but safe: avoid vehicles (2) + stop before peds (1) + avoid empty vehicles (4) + use shortcuts (262144)
                // NOT reckless: no wrong-way driving
                int evadeDrivingStyle = 2 + 1 + 4 + 262144;
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    butler, veh,
                    escapeDest.X, escapeDest.Y, escapeDest.Z,
                    evadeSpeed, evadeDrivingStyle, 20f);
                butler.AlwaysKeepTask = true;
                butler.BlockPermanentEvents = true;
            }
            else if (butlerEvading)
            {
                // Threats cleared, wait 5 seconds before resuming normal
                if (threatsClearTicks == 0)
                    threatsClearTicks = DateTime.Now.Ticks;

                if (DateTime.Now.Ticks - threatsClearTicks > 50000000) // 5 seconds
                {
                    butlerEvading = false;
                    threatsClearTicks = 0;
                    Tolk.Speak("Clear.");

                    // Resume normal driving if auto-drive was active
                    if (isAutodriving)
                        UpdateAutodriveSpeed();
                }
            }
        }

        GTA.Math.Vector3 CalculateEscapeVector()
        {
            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
            GTA.Math.Vector3 avgEnemyPos = GTA.Math.Vector3.Zero;
            int count = 0;

            foreach (Ped enemy in trackedEnemies)
            {
                if (enemy == null || !enemy.Exists()) continue;
                avgEnemyPos += enemy.Position;
                count++;
            }

            if (count == 0) return Game.Player.Character.ForwardVector;

            avgEnemyPos /= count;
            GTA.Math.Vector3 awayFromEnemies = playerPos - avgEnemyPos;
            awayFromEnemies.Z = 0; // Keep on ground plane
            if (awayFromEnemies.Length() < 0.1f)
                return Game.Player.Character.ForwardVector;
            return GTA.Math.Vector3.Normalize(awayFromEnemies);
        }

        // ============================================
        // PHASE 5: PER-GUARD WEAPON CONFIG
        // ============================================
        void LoadGuardWeaponConfig()
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/guardWeapons.json";
            try
            {
                if (System.IO.File.Exists(path))
                {
                    string json = System.IO.File.ReadAllText(path);
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    if (dict == null) { Tolk.Speak("Guard weapon config empty or invalid; using defaults."); return; }
                    guardWeaponConfig.Clear();
                    foreach (var kvp in dict)
                    {
                        if (WEAPON_NAME_MAP.ContainsKey(kvp.Value))
                            guardWeaponConfig[kvp.Key] = WEAPON_NAME_MAP[kvp.Value];
                    }
                }
                else
                {
                    SaveGuardWeaponConfig();
                }
            }
            catch (Exception ex)
            {
                // Audible failure: the user is blind/visually-impaired and would otherwise
                // never know why per-guard weapon assignment isn't taking effect.
                Tolk.Speak("Guard weapon config load failed: " + ex.Message);
            }
        }

        void SaveGuardWeaponConfig()
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/guardWeapons.json";
            try
            {
                var dict = new Dictionary<string, string>();
                string[] keys = { "butler", "guard1", "guard2", "guard3", "guard4", "guard5", "guard6" };
                foreach (string key in keys)
                {
                    if (guardWeaponConfig.ContainsKey(key))
                    {
                        // Reverse lookup name from hash
                        string name = "Pistol";
                        foreach (var wkvp in WEAPON_NAME_MAP)
                        {
                            if (wkvp.Value == guardWeaponConfig[key]) { name = wkvp.Key; break; }
                        }
                        dict[key] = name;
                    }
                    else
                    {
                        dict[key] = "Pistol";
                    }
                }
                string json = JsonConvert.SerializeObject(dict, Formatting.Indented);
                System.IO.File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Tolk.Speak("Guard weapon config save failed: " + ex.Message);
            }
        }

        void ReloadGuardWeaponsFromConfig()
        {
            LoadGuardWeaponConfig();
            string[] keys = { "butler", "guard1", "guard2", "guard3", "guard4", "guard5", "guard6" };
            for (int i = 0; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                if (guard == null || !guard.IsAlive || !guard.Exists()) continue;
                string key = (i == 0) ? "butler" : "guard" + i;
                if (guardWeaponConfig.ContainsKey(key))
                {
                    WeaponHash wh = guardWeaponConfig[key];
                    guard.Weapons.RemoveAll();
                    guard.Weapons.Give(wh, 9999, true, true);
                    Function.Call(Hash.SET_PED_INFINITE_AMMO, guard, true);
                    Function.Call(Hash.SET_PED_INFINITE_AMMO_CLIP, guard, true);
                }
            }
            Tolk.Speak("Weapon config reloaded. " + bodyguards.Count + " guards re-armed.");
        }

        // ============================================
        // PHASE 5: CUSTOM FORMATIONS
        // ============================================
        void TickCustomFormation()
        {
            // Only apply custom formations (indices 4, 5, 6)
            if (guardFormationIndex < 4) return;
            if (DateTime.Now.Ticks - guardCustomFormationTicks < 50000000) return; // 5 seconds
            guardCustomFormationTicks = DateTime.Now.Ticks;

            Ped player = Game.Player.Character;
            for (int i = 0; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                if (guard == null || !guard.IsAlive || !guard.Exists()) continue;
                // Don't override Butler's driving task or helicopter extraction
                if (i == 0 && (guardDriverActive || extractionInProgress) && guard.IsInVehicle()) continue;
                // Don't override combat tasks
                if (guardCurrentTarget.ContainsKey(guard.Handle)) continue;

                GTA.Math.Vector2 offset = GetFormationOffset(i, guardFormationIndex);
                float scaledX = offset.X * guardFormationSpacing;
                float scaledY = offset.Y * guardFormationSpacing;

                guard.Task.ClearAllImmediately();
                Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY,
                    guard, player,
                    scaledX, scaledY, 0f,
                    2f, // speed
                    -1, // duration (infinite)
                    2f, // stop range
                    true); // relative offset
                guard.AlwaysKeepTask = true;
                guard.BlockPermanentEvents = true;
            }
        }

        GTA.Math.Vector2 GetFormationOffset(int guardIndex, int formationType)
        {
            switch (formationType)
            {
                case 4: // V-Wedge
                    float wx = (guardIndex % 2 == 0) ? -(1 + guardIndex) : (1 + guardIndex);
                    float wy = -(1 + guardIndex);
                    return new GTA.Math.Vector2(wx, wy);

                case 5: // Diamond
                    switch (guardIndex)
                    {
                        case 0: return new GTA.Math.Vector2(0, 3);    // Front
                        case 1: return new GTA.Math.Vector2(0, -3);   // Rear
                        case 2: return new GTA.Math.Vector2(-3, 0);   // Left
                        case 3: return new GTA.Math.Vector2(3, 0);    // Right
                        case 4: return new GTA.Math.Vector2(-2, 2);   // Front-left
                        case 5: return new GTA.Math.Vector2(2, 2);    // Front-right
                        default: return new GTA.Math.Vector2(0, -4);  // Extra rear
                    }

                case 6: // Front/Back Escort
                    float ex = (guardIndex % 2 == 0) ? -1.5f : 1.5f;
                    float ey = (guardIndex < 3) ? 3f : -3f;
                    return new GTA.Math.Vector2(ex * (1 + guardIndex * 0.3f), ey);

                default:
                    return new GTA.Math.Vector2(0, -2);
            }
        }

        // ============================================
        // PHASE 7: AUTO-PATROL
        // ============================================
        void TickGuardPatrol()
        {
            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

            // Check if player is stationary
            float distMoved = World.GetDistance(playerPos, playerStationaryPos);

            if (distMoved > 2f)
            {
                // Player moved - reset timer and cancel patrol
                playerStationaryPos = playerPos;
                playerStationaryTicks = DateTime.Now.Ticks;

                if (guardsPatrolling)
                {
                    guardsPatrolling = false;
                    // Cancel patrol tasks, re-add to group
                    for (int i = 0; i < bodyguards.Count; i++)
                    {
                        Ped guard = bodyguards[i];
                        if (guard == null || !guard.IsAlive || !guard.Exists()) continue;
                        // Don't clear butler's driving/extraction task if in a vehicle or extracting
                        if (i == 0 && (guard.IsInVehicle() || extractionInProgress)) continue;
                        guard.Task.ClearAllImmediately();
                        // Re-add to group
                        if (bodyguardGroupId >= 0)
                            Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, guard, bodyguardGroupId);
                        guard.AlwaysKeepTask = true;
                        guard.BlockPermanentEvents = true;
                    }
                    Tolk.Speak("Patrol ended, guards following.");
                }
                return;
            }

            // Check if stationary for 30 seconds
            if (!guardsPatrolling && DateTime.Now.Ticks - playerStationaryTicks > 300000000) // 30 seconds
            {
                guardsPatrolling = true;
                bool playerInVehicle = Game.Player.Character.IsInVehicle();

                for (int i = 0; i < bodyguards.Count; i++)
                {
                    Ped guard = bodyguards[i];
                    if (guard == null || !guard.IsAlive || !guard.Exists()) continue;

                    // Never pull butler out of a vehicle to patrol
                    if (i == 0 && guard.IsInVehicle()) continue;

                    guard.Task.ClearAllImmediately();

                    if (guard.IsInVehicle() && playerInVehicle)
                    {
                        // Vehicle patrol - 100m radius at low speed
                        Vehicle guardVeh = guard.CurrentVehicle;
                        if (guardVeh != null)
                        {
                            Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, guard, guardVeh, 6.7f, 786603); // ~15 mph
                        }
                    }
                    else
                    {
                        // Foot patrol - wander in area
                        Function.Call(Hash.TASK_WANDER_STANDARD, guard, 10f, 0);
                    }
                    guard.AlwaysKeepTask = true;
                    guard.BlockPermanentEvents = true;
                }
                Tolk.Speak("Guards patrolling.");
            }
        }

        // ============================================
        // PHASE 8: ADAPTIVE CONVOY SYSTEM
        // ============================================
        void TickConvoyManagement()
        {
            if (bodyguards.Count <= 1) return;
            if (!Game.Player.Character.IsInVehicle()) return;

            Vehicle playerVeh = Game.Player.Character.CurrentVehicle;
            if (playerVeh == null) return;

            // If player is in a helicopter or plane, use ground convoy system instead
            int playerVehClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, playerVeh);
            if (playerVehClass == 15 || playerVehClass == 16) // Helicopter or Plane
            {
                TickHeliGroundConvoy();
                return;
            }

            float speed = playerVeh.Speed;
            int maxPassengers = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, playerVeh);

            // Count overflow guards (those not in the player's vehicle)
            int overflowCount = 0;
            for (int i = 1; i < bodyguards.Count; i++) // Skip Butler
            {
                Ped guard = bodyguards[i];
                if (guard == null || !guard.IsAlive || !guard.Exists()) continue;
                if (!guard.IsInVehicle() || guard.CurrentVehicle != playerVeh)
                    overflowCount++;
            }

            if (overflowCount == 0) return;

            if (speed >= 4f && !convoyActive) // >= ~9 mph
            {
                // Spawn convoy vehicles
                convoyActive = true;
                SpawnConvoyVehicle(playerVeh);
            }
            else if (speed < 2f && convoyActive) // < ~4.5 mph
            {
                // Dismiss convoy, guards go on foot
                convoyActive = false;
                DismissConvoyVehicles();
            }

            // Maintain convoy - check distance every 2s
            if (convoyActive && convoyVehicles.Count > 0)
            {
                if (DateTime.Now.Ticks - guardPersistenceCheckTicks < 20000000) return;

                foreach (Vehicle cv in convoyVehicles)
                {
                    if (cv == null || !cv.Exists()) continue;
                    float dist = World.GetDistance(cv.Position, playerVeh.Position);

                    if (dist > 80f)
                    {
                        // Too far - teleport to nearest road node behind the player's
                        // direction of travel (not just facing) so we never spawn the
                        // convoy in front of a reversing or spun-around player. The
                        // candidate point itself avoids alleys via FindGoodRearSpawn;
                        // the native then snaps it to the closest actual road node.
                        GTA.Math.Vector3 playerBack = FindGoodRearSpawn(playerVeh.Position, 25f);
                        OutputArgument outPos = new OutputArgument();
                        OutputArgument outHeading = new OutputArgument();
                        OutputArgument outLanes = new OutputArgument();
                        Function.Call(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                            playerBack.X, playerBack.Y, playerBack.Z,
                            0, outPos, outHeading, outLanes, 1, 3.0f, 0f);
                        GTA.Math.Vector3 roadPos = outPos.GetResult<GTA.Math.Vector3>();
                        if (roadPos != GTA.Math.Vector3.Zero)
                            cv.Position = roadPos;
                    }
                    else if (dist > 30f)
                    {
                        // Re-issue escort at higher catch-up speed
                        Ped convoyDriver = cv.GetPedOnSeat(VehicleSeat.Driver);
                        if (convoyDriver != null && convoyDriver.Exists())
                        {
                            convoyDriver.Task.ClearAllImmediately();
                            float catchUpSpeed = speed + 10f;
                            int escortStyle = GetConvoyDrivingStyle(speed);
                            Function.Call(Hash.TASK_VEHICLE_ESCORT,
                                convoyDriver, cv, playerVeh,
                                -1,              // mode: behind
                                catchUpSpeed,
                                escortStyle,
                                10f,             // minDistance
                                0,               // padding
                                20f);            // noHighwaySeparation
                            convoyDriver.AlwaysKeepTask = true;
                            convoyDriver.BlockPermanentEvents = true;
                        }
                    }
                }
            }
        }

        void SpawnConvoyVehicle(Vehicle playerVeh)
        {
            // Find overflow guards not in player's vehicle
            List<Ped> overflowGuards = new List<Ped>();
            for (int i = 1; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                if (guard == null || !guard.IsAlive || !guard.Exists()) continue;
                if (!guard.IsInVehicle() || guard.CurrentVehicle != playerVeh)
                    overflowGuards.Add(guard);
            }

            if (overflowGuards.Count == 0) return;

            // Spawn vehicle matching player's model behind the player. Uses
            // FindGoodRearSpawn so the convoy doesn't materialise in an alley
            // when MapDb has data for the area; falls back to a plain rear
            // offset otherwise.
            GTA.Math.Vector3 spawnPos = FindGoodRearSpawn(playerVeh.Position, 15f);
            Model vehModel = new Model(playerVeh.Model.Hash);
            vehModel.Request(5000);
            if (!vehModel.IsLoaded)
            {
                Tolk.Speak("Could not load convoy vehicle model.");
                return;
            }

            Vehicle convoyVeh = World.CreateVehicle(vehModel, spawnPos, playerVeh.Heading);
            vehModel.MarkAsNoLongerNeeded();
            if (convoyVeh == null)
            {
                Tolk.Speak("Could not spawn convoy vehicle.");
                return;
            }

            convoyVeh.IsPersistent = true;
            convoyVehicles.Add(convoyVeh);

            // First overflow guard is convoy driver
            Ped driver = overflowGuards[0];
            driver.SetIntoVehicle(convoyVeh, VehicleSeat.Driver);

            // Fill remaining seats
            int maxSeats = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, convoyVeh);
            for (int i = 1; i < overflowGuards.Count && i <= maxSeats; i++)
            {
                overflowGuards[i].SetIntoVehicle(convoyVeh, (VehicleSeat)(i - 1));
            }

            // Set convoy driver to escort player (TASK_VEHICLE_ESCORT keeps up at high speeds)
            float escortSpeed = playerVeh.Speed + 5f;
            int escortStyle = GetConvoyDrivingStyle(playerVeh.Speed);
            Function.Call(Hash.TASK_VEHICLE_ESCORT,
                driver, convoyVeh, playerVeh,
                -1,            // mode: behind
                escortSpeed,
                escortStyle,
                10f,           // minDistance
                0,             // padding
                20f);          // noHighwaySeparation
            driver.AlwaysKeepTask = true;
            driver.BlockPermanentEvents = true;
        }

        int GetConvoyDrivingStyle(float speed)
        {
            if (speed > 44.7f) // > 100 mph: race mode
                return 2097188; // ignore pathing + avoid vehicles + avoid empty
            else if (speed > 22.3f) // > 50 mph: aggressive escort
                return 786468; // avoid vehicles + avoid empty + ignore lights + allow wrong way
            else
                return 786603; // balanced
        }

        // ============================================
        // PHASE 8B: HELICOPTER GROUND CONVOY
        // Guards drive to waypoint while player flies
        // ============================================

        void TickHeliGroundConvoy()
        {
            // Resolve current waypoint
            GTA.Math.Vector3 waypointPos = GTA.Math.Vector3.Zero;
            bool hasWaypoint = false;

            if (Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE))
            {
                int wpHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
                if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, wpHandle))
                {
                    waypointPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, wpHandle);
                    if (waypointPos != GTA.Math.Vector3.Zero)
                        hasWaypoint = true;
                }
            }

            if (!heliGroundConvoyActive)
            {
                // Not yet dispatched — dispatch only when a waypoint exists
                if (hasWaypoint)
                {
                    DispatchHeliGroundConvoy(waypointPos);
                }
                return;
            }

            // Convoy is active — check for waypoint changes or removal
            if (!hasWaypoint)
            {
                // Waypoint removed — have convoy drivers pull over
                foreach (Vehicle cv in convoyVehicles)
                {
                    if (cv == null || !cv.Exists()) continue;
                    Ped driver = cv.GetPedOnSeat(VehicleSeat.Driver);
                    if (driver != null && driver.Exists() && driver.IsAlive)
                    {
                        driver.Task.ClearAllImmediately();
                        // Action 1 = brake/stop
                        Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, driver, cv, 1, 5000);
                    }
                }
                heliGroundConvoyTarget = GTA.Math.Vector3.Zero;
                return;
            }

            // Waypoint exists — check if it changed significantly
            float waypointDelta = World.GetDistance(waypointPos, heliGroundConvoyTarget);
            if (waypointDelta > 50f)
            {
                // Waypoint changed — re-task all convoy drivers
                heliGroundConvoyTarget = waypointPos;

                // Resolve ground Z for the new waypoint
                OutputArgument groundZ = new OutputArgument();
                bool gFound = Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD,
                    waypointPos.X, waypointPos.Y, waypointPos.Z + 200f, groundZ, false);
                float targetZ = gFound ? groundZ.GetResult<float>() : waypointPos.Z;

                foreach (Vehicle cv in convoyVehicles)
                {
                    if (cv == null || !cv.Exists()) continue;
                    Ped driver = cv.GetPedOnSeat(VehicleSeat.Driver);
                    if (driver != null && driver.Exists() && driver.IsAlive)
                    {
                        driver.Task.ClearAllImmediately();
                        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                            driver, cv,
                            waypointPos.X, waypointPos.Y, targetZ,
                            HELI_GROUND_CONVOY_SPEED, HELI_GROUND_CONVOY_STYLE, 20f);
                        driver.AlwaysKeepTask = true;
                        driver.BlockPermanentEvents = true;
                    }
                }
                Tolk.Speak("Guards rerouted to new waypoint.");
            }

            // Maintenance: unstick convoy vehicles every 5 seconds
            long now = DateTime.Now.Ticks;
            if (now - heliGroundConvoyCheckTicks < 50000000) return; // 5 seconds
            heliGroundConvoyCheckTicks = now;

            foreach (Vehicle cv in convoyVehicles)
            {
                if (cv == null || !cv.Exists()) continue;
                float distToTarget = World.GetDistance(cv.Position, heliGroundConvoyTarget);

                if (distToTarget > 500f && cv.Speed < 1f)
                {
                    // Stuck — teleport to a road node closer to the waypoint
                    GTA.Math.Vector3 midpoint = (cv.Position + heliGroundConvoyTarget) * 0.5f;
                    OutputArgument outPos = new OutputArgument();
                    OutputArgument outHeading = new OutputArgument();
                    OutputArgument outLanes = new OutputArgument();
                    Function.Call(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                        midpoint.X, midpoint.Y, midpoint.Z,
                        0, outPos, outHeading, outLanes, 1, 3.0f, 0f);
                    GTA.Math.Vector3 roadPos = outPos.GetResult<GTA.Math.Vector3>();
                    if (roadPos != GTA.Math.Vector3.Zero)
                    {
                        cv.Position = roadPos;
                        // Re-task driver after teleport
                        Ped driver = cv.GetPedOnSeat(VehicleSeat.Driver);
                        if (driver != null && driver.Exists() && driver.IsAlive)
                        {
                            driver.Task.ClearAllImmediately();
                            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                                driver, cv,
                                heliGroundConvoyTarget.X, heliGroundConvoyTarget.Y, heliGroundConvoyTarget.Z,
                                HELI_GROUND_CONVOY_SPEED, HELI_GROUND_CONVOY_STYLE, 20f);
                            driver.AlwaysKeepTask = true;
                            driver.BlockPermanentEvents = true;
                        }
                    }
                }
            }
        }

        void DispatchHeliGroundConvoy(GTA.Math.Vector3 target)
        {
            // Collect overflow guards (index 1+, alive, not in the extraction helicopter)
            List<Ped> overflowGuards = new List<Ped>();
            for (int i = 1; i < bodyguards.Count; i++)
            {
                Ped guard = bodyguards[i];
                if (guard == null || !guard.IsAlive || !guard.Exists()) continue;
                // Skip guards already in the extraction helicopter
                if (extractionVehicle != null && extractionVehicle.Exists()
                    && guard.IsInVehicle() && guard.CurrentVehicle == extractionVehicle)
                    continue;
                overflowGuards.Add(guard);
            }

            if (overflowGuards.Count == 0) return;

            // Find nearest road node at ground level below the player
            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
            OutputArgument outPos = new OutputArgument();
            OutputArgument outHeading = new OutputArgument();
            OutputArgument outLanes = new OutputArgument();
            Function.Call(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                playerPos.X, playerPos.Y, playerPos.Z,
                0, outPos, outHeading, outLanes, 1, 3.0f, 0f);
            GTA.Math.Vector3 roadPos = outPos.GetResult<GTA.Math.Vector3>();
            float roadHeading = outHeading.GetResult<float>();

            if (roadPos == GTA.Math.Vector3.Zero) return;

            // Resolve ground Z for the waypoint target
            OutputArgument groundZ = new OutputArgument();
            bool gFound = Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD,
                target.X, target.Y, target.Z + 200f, groundZ, false);
            float targetZ = gFound ? groundZ.GetResult<float>() : target.Z;
            GTA.Math.Vector3 driveTarget = new GTA.Math.Vector3(target.X, target.Y, targetZ);

            // Spawn vehicle(s) — style-matched to guard model
            VehicleHash vehHash = GUARD_CONVOY_VEHICLES[guardModelIndex];
            int guardIndex = 0;

            while (guardIndex < overflowGuards.Count)
            {
                Model vehModel = new Model(vehHash);
                vehModel.Request(5000);
                if (!vehModel.IsLoaded)
                {
                    Tolk.Speak("Could not load convoy vehicle model.");
                    break;
                }

                // Offset spawn position slightly for multiple vehicles
                GTA.Math.Vector3 spawnPos = roadPos + new GTA.Math.Vector3(
                    0f, -8f * convoyVehicles.Count, 0f);
                Vehicle convoyVeh = World.CreateVehicle(vehModel, spawnPos, roadHeading);
                vehModel.MarkAsNoLongerNeeded();
                if (convoyVeh == null)
                {
                    Tolk.Speak("Could not spawn convoy vehicle.");
                    break;
                }

                convoyVeh.IsPersistent = true;
                convoyVehicles.Add(convoyVeh);

                // First guard is the driver
                Ped driver = overflowGuards[guardIndex];
                driver.SetIntoVehicle(convoyVeh, VehicleSeat.Driver);
                guardIndex++;

                // Fill passenger seats
                int maxSeats = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, convoyVeh);
                for (int s = 0; s < maxSeats && guardIndex < overflowGuards.Count; s++)
                {
                    overflowGuards[guardIndex].SetIntoVehicle(convoyVeh, (VehicleSeat)s);
                    guardIndex++;
                }

                // Task driver to drive to the waypoint
                Function.Call(Hash.SET_DRIVER_ABILITY, driver, 1.0f);
                Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, 0.5f);
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    driver, convoyVeh,
                    driveTarget.X, driveTarget.Y, driveTarget.Z,
                    HELI_GROUND_CONVOY_SPEED, HELI_GROUND_CONVOY_STYLE, 20f);
                driver.AlwaysKeepTask = true;
                driver.BlockPermanentEvents = true;
            }

            heliGroundConvoyActive = true;
            heliGroundConvoyTarget = driveTarget;
            heliGroundConvoyCheckTicks = DateTime.Now.Ticks;
            Tolk.Speak("Guards dispatched by road to waypoint.");
        }

        /// <summary>
        /// Adds a single guard to the helicopter ground convoy (used for respawned guards).
        /// </summary>
        void AddGuardToHeliGroundConvoy(Ped guard, GTA.Math.Vector3 target)
        {
            // Try to find a free seat in an existing convoy vehicle first
            foreach (Vehicle cv in convoyVehicles)
            {
                if (cv == null || !cv.Exists()) continue;
                int maxSeats = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, cv);
                for (int s = 0; s < maxSeats; s++)
                {
                    if (Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, cv, s))
                    {
                        guard.SetIntoVehicle(cv, (VehicleSeat)s);
                        return;
                    }
                }
            }

            // No free seats — spawn a new convoy vehicle for this guard
            GTA.Math.Vector3 guardPos = guard.Position;
            OutputArgument outPos = new OutputArgument();
            OutputArgument outHeading = new OutputArgument();
            OutputArgument outLanes = new OutputArgument();
            Function.Call(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                guardPos.X, guardPos.Y, guardPos.Z,
                0, outPos, outHeading, outLanes, 1, 3.0f, 0f);
            GTA.Math.Vector3 roadPos = outPos.GetResult<GTA.Math.Vector3>();
            float roadHeading = outHeading.GetResult<float>();

            if (roadPos == GTA.Math.Vector3.Zero) return;

            VehicleHash vehHash = GUARD_CONVOY_VEHICLES[guardModelIndex];
            Model vehModel = new Model(vehHash);
            vehModel.Request(5000);
            if (!vehModel.IsLoaded) return;

            Vehicle convoyVeh = World.CreateVehicle(vehModel, roadPos, roadHeading);
            vehModel.MarkAsNoLongerNeeded();
            if (convoyVeh == null) return;

            convoyVeh.IsPersistent = true;
            convoyVehicles.Add(convoyVeh);
            guard.SetIntoVehicle(convoyVeh, VehicleSeat.Driver);

            Function.Call(Hash.SET_DRIVER_ABILITY, guard, 1.0f);
            Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, guard, 0.5f);
            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                guard, convoyVeh,
                target.X, target.Y, target.Z,
                HELI_GROUND_CONVOY_SPEED, HELI_GROUND_CONVOY_STYLE, 20f);
            guard.AlwaysKeepTask = true;
            guard.BlockPermanentEvents = true;
        }

        // ============================================
        // PHASE 9: EMERGENCY EXTRACTION
        // ============================================
        void ButlerGroundExtraction()
        {
            // Check if we can reuse the existing butler instead of spawning a new one
            bool reuseExistingButler = bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive;

            float spawnDist = GROUND_EXTRACTION_DISTANCES[groundExtractionDistanceIndex];
            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

            // Find nearest road at the configured spawn distance
            GTA.Math.Vector3 searchPos = (spawnDist > 0f)
                ? playerPos + Game.Player.Character.ForwardVector * spawnDist
                : playerPos; // Instant warp: find road near player

            OutputArgument outPos = new OutputArgument();
            OutputArgument outHeading = new OutputArgument();
            OutputArgument outLanes = new OutputArgument();
            Function.Call(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                searchPos.X, searchPos.Y, searchPos.Z,
                0, outPos, outHeading, outLanes, 1, 3.0f, 0f);
            GTA.Math.Vector3 roadPos = outPos.GetResult<GTA.Math.Vector3>();
            float roadHeading = outHeading.GetResult<float>();

            if (roadPos == GTA.Math.Vector3.Zero)
            {
                Tolk.Speak("Could not find a road for extraction.");
                return;
            }

            // Spawn vehicle
            Model vehModel = new Model(VehicleHash.Oracle);
            vehModel.Request(5000);
            if (!vehModel.IsLoaded)
            {
                Tolk.Speak("Could not load extraction vehicle.");
                return;
            }
            extractionVehicle = World.CreateVehicle(vehModel, roadPos, roadHeading);
            vehModel.MarkAsNoLongerNeeded();
            if (extractionVehicle == null)
            {
                Tolk.Speak("Could not spawn extraction vehicle.");
                return;
            }
            extractionVehicle.IsPersistent = true;
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, extractionVehicle, true, true);

            Ped butler;
            if (reuseExistingButler)
            {
                // Reuse existing butler - reset driving state if active
                butler = bodyguards[0];
                if (guardDriverActive)
                {
                    guardDriverActive = false;
                    isAutodriving = false;
                    autodriveWanderMode = false;
                }
            }
            else
            {
                // Spawn new Butler
                Model pedModel = new Model(GUARD_MODELS[guardModelIndex].hash);
                pedModel.Request(5000);
                if (!pedModel.IsLoaded)
                {
                    Tolk.Speak("Could not load Butler model.");
                    if (extractionVehicle != null && extractionVehicle.Exists()) extractionVehicle.Delete();
                    return;
                }

                butler = World.CreatePed(pedModel, roadPos);
                pedModel.MarkAsNoLongerNeeded();
                if (butler == null)
                {
                    Tolk.Speak("Could not spawn Butler.");
                    if (extractionVehicle != null && extractionVehicle.Exists()) extractionVehicle.Delete();
                    return;
                }

                // Replace or add Butler
                if (bodyguards.Count > 0)
                {
                    if (bodyguards[0] != null && bodyguards[0].Exists())
                        bodyguards[0].Delete();
                    bodyguards[0] = butler;
                }
                else
                {
                    bodyguards.Add(butler);
                }
            }

            SetupGuardRelationship(butler);
            SetupGuardAttributes(butler, 0);
            // SetIntoVehicle handles warping butler out of any current vehicle automatically
            butler.SetIntoVehicle(extractionVehicle, VehicleSeat.Driver);

            if (spawnDist == 0f)
            {
                // Instant spawn and warp: put player aboard immediately
                VehicleSeat seat = FindFirstFreePassengerSeat(extractionVehicle);
                Game.Player.Character.SetIntoVehicle(extractionVehicle, seat);
                guardDriverActive = true;
                if (bodyguardGroupId >= 0)
                    Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, butler, bodyguardGroupId);
                Tolk.Speak("Ground extraction ready. You're aboard.");
            }
            else
            {
                // Drive to player; tick monitor will linearly ramp speed down on approach.
                // Override with conservative driving style during extraction approach:
                // stop before vehicles/peds, avoid vehicles/empty/peds/objects, stop at lights, use blinkers.
                // Keeps player's specified speed but removes reckless/wrong-way flags for safer arrival.
                extractionInProgress = true;
                extractionIsHeli = false;
                extractionSpawnDistance = World.GetDistance(roadPos, playerPos);
                int extractionStyle = 1 | 2 | 4 | 8 | 16 | 32 | 128 | 256; // 447 = conservative
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    butler, extractionVehicle,
                    playerPos.X, playerPos.Y, playerPos.Z,
                    autodriveSpeed, extractionStyle, 20f);
                butler.AlwaysKeepTask = true;
                butler.BlockPermanentEvents = true;
                Tolk.Speak("Ground extraction inbound.");
            }
        }

        void ButlerHelicopterExtraction()
        {
            // Check if we can reuse the existing butler instead of spawning a new one
            bool reuseExistingButler = bodyguards.Count > 0 && bodyguards[0] != null && bodyguards[0].IsAlive;

            float spawnDist = HELI_EXTRACTION_DISTANCES[heliExtractionDistanceIndex];
            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

            // Position helicopter at configured distance (or near player for instant warp)
            GTA.Math.Vector3 spawnPos = (spawnDist > 0f)
                ? playerPos + new GTA.Math.Vector3(spawnDist, 0f, autopilotAltitude)
                : playerPos + new GTA.Math.Vector3(0f, 0f, autopilotAltitude);

            // Spawn helicopter
            Model heliModel = new Model(VehicleHash.Swift2);
            heliModel.Request(5000);
            if (!heliModel.IsLoaded)
            {
                Tolk.Speak("Could not load helicopter.");
                return;
            }
            extractionVehicle = World.CreateVehicle(heliModel, spawnPos, 0f);
            heliModel.MarkAsNoLongerNeeded();
            if (extractionVehicle == null)
            {
                Tolk.Speak("Could not spawn helicopter.");
                return;
            }
            extractionVehicle.IsPersistent = true;
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, extractionVehicle, true, true);

            Ped butler;
            if (reuseExistingButler)
            {
                // Reuse existing butler - reset driving state if active
                butler = bodyguards[0];
                if (guardDriverActive)
                {
                    guardDriverActive = false;
                    isAutodriving = false;
                    autodriveWanderMode = false;
                }
            }
            else
            {
                // Spawn new Butler
                Model pedModel = new Model(GUARD_MODELS[guardModelIndex].hash);
                pedModel.Request(5000);
                if (!pedModel.IsLoaded)
                {
                    Tolk.Speak("Could not load Butler model.");
                    if (extractionVehicle != null && extractionVehicle.Exists()) extractionVehicle.Delete();
                    return;
                }

                butler = World.CreatePed(pedModel, spawnPos);
                pedModel.MarkAsNoLongerNeeded();
                if (butler == null)
                {
                    Tolk.Speak("Could not spawn Butler.");
                    if (extractionVehicle != null && extractionVehicle.Exists()) extractionVehicle.Delete();
                    return;
                }

                if (bodyguards.Count > 0)
                {
                    if (bodyguards[0] != null && bodyguards[0].Exists())
                        bodyguards[0].Delete();
                    bodyguards[0] = butler;
                }
                else
                {
                    bodyguards.Add(butler);
                }
            }

            SetupGuardRelationship(butler);
            SetupGuardAttributes(butler, 0);
            // SetIntoVehicle handles warping butler out of any current vehicle automatically
            butler.SetIntoVehicle(extractionVehicle, VehicleSeat.Driver);

            if (spawnDist == 0f)
            {
                // Instant spawn and warp: put player aboard immediately
                VehicleSeat seat = FindFirstFreePassengerSeat(extractionVehicle);
                Game.Player.Character.SetIntoVehicle(extractionVehicle, seat);
                guardDriverActive = true;
                if (bodyguardGroupId >= 0)
                    Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, butler, bodyguardGroupId);
                Tolk.Speak("Helicopter extraction ready. You're aboard.");
            }
            else
            {
                // Fly to player at full autodrive speed; tick monitor will linearly
                // ramp speed down as the helicopter approaches the player
                extractionInProgress = true;
                extractionIsHeli = true;
                heliLandingPhase = false;
                heliLandingTarget = GTA.Math.Vector3.Zero;
                extractionSpawnDistance = spawnDist;
                heliTaskReissueTicks = 0; // allow first tick re-issue immediately
                // Remove butler from ped group so native group-follow AI
                // doesn't compete with TASK_HELI_MISSION during flight
                Function.Call(Hash.REMOVE_PED_FROM_GROUP, butler);
                Function.Call(Hash.TASK_HELI_MISSION,
                    butler, extractionVehicle, 0, 0,
                    playerPos.X, playerPos.Y, playerPos.Z + autopilotAltitude,
                    4, autodriveSpeed, 20f, -1f,
                    (int)(playerPos.Z + autopilotAltitude + 60f), (int)(playerPos.Z - 10),
                    -1f, 0);
                butler.AlwaysKeepTask = true;
                butler.BlockPermanentEvents = true;
                Tolk.Speak("Helicopter extraction inbound.");
            }
        }

        private Vehicle extractionVehicle;
        private bool extractionIsHeli = false;
        private float extractionSpawnDistance = 0f;

        void CleanupExtractionVehicle()
        {
            if (extractionVehicle != null && extractionVehicle.Exists())
            {
                extractionVehicle.IsPersistent = false;
                extractionVehicle.MarkAsNoLongerNeeded();
            }
            extractionVehicle = null;
            postExtractionAutoEngage = false;
        }

        void TickExtractionMonitor()
        {
            if (!extractionInProgress) return;

            // Butler died or extraction vehicle gone — cancel and clean up
            if (bodyguards.Count == 0 || bodyguards[0] == null || !bodyguards[0].IsAlive ||
                extractionVehicle == null || !extractionVehicle.Exists())
            {
                extractionInProgress = false;
                heliLandingPhase = false;
                CleanupExtractionVehicle();
                return;
            }

            Ped butler = bodyguards[0];
            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;
            float dist = World.GetDistance(butler.Position, playerPos);

            // Player entered a vehicle manually - cancel extraction
            if (Game.Player.Character.IsInVehicle() && Game.Player.Character.CurrentVehicle != extractionVehicle)
            {
                extractionInProgress = false;
                heliLandingPhase = false;
                CleanupExtractionVehicle();
                return;
            }

            if (extractionIsHeli)
            {
                if (!heliLandingPhase)
                {
                    // Phase 1: Helicopter is flying toward player - use horizontal distance
                    // (3D distance includes altitude offset and may never drop below threshold)
                    float hDistToPlayer = World.GetDistance(
                        new GTA.Math.Vector3(extractionVehicle.Position.X, extractionVehicle.Position.Y, 0f),
                        new GTA.Math.Vector3(playerPos.X, playerPos.Y, 0f));

                    // Speed ramp: slow helicopter as it nears the landing threshold.
                    // Altitude stays at cruise — TASK_HELI_MISSION mode 4 does not
                    // reliably descend, so we defer all descent to mode 6 (landing phase).
                    float minApproachSpeed = 4.4704f; // 10 mph in m/s
                    float speedRampDist = Math.Max(autodriveSpeed * 4f, HELI_LANDING_THRESHOLD + 100f);
                    float t = Math.Max(0f, Math.Min(1f, (hDistToPlayer - HELI_LANDING_THRESHOLD) / (speedRampDist - HELI_LANDING_THRESHOLD)));
                    float rampedSpeed = minApproachSpeed + t * (autodriveSpeed - minApproachSpeed);

                    // Throttle task re-issue to every ~5 seconds so the AI can fly smoothly
                    long now = DateTime.Now.Ticks;
                    if (now - heliTaskReissueTicks >= 50000000) // 5 seconds
                    {
                        heliTaskReissueTicks = now;
                        float targetZ = playerPos.Z + autopilotAltitude;
                        butler.AlwaysKeepTask = false;
                        Function.Call(Hash.CLEAR_PED_TASKS, butler);
                        Function.Call(Hash.TASK_HELI_MISSION,
                            butler, extractionVehicle, 0, 0,
                            playerPos.X, playerPos.Y, targetZ,
                            4, rampedSpeed, 20f, -1f,
                            (int)(targetZ + 60f), (int)(playerPos.Z - 10),
                            -1f, 0);
                        butler.AlwaysKeepTask = true;
                    }

                    if (hDistToPlayer < HELI_LANDING_THRESHOLD)
                    {
                        // Enter landing phase - begin iterative landing search
                        heliLandingPhase = true;
                        heliLandingSearchRadius = 20f;
                        heliLandingSearchPointIndex = 0;
                        heliLandingTargetActive = false;
                        heliLandingTargetTicks = 0;
                        Tolk.Speak("Searching for landing zone.");
                    }
                }
                else
                {
                    // Phase 2: Iterative landing search with expanding radius.
                    // Tries candidate spots around the circumference of increasing radii,
                    // re-issuing TASK_HELI_MISSION mode 6 for each flat candidate.
                    // Gives the AI 15 seconds per candidate before moving on.
                    if (heliLandingTargetActive)
                    {
                        // A landing candidate has been issued - check if heli landed
                        float hDist = World.GetDistance(
                            new GTA.Math.Vector3(extractionVehicle.Position.X, extractionVehicle.Position.Y, 0f),
                            new GTA.Math.Vector3(heliLandingTarget.X, heliLandingTarget.Y, 0f));
                        float altAboveLanding = extractionVehicle.Position.Z - heliLandingTarget.Z;

                        if (hDist < 20f && altAboveLanding < 3f)
                        {
                            // Successfully landed
                            Function.Call(Hash.CLEAR_PED_TASKS, butler);
                            Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, extractionVehicle, 5.0f);
                            Function.Call(Hash.SET_VEHICLE_ENGINE_ON, extractionVehicle, true, true, false);
                            Function.Call(Hash.SET_HELI_BLADES_FULL_SPEED, extractionVehicle);

                            extractionInProgress = false;
                            heliLandingPhase = false;
                            guardDriverActive = true;
                            if (bodyguardGroupId >= 0)
                                Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, butler, bodyguardGroupId);

                            if (heliManualLanding)
                            {
                                heliManualLanding = false;
                                isAutodriving = false;
                                Tolk.Speak("Helicopter landed.");
                            }
                            else
                            {
                                postExtractionAutoEngage = true;
                                postExtractionIsHeli = true;
                                Tolk.Speak("Helicopter landed. Butler standing by.");
                            }
                        }
                        else
                        {
                            long elapsed = DateTime.Now.Ticks - heliLandingTargetTicks;

                            // Force-land: if heli is stalled close to target Z, place it on ground
                            float altAboveTarget = extractionVehicle.Position.Z - heliLandingTarget.Z;
                            if (altAboveTarget < 3f && elapsed >= 300000000) // within 3m of target Z and 30+ seconds
                            {
                                Function.Call(Hash.CLEAR_PED_TASKS, butler);
                                Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, extractionVehicle, 5.0f);
                                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, extractionVehicle, true, true, false);
                                Function.Call(Hash.SET_HELI_BLADES_FULL_SPEED, extractionVehicle);

                                extractionInProgress = false;
                                heliLandingPhase = false;
                                guardDriverActive = true;
                                if (bodyguardGroupId >= 0)
                                    Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, butler, bodyguardGroupId);

                                if (heliManualLanding)
                                {
                                    heliManualLanding = false;
                                    isAutodriving = false;
                                    Tolk.Speak("Helicopter landed.");
                                }
                                else
                                {
                                    postExtractionAutoEngage = true;
                                    postExtractionIsHeli = true;
                                    Tolk.Speak("Helicopter landed. Butler standing by.");
                                }
                            }
                            // Hard timeout: 60 seconds per candidate to allow full descent
                            // from cruise altitude (~200m) at 30 mph with final approach slowdown.
                            else if (elapsed >= 600000000) // 60 seconds
                            {
                                heliLandingTargetActive = false;
                            }
                        }
                    }

                    // Search for next valid landing candidate (only if no active target and still extracting)
                    if (!heliLandingTargetActive && extractionInProgress)
                    {
                        int pointsPerRing = 8;
                        bool found = false;

                        // Scan current ring for a flat terrain candidate
                        while (heliLandingSearchPointIndex < pointsPerRing)
                        {
                            float angle = (float)(heliLandingSearchPointIndex * 2.0 * Math.PI / pointsPerRing);
                            float testX = playerPos.X + heliLandingSearchRadius * (float)Math.Cos(angle);
                            float testY = playerPos.Y + heliLandingSearchRadius * (float)Math.Sin(angle);

                            OutputArgument groundZ = new OutputArgument();
                            bool gFound = Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD,
                                testX, testY, playerPos.Z + 200f, groundZ, false);

                            if (gFound)
                            {
                                float z = groundZ.GetResult<float>();
                                if (z >= 1f)
                                {
                                    // Check flatness: sample 4 cardinal offsets (±4m for helicopter-sized pad)
                                    bool isFlat = true;
                                    float checkOffset = 4f;
                                    float[] offX = { checkOffset, -checkOffset, 0f, 0f };
                                    float[] offY = { 0f, 0f, checkOffset, -checkOffset };

                                    for (int j = 0; j < 4; j++)
                                    {
                                        OutputArgument neighborZ = new OutputArgument();
                                        bool nFound = Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD,
                                            testX + offX[j], testY + offY[j], playerPos.Z + 200f, neighborZ, false);
                                        if (!nFound) { isFlat = false; break; }
                                        float nz = neighborZ.GetResult<float>();
                                        if (Math.Abs(nz - z) > 1.5f) { isFlat = false; break; }
                                    }

                                    if (isFlat)
                                    {
                                        // Valid candidate - issue landing task ONCE with offset
                                        heliLandingTarget = new GTA.Math.Vector3(testX, testY, z);
                                        heliLandingTargetActive = true;
                                        heliLandingTargetTicks = DateTime.Now.Ticks;
                                        heliLandingStartAltitude = extractionVehicle.Position.Z;

                                        // Offset landing target away from player to avoid rotor collision
                                        float dx = testX - playerPos.X;
                                        float dy = testY - playerPos.Y;
                                        float len = (float)Math.Sqrt(dx * dx + dy * dy);
                                        if (len < 1f) len = 1f;
                                        float nx = dx / len;
                                        float ny = dy / len;
                                        float safetyOffset = 8f;
                                        float offsetX = testX + nx * safetyOffset;
                                        float offsetY = testY + ny * safetyOffset;

                                        butler.AlwaysKeepTask = false;
                                        Function.Call(Hash.CLEAR_PED_TASKS, butler);
                                        Function.Call(Hash.TASK_HELI_MISSION,
                                            butler, extractionVehicle, 0, 0,
                                            offsetX, offsetY, z,
                                            20, 40.2336f, -1f, -1f,
                                            -1, -1,
                                            -1f, 96);   // flags: LandOnArrival | DontDoAvoidance
                                        butler.AlwaysKeepTask = true;

                                        Tolk.Speak("Trying landing zone at " + (int)heliLandingSearchRadius + " meters.");
                                        heliLandingSearchPointIndex++;
                                        found = true;
                                        break;
                                    }
                                }
                            }

                            heliLandingSearchPointIndex++;
                        }

                        if (!found)
                        {
                            // All points at current radius exhausted - expand to next ring
                            heliLandingSearchRadius += 10f;
                            heliLandingSearchPointIndex = 0;

                            if (heliLandingSearchRadius > 100f)
                            {
                                // Exhausted all candidates within 100m - warp player aboard
                                if (!Game.Player.Character.IsInVehicle())
                                {
                                    VehicleSeat seat = FindFirstFreePassengerSeat(extractionVehicle);
                                    Game.Player.Character.SetIntoVehicle(extractionVehicle, seat);
                                }
                                Tolk.Speak("No safe landing zone within 100 meters. Warped aboard helicopter.");
                                extractionInProgress = false;
                                heliLandingPhase = false;
                                guardDriverActive = true;
                                if (bodyguardGroupId >= 0)
                                    Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, butler, bodyguardGroupId);
                            }
                        }
                    }
                }
            }
            else
            {
                // Adaptive ground speed ramp: scales deceleration distance with speed
                // so the vehicle arrives smoothly at 5 mph regardless of cruise speed setting
                float minGroundSpeed = 2.2352f; // 5 mph in m/s
                float groundRampDist = Math.Max(autodriveSpeed * 6f, 60f); // ~6s braking window
                float groundRampStart = Math.Max(extractionSpawnDistance, groundRampDist);
                float tGround = Math.Max(0f, Math.Min(1f, (dist - 20f) / (groundRampStart - 20f)));
                float groundSpeed = minGroundSpeed + tGround * (autodriveSpeed - minGroundSpeed);
                Function.Call(Hash.SET_DRIVE_TASK_CRUISE_SPEED, butler, groundSpeed);

                // Check if arrived (20m parking distance for safer approach)
                if (dist < 20f)
                {
                    // Honk to announce arrival
                    if (extractionVehicle != null && extractionVehicle.Exists())
                        Function.Call(Hash.START_VEHICLE_HORN, extractionVehicle, 2000, 0, false);
                    Tolk.Speak("Butler arrived. Set a waypoint for a destination.");
                    extractionInProgress = false;
                    guardDriverActive = true;
                    postExtractionAutoEngage = true;
                    postExtractionIsHeli = false;
                    if (bodyguardGroupId >= 0)
                        Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, butler, bodyguardGroupId);
                }
            }
        }

        /// <summary>
        /// Scans for a flat landing spot for a helicopter within the given radius of center.
        /// Checks concentric rings of points for flat terrain suitable for helicopter landing.
        /// Returns Vector3.Zero if no suitable spot is found.
        /// </summary>
        GTA.Math.Vector3 FindHeliLandingSpot(GTA.Math.Vector3 center, float maxRadius)
        {
            float bestDist = float.MaxValue;
            GTA.Math.Vector3 bestSpot = GTA.Math.Vector3.Zero;

            // Scan in concentric rings at increasing radii
            float[] radii = { 15f, 30f, 50f, 70f, 100f };
            int pointsPerRing = 12; // Every 30 degrees

            foreach (float radius in radii)
            {
                if (radius > maxRadius) break;

                for (int i = 0; i < pointsPerRing; i++)
                {
                    float angle = (float)(i * 2.0 * Math.PI / pointsPerRing);
                    float testX = center.X + radius * (float)Math.Cos(angle);
                    float testY = center.Y + radius * (float)Math.Sin(angle);

                    // Get ground height at this point
                    OutputArgument groundZ = new OutputArgument();
                    bool found = Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD,
                        testX, testY, center.Z + 200f, groundZ, false);
                    if (!found) continue;
                    float z = groundZ.GetResult<float>();

                    // Skip points at water level or suspiciously low
                    if (z < 1f) continue;

                    // Check flatness: sample 4 cardinal offsets (±4m for helicopter-sized pad)
                    bool isFlat = true;
                    float checkOffset = 4f;
                    float[] offX = { checkOffset, -checkOffset, 0f, 0f };
                    float[] offY = { 0f, 0f, checkOffset, -checkOffset };

                    for (int j = 0; j < 4; j++)
                    {
                        OutputArgument neighborZ = new OutputArgument();
                        bool nFound = Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD,
                            testX + offX[j], testY + offY[j], center.Z + 200f, neighborZ, false);
                        if (!nFound) { isFlat = false; break; }
                        float nz = neighborZ.GetResult<float>();
                        if (Math.Abs(nz - z) > 1.5f) { isFlat = false; break; }
                    }

                    if (!isFlat) continue;

                    // Valid landing spot - track the closest one to player
                    float spotDist = (float)Math.Sqrt(
                        (testX - center.X) * (testX - center.X) +
                        (testY - center.Y) * (testY - center.Y));
                    if (spotDist < bestDist)
                    {
                        bestDist = spotDist;
                        bestSpot = new GTA.Math.Vector3(testX, testY, z);
                    }
                }
            }

            return bestSpot;
        }

        // ============================================
        // PHASE 9: GUARD CALLOUTS
        // ============================================
        void TickGuardCallouts()
        {
            if (DateTime.Now.Ticks - lastCalloutTicks < 50000000) return; // 5 seconds

            trackedEnemies.RemoveAll(p => p == null || p.IsDead || !p.Exists());

            if (trackedEnemies.Count == 0)
            {
                // Check if we just finished combat
                if (guardCurrentTarget.Count > 0 || wasInCombatForCallout)
                {
                    lastCalloutTicks = DateTime.Now.Ticks;
                    wasInCombatForCallout = false;
                    Tolk.Speak("Area clear.");
                }
                return;
            }

            wasInCombatForCallout = true;
            lastCalloutTicks = DateTime.Now.Ticks;

            // Find closest new threat for directional callout
            Ped closest = null;
            float closestDist = float.MaxValue;
            foreach (Ped enemy in trackedEnemies)
            {
                if (enemy == null || !enemy.Exists()) continue;
                float d = World.GetDistance(Game.Player.Character.Position, enemy.Position);
                if (d < closestDist) { closestDist = d; closest = enemy; }
            }

            if (closest == null) return;

            // Direction using dot product (same math as enemy detection)
            GTA.Math.Vector3 toEnemy = closest.Position - Game.Player.Character.Position;
            GTA.Math.Vector3 toEnemyNorm = GTA.Math.Vector3.Normalize(toEnemy);
            GTA.Math.Vector3 forwardVec = Game.Player.Character.ForwardVector;
            GTA.Math.Vector3 rightVec = new GTA.Math.Vector3(forwardVec.Y, -forwardVec.X, 0);

            float dotFwd = GTA.Math.Vector3.Dot(forwardVec, toEnemyNorm);
            float dotRight = GTA.Math.Vector3.Dot(rightVec, toEnemyNorm);

            string direction;
            if (dotFwd > 0.5f)
                direction = "ahead";
            else if (dotFwd < -0.5f)
                direction = "behind";
            else if (dotRight > 0)
                direction = "right";
            else
                direction = "left";

            // Build callout with tactical info
            string callout = "Contact " + direction;
            int dist = (int)closestDist;
            callout += ", " + dist + " meters";

            // Add tactical state info for active guards
            int engagingCount = 0;
            int flankingCount = 0;
            int suppressingCount = 0;
            int protectingCount = 0;
            foreach (var kvp in guardCombatState)
            {
                switch (kvp.Value)
                {
                    case GuardCombatState.Engaging: engagingCount++; break;
                    case GuardCombatState.Flanking: flankingCount++; break;
                    case GuardCombatState.Suppressing: suppressingCount++; break;
                    case GuardCombatState.Protecting: protectingCount++; break;
                }
            }

            if (flankingCount > 0) callout += ". Flanking";
            if (suppressingCount > 0) callout += ". Suppressing";

            Tolk.Speak(callout + "!");
        }

        private bool wasInCombatForCallout = false;

        // ============================================
        // PHASE 9: BUTLER AUDIO BEACON
        // ============================================
        void TickButlerBeacon()
        {
            if (bodyguards.Count == 0 || bodyguards[0] == null || !bodyguards[0].IsAlive) return;
            if (DateTime.Now.Ticks - lastBeaconTicks < 30000000) return; // 3 seconds
            lastBeaconTicks = DateTime.Now.Ticks;

            Ped butler = bodyguards[0];
            float dist = World.GetDistance(Game.Player.Character.Position, butler.Position);

            // Only beep when Butler is > 5m away
            if (dist < 5f) return;

            // Calculate panning direction
            GTA.Math.Vector3 toButler = butler.Position - Game.Player.Character.Position;
            GTA.Math.Vector3 toButlerNorm = GTA.Math.Vector3.Normalize(toButler);
            GTA.Math.Vector3 forwardVec = Game.Player.Character.ForwardVector;
            GTA.Math.Vector3 rightVec = new GTA.Math.Vector3(forwardVec.Y, -forwardVec.X, 0);

            float dotRight = GTA.Math.Vector3.Dot(rightVec, toButlerNorm);
            float pan = Math.Max(-1f, Math.Min(1f, dotRight));

            // Pitch based on distance: close = 800Hz, far = 400Hz
            float distFactor = Math.Min(dist / 100f, 1f);
            float freq = 800 - (distFactor * 400);

            // Dispose and recreate WaveOutEvent each tick to avoid format mismatch
            // (SignalGenerator is mono but PanningSampleProvider outputs stereo;
            // WaveOutEvent doesn't reliably handle channel count changes on re-Init)
            try { outBeacon?.Stop(); outBeacon?.Dispose(); } catch { }
            outBeacon = new WaveOutEvent();
            beaconBeep.Gain = 0.08;
            beaconBeep.Frequency = freq;
            beaconBeep.Type = SignalGeneratorType.Sin; // Sine wave (distinct from enemy SawTooth)

            var beaconSample = beaconBeep.Take(TimeSpan.FromSeconds(0.1));
            var beaconPanned = new PanningSampleProvider(beaconSample) { Pan = pan };
            outBeacon.Init(beaconPanned);
            outBeacon.Play();
        }

        // ============================================
        // PHASE 9: BUTLER POI NARRATION
        // ============================================
        void TickButlerPOINarration()
        {
            if (bodyguards.Count == 0 || bodyguards[0] == null || !bodyguards[0].IsAlive) return;
            if (DateTime.Now.Ticks - lastPOITicks < 100000000) return; // 10 seconds
            lastPOITicks = DateTime.Now.Ticks;

            GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

            // Check common POI blip types
            int[] poiTypes = { 110, 89, 72, 108, 93, 478, 73 };
            string[] poiNames = { "Ammu-Nation", "Hospital", "Garage", "Police Station", "Store", "Barber", "Mod Shop" };

            for (int i = 0; i < poiTypes.Length; i++)
            {
                int blipHandle = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, poiTypes[i]);
                while (Function.Call<bool>(Hash.DOES_BLIP_EXIST, blipHandle))
                {
                    GTA.Math.Vector3 blipPos = Function.Call<GTA.Math.Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blipHandle);
                    float dist = World.GetDistance(playerPos, blipPos);

                    if (dist < 100f)
                    {
                        int blipId = blipHandle;
                        if (!announcedPOIBlips.Contains(blipId))
                        {
                            announcedPOIBlips.Add(blipId);
                            Tolk.Speak(poiNames[i] + " nearby.");
                            return; // One announcement per tick
                        }
                    }
                    else if (dist > 200f)
                    {
                        // Reset so it can be announced again on next visit
                        announcedPOIBlips.Remove(blipHandle);
                    }

                    blipHandle = Function.Call<int>(Hash.GET_NEXT_BLIP_INFO_ID, poiTypes[i]);
                }
            }
        }

        VehicleSeat FindFirstFreePassengerSeat(Vehicle veh)
        {
            int maxPassengers = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, veh);
            for (int seat = 0; seat < maxPassengers; seat++)
            {
                if (Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, veh, seat))
                {
                    return (VehicleSeat)seat;
                }
            }
            return VehicleSeat.Passenger; // Fallback
        }

        /// <summary>
        /// Called when the script is aborted - clean up resources
        /// </summary>
        private void onAborted(object sender, EventArgs e)
        {
            try { if (driveLogger != null) driveLogger.Stop(); } catch { }

            DismissAllGuards();

            // Dispose audio resources to prevent handle leaks on script reload
            WaveOutEvent[] audioOutputs = { out1, out2, out3, out11, out12, out13, out14, out15,
                outNavLeft, outNavCenter, outNavRight, outNavBehind, outWaypoint, outEnemy,
                outBeacon, outPickup, outWater, outDropoff, outCover, outInteract,
                outMissionBeep, outHit, outHeadshot, outKill, outDoor, outLadder,
                outPartCycle, outSteerAssist, outBrakeWarn };
            foreach (var wo in audioOutputs)
            {
                try { if (wo != null) { wo.Stop(); wo.Dispose(); } } catch { }
            }

            IDisposable[] readers = { tped, tvehicle, tprop, pickupSound, coverSound,
                interactSound, hitSound, headshotSound, killSound, doorSound, ladderSound };
            foreach (var r in readers)
            {
                try { if (r != null) r.Dispose(); } catch { }
            }
        }

        // ===================================================================
        // DRIVE ASSIST DEBUG LOGGING
        // ===================================================================

        // Compact Vector3 formatter for log lines.
        private static string FmtV(GTA.Math.Vector3 v)
        {
            return "(" + v.X.ToString("F1") + "," + v.Y.ToString("F1") + "," + v.Z.ToString("F1") + ")";
        }

        // Float formatter that renders the "no threat" sentinels (999 / MaxValue) as "none".
        private static string FmtF(float f)
        {
            if (f >= 999f || f == float.MaxValue) return "none";
            return f.ToString("F2");
        }

        // Per-frame structured telemetry for the whole drive-assist feature. Reads
        // existing state fields and emits one FRAME block via the background logger.
        // Called from onTick only while driveAssistDebugLog is on and a driving
        // system is active. veh is null when the player is on foot (auto-walk).
        private void LogDriveAssistFrame(Vehicle veh)
        {
            try
            {
                Ped player = Game.Player.Character;
                GTA.Math.Vector3 pos = veh != null ? veh.Position : player.Position;
                float heading = veh != null ? veh.Heading : player.Heading;
                float speed = veh != null ? veh.Speed : player.Velocity.Length();

                // Deltas vs. the previous logged frame.
                float dPlanar = 0f, dHeading = 0f, dZ = 0f;
                if (driveLogFrameCount > 0)
                {
                    float ddx = pos.X - lastLoggedPos.X;
                    float ddy = pos.Y - lastLoggedPos.Y;
                    dPlanar = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                    dHeading = heading - lastLoggedHeading;
                    while (dHeading > 180f) dHeading -= 360f;
                    while (dHeading < -180f) dHeading += 360f;
                    dZ = pos.Z - lastLoggedZ;
                }
                bool bigZ = driveLogFrameCount > 0 && Math.Abs(dZ) > DRIVE_LOG_BIG_Z_THRESHOLD;

                long nowTicks = DateTime.Now.Ticks;
                float steerScanAgeMs = (nowTicks - steeringAssistTicks) / 10000f;
                float brakeThreatAgeMs = cachedBrakeThreatStamp > 0
                    ? (nowTicks - cachedBrakeThreatStamp) / 10000f : -1f;
                float brakeThreatDist = cachedBrakeThreatStamp > 0
                    ? pos.DistanceTo(cachedBrakeThreatPos) : -1f;

                StringBuilder sb = new StringBuilder(1200);
                sb.AppendLine("FRAME " + driveLogFrameCount
                    + " t=" + DateTime.Now.ToString("HH:mm:ss.fff")
                    + " dt=" + deltaTime.ToString("F4"));
                sb.AppendLine("  veh: " + (veh != null
                        ? ("aircraft=" + veh.IsAircraft + " boat=" + veh.IsBoat)
                        : "ON-FOOT")
                    + " pos=(" + pos.X.ToString("F2") + "," + pos.Y.ToString("F2") + "," + pos.Z.ToString("F2") + ")"
                    + " heading=" + heading.ToString("F1")
                    + " speed=" + speed.ToString("F2") + "m/s/" + (speed * 2.23694f).ToString("F1") + "mph"
                    + " reversing=" + isReversing);
                sb.AppendLine("  delta: planar=" + dPlanar.ToString("F3") + "m"
                    + " heading=" + dHeading.ToString("F2") + "deg"
                    + " z=" + dZ.ToString("F3") + "m" + (bigZ ? "  <<BIG_Z_CHANGE>>" : ""));
                sb.AppendLine("  road: onValidRoad=" + isOnValidRoad
                    + " headingDelta=" + roadHeadingDelta.ToString("F1")
                    + " lastValidRoadDist=" + lastValidRoadDistance.ToString("F1")
                    + " skewed=" + vehicleIsSkewed + " skewAngle=" + vehicleSkewAngle.ToString("F1")
                    + " offRoadDurMs=" + (offRoadStartTicks > 0
                        ? ((nowTicks - offRoadStartTicks) / 10000f).ToString("F0") : "0"));
                sb.AppendLine("  path: points=" + pathPolyline.Count
                    + " fromGps=" + pathPolylineFromGps
                    + " p0=" + (pathPolyline.Count > 0 ? FmtV(pathPolyline[0]) : "-")
                    + " p1=" + (pathPolyline.Count > 1 ? FmtV(pathPolyline[1]) : "-")
                    + " p2=" + (pathPolyline.Count > 2 ? FmtV(pathPolyline[2]) : "-"));
                sb.AppendLine("  mode: drive=" + currentDriveMode
                    + " laneKeepFailStreak=" + laneKeepFailureStreak
                    + " alignReverse=" + alignmentEngageReverse
                    + " hasRecoveryTarget=" + hasRecoveryTarget
                    + " recoveryPos=" + FmtV(recoveryTargetPos)
                    + " recoveryHeading=" + recoveryTargetHeading.ToString("F1")
                    + " recoveryDist=" + recoveryTargetDistance.ToString("F1"));
                sb.AppendLine("  threat: steerTTC=" + FmtF(threatTimeToCollision)
                    + " dir=" + threatDirection + " type=" + threatType
                    + " closestThreatPos=" + FmtV(closestThreatPosition)
                    + " brakeObstacle=" + closestBrakeObstacleType
                    + " brakeThreatDist=" + brakeThreatDist.ToString("F1")
                    + " brakeThreatAgeMs=" + brakeThreatAgeMs.ToString("F0")
                    + " adjLatchL=" + adjacentLatchedLeft + " adjLatchR=" + adjacentLatchedRight);
                sb.AppendLine("  navDist: L=" + FmtF(navAssistDistLeft)
                    + " C=" + FmtF(navAssistDistCenter)
                    + " R=" + FmtF(navAssistDistRight)
                    + " B=" + FmtF(navAssistDistBehind));
                sb.AppendLine("  decision: steerCmd=" + cachedSteerCorrection.ToString("F3")
                    + " smoothedSteer=" + smoothedSteerCorrection.ToString("F3")
                    + " prevFrameSteer=" + previousFrameSteer.ToString("F3")
                    + " roadCorrection=" + roadSteerCorrection.ToString("F3")
                    + " smoothedRoadCorr=" + smoothedRoadCorrection.ToString("F3")
                    + " lastRoadCorr=" + lastRoadCorrection.ToString("F3")
                    + " avoidDir=" + cachedAvoidDirection);
                sb.AppendLine("  brake: brakeCmd=" + cachedBrakeMagnitude.ToString("F3")
                    + " rampedBrake=" + rampedBrakeInput.ToString("F3")
                    + " handbrake=" + cachedHandbrakeMagnitude.ToString("F3")
                    + " isBraking=" + cachedIsBraking
                    + " emergencyBrake=" + emergencyBrakeActive
                    + " fullMode=" + cachedIsFullMode
                    + " assistActive=" + steeringAssistActive
                    + " scanAgeMs=" + steerScanAgeMs.ToString("F0"));
                if (isAutodriving)
                {
                    float destDist = autodriveWanderMode ? -1f : pos.DistanceTo(autodriveDestination);
                    sb.AppendLine("  autodrive: active=true wander=" + autodriveWanderMode
                        + " dest=" + FmtV(autodriveDestination)
                        + " destDist=" + destDist.ToString("F1")
                        + " speed=" + autodriveSpeed.ToString("F2") + "m/s/"
                        + (autodriveSpeed * 2.23694f).ToString("F1") + "mph");
                }

                driveLogger.Write(sb.ToString().TrimEnd());

                lastLoggedPos = pos;
                lastLoggedHeading = heading;
                lastLoggedZ = pos.Z;
                driveLogFrameCount++;
            }
            catch { }
        }

        // Iter-9 Patch C: reset all location-specific drive-assist state.
        // Called from:
        //   - the planar-jump branch in ProcessSteeringAssist (covers player
        //     long-range teleports and any other code path that warps the
        //     vehicle without touching the handle)
        //   - CheckRoadTeleport's 3 fire sites (covers the mod-initiated
        //     teleports whose short jumps the planar-jump gate misses)
        // Also schedules a 500 ms post-teleport brake hold so the car doesn't
        // continue closing on whatever's at the new location while the next
        // 1-2 scans rebuild a fresh threat picture.
        private void ResetForTeleport(string source)
        {
            cachedBrakeThreatPos = GTA.Math.Vector3.Zero;
            cachedBrakeThreatVel = GTA.Math.Vector3.Zero;
            cachedBrakeThreatStamp = 0;
            cachedBrakeThreatFirstSeenStamp = 0;
            cachedSteerThreatPos = GTA.Math.Vector3.Zero;
            cachedSteerThreatVel = GTA.Math.Vector3.Zero;
            cachedSteerThreatStamp = 0;
            emergencyBrakeActive = false;
            wasObstacleInBrakeZone = false;
            criticalZoneArmFrames = 0;
            brakeArmed = false;
            rampedBrakeInput = 0f;
            cachedBrakeMagnitude = 0f;
            cachedSteerCorrection = 0f;
            smoothedSteerCorrection = 0f;
            smoothedRoadCorrection = 0f;
            lastRoadCorrection = 0f;
            cachedAvoidDirection = 0;
            lastNonZeroAvoidDir = 0;
            lastNonZeroAvoidDirTicks = 0;
            proposedAvoidDirection = 0;
            avoidDirHoldFrames = 0;
            // Iter-11 Patch I: reset distance history on teleport so the
            // post-teleport rising-distance check isn't fooled by the jump.
            cachedBrakeDistHistoryValid = false;
            cachedBrakeDistIdx = 0;
            // Iter-12 Patch Q: post-teleport position is on a road by
            // construction (TeleportToNearestRoad puts us on the nearest
            // valid node). Clear the off-named-street tracking so we don't
            // immediately re-trigger the off-road override at the new spot.
            offNamedStreetSinceTicks = 0;
            offNamedStreetLogged = false;
            // Path polyline rebuilds on the next BuildPathPolyline call;
            // clearing it here prevents Stanley from steering toward an OLD
            // road node before the rebuild runs.
            pathPolyline.Clear();
            ppGoalInitialized = false;
            hasRecoveryTarget = false;
            recoveryTargetPos = GTA.Math.Vector3.Zero;
            recoveryTargetHeading = 0f;
            recoveryTargetDistance = 999f;
            // A 30 deg post-teleport heading diff vs a stale road tangent
            // isn't a real skew, so don't carry the streak across.
            skewFailureStreak = 0;
            laneKeepFailureStreak = 0;
            // Schedule the post-teleport brake hold.
            postTeleportBrakeHoldUntilTicks = DateTime.Now.Ticks + POST_TELEPORT_BRAKE_HOLD_TICKS;
            // Update the position cache so the planar-jump check doesn't
            // re-fire next scan from the same data.
            try
            {
                Vehicle v = Game.Player.Character != null && Game.Player.Character.IsInVehicle()
                    ? Game.Player.Character.CurrentVehicle : null;
                if (v != null) lastDriveAssistVehiclePos = v.Position;
            }
            catch { }
            if (driveLogger != null && driveLogger.IsRunning)
                driveLogger.Write("[F" + driveLogFrameCount
                    + "] EVENT teleport-reset: source=" + source);
            RecordDriveDecision("teleport-reset: source=" + source);
        }

        // Pushes one discrete drive-assist decision onto the ring buffer. Kept
        // cheap so it can be called from the per-tick decision paths. The F1
        // failure snapshot reads the last 5 back out.
        private void RecordDriveDecision(string decision)
        {
            try
            {
                driveDecisionLog[driveDecisionLogCount % driveDecisionLog.Length] =
                    "[F" + driveLogFrameCount + " " + DateTime.Now.ToString("HH:mm:ss")
                    + "] " + decision;
                driveDecisionLogCount++;
            }
            catch { }
        }

        // Writes a "player-indicated failure" snapshot to the drive-assist log.
        // Triggered by F1 so a play-tester can independently mark a failure
        // moment; the block captures everything needed to diagnose it later.
        private void LogPlayerIndicatedFailure()
        {
            if (driveLogger == null || !driveLogger.IsRunning)
            {
                Tolk.Speak("Failure marker needs drive assist debug logging turned on.", true);
                return;
            }
            if (LogCollisionSnapshot("PLAYER-INDICATED-FAILURE", null))
                Tolk.Speak("Failure marked in drive assist log.", true);
            else
                Tolk.Speak("Failure marker error.", true);
        }

        // Shared snapshot writer used by the F1 player marker (Patch F) and by
        // auto-collision detection (Patch G). The tag distinguishes them in
        // the log so post-hoc analysis can either merge or separate the two
        // populations. Extra is a one-line annotation appended to the marker
        // header (auto-collision uses it to record healthDelta=...). Returns
        // true on success.
        private bool LogCollisionSnapshot(string tag, string extra)
        {
            try
            {
                if (driveLogger == null || !driveLogger.IsRunning) return false;

                Ped player = Game.Player.Character;
                Vehicle veh = player.IsInVehicle() ? player.CurrentVehicle : null;
                GTA.Math.Vector3 pos = veh != null ? veh.Position : player.Position;
                collisionMarkerSeq++;

                StringBuilder sb = new StringBuilder(2800);
                sb.AppendLine("==================================================");
                sb.AppendLine("[F" + driveLogFrameCount + "] " + tag + " #" + collisionMarkerSeq
                    + (string.IsNullOrEmpty(extra) ? "" : "  " + extra)
                    + "  t=" + DateTime.Now.ToString("HH:mm:ss.fff"));
                sb.AppendLine("==================================================");

                // 1. Exact vehicle coordinates.
                sb.AppendLine("  coords: (" + pos.X.ToString("F4") + ","
                    + pos.Y.ToString("F4") + "," + pos.Z.ToString("F4") + ")"
                    + (veh == null ? "  <ON-FOOT>" : ""));

                // 2. Nearest vehicles within 25 m. World.GetNearbyVehicles
                //    returns nearest-first; the player's own vehicle is skipped.
                sb.AppendLine("  nearby-vehicles (25m):");
                int vehShown = 0;
                foreach (Vehicle v in World.GetNearbyVehicles(pos, 25f))
                {
                    if (v == null || !v.Exists()) continue;
                    if (veh != null && v.Handle == veh.Handle) continue;
                    string vn = v.LocalizedName;
                    if (string.IsNullOrEmpty(vn) || vn == "NULL") vn = v.DisplayName;
                    sb.AppendLine("    - " + vn
                        + " dist=" + pos.DistanceTo(v.Position).ToString("F1") + "m"
                        + " pos=" + FmtV(v.Position)
                        + " speed=" + v.Speed.ToString("F1") + "m/s");
                    if (++vehShown >= 8) break;
                }
                if (vehShown == 0) sb.AppendLine("    (none)");

                // 3. Nearest detected obstacles within 25 m: peds and props,
                //    plus the assist's live directional shape-cast readings.
                sb.AppendLine("  nearby-obstacles (25m):");
                int obsShown = 0;
                foreach (Ped p in World.GetNearbyPeds(pos, 25f))
                {
                    if (p == null || !p.Exists()) continue;
                    if (p.Handle == player.Handle) continue;
                    sb.AppendLine("    - ped handle=" + p.Handle
                        + " dist=" + pos.DistanceTo(p.Position).ToString("F1") + "m"
                        + " pos=" + FmtV(p.Position));
                    if (++obsShown >= 6) break;
                }
                int propShown = 0;
                foreach (Prop pr in World.GetNearbyProps(pos, 25f))
                {
                    if (pr == null || !pr.Exists()) continue;
                    sb.AppendLine("    - prop model=" + pr.Model.Hash
                        + " dist=" + pos.DistanceTo(pr.Position).ToString("F1") + "m"
                        + " pos=" + FmtV(pr.Position));
                    if (++propShown >= 6) break;
                }
                if (obsShown == 0 && propShown == 0) sb.AppendLine("    (none)");
                sb.AppendLine("    shapecast: L=" + FmtF(navAssistDistLeft) + "/" + navAssistTypeLeft
                    + " C=" + FmtF(navAssistDistCenter) + "/" + navAssistTypeCenter
                    + " R=" + FmtF(navAssistDistRight) + "/" + navAssistTypeRight
                    + " B=" + FmtF(navAssistDistBehind) + "/" + navAssistTypeBehind);

                // 4. Nearest road node.
                OutputArgument nodeOut = new OutputArgument();
                OutputArgument nodeHd  = new OutputArgument();
                OutputArgument nodeLn  = new OutputArgument();
                bool nodeOk = Function.Call<bool>(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                    pos.X, pos.Y, pos.Z, 1, nodeOut, nodeHd, nodeLn, 1, 3.0f, 0f);
                if (nodeOk)
                {
                    GTA.Math.Vector3 nodePos = nodeOut.GetResult<GTA.Math.Vector3>();
                    sb.AppendLine("  nearest-road-node: pos=" + FmtV(nodePos)
                        + " dist=" + pos.DistanceTo(nodePos).ToString("F1") + "m"
                        + " heading=" + nodeHd.GetResult<float>().ToString("F1"));
                }
                else
                {
                    sb.AppendLine("  nearest-road-node: (none found)");
                }

                // 5. Pitch and roll of the vehicle.
                if (veh != null)
                {
                    GTA.Math.Vector3 rot = veh.Rotation;
                    sb.AppendLine("  attitude: pitch=" + rot.X.ToString("F1")
                        + " roll=" + rot.Y.ToString("F1"));
                }
                else
                {
                    sb.AppendLine("  attitude: (on-foot)");
                }

                // 6. Whether the vehicle is in water.
                sb.AppendLine("  inWater: " + (veh != null ? veh.IsInWater : player.IsInWater));

                // 7. Vehicle health.
                if (veh != null)
                {
                    sb.AppendLine("  health: body=" + veh.BodyHealth.ToString("F0")
                        + " engine=" + veh.EngineHealth.ToString("F0")
                        + " overall=" + veh.HealthFloat.ToString("F0"));
                }
                else
                {
                    sb.AppendLine("  health: (on-foot) player=" + player.HealthFloat.ToString("F0"));
                }

                // 8. Cached threat dump (Iter-9 Patch F). If a marker shows
                //    obstacles nearby but no brake, this section tells us
                //    whether the threat was cached but didn't make it through
                //    the gates, vs. never cached at all.
                long now = DateTime.Now.Ticks;
                if (cachedBrakeThreatPos != GTA.Math.Vector3.Zero)
                {
                    long ageMs = cachedBrakeThreatStamp > 0
                        ? (now - cachedBrakeThreatStamp) / 10000 : -1;
                    long firstSeenMs = cachedBrakeThreatFirstSeenStamp > 0
                        ? (now - cachedBrakeThreatFirstSeenStamp) / 10000 : -1;
                    sb.AppendLine("  cached-brake-threat: pos=" + FmtV(cachedBrakeThreatPos)
                        + " ageMs=" + ageMs + " firstSeenMs=" + firstSeenMs
                        + " velMag=" + cachedBrakeThreatVel.Length().ToString("F1"));
                }
                else
                {
                    sb.AppendLine("  cached-brake-threat: (none)");
                }
                if (cachedSteerThreatPos != GTA.Math.Vector3.Zero)
                {
                    long sAgeMs = cachedSteerThreatStamp > 0
                        ? (now - cachedSteerThreatStamp) / 10000 : -1;
                    sb.AppendLine("  cached-steer-threat: pos=" + FmtV(cachedSteerThreatPos)
                        + " ageMs=" + sAgeMs
                        + " velMag=" + cachedSteerThreatVel.Length().ToString("F1"));
                }
                else
                {
                    sb.AppendLine("  cached-steer-threat: (none)");
                }

                // 9. Brake-pipeline detail (Iter-9 Patch F). Distinguishes
                //    "no brake fired" from "brake fired but blended down" etc.
                sb.AppendLine("  brake-detail: armed=" + brakeArmed
                    + " rampedBrake=" + rampedBrakeInput.ToString("F2")
                    + " cachedBrakeMag=" + cachedBrakeMagnitude.ToString("F2")
                    + " recoveryBrake=" + recoveryBrakeRequest.ToString("F2")
                    + " curveBrake=" + curveBrakeRequest.ToString("F2")
                    + " accBrake=" + accBrakeOut.ToString("F2")
                    + " emergency=" + emergencyBrakeActive
                    + " handbrake=" + cachedHandbrakeMagnitude.ToString("F2"));

                // 10. Lane / road / skew detail.
                long skewMs = persistentSkewStartTicks > 0
                    ? (now - persistentSkewStartTicks) / 10000 : 0;
                sb.AppendLine("  road-detail: polylineCount=" + pathPolyline.Count
                    + " closestPolySeg=" + lastClosestPolySeg
                    + " skewMs=" + skewMs
                    + " skewStreak=" + skewFailureStreak
                    + " failStreak=" + laneKeepFailureStreak
                    + " roadCorrSmoothed=" + smoothedRoadCorrection.ToString("F2")
                    + " mode=" + currentDriveMode);

                // 11. Vehicle-handling dump from the iter-8 registry (only
                //     meaningful when in a vehicle).
                if (veh != null)
                {
                    int gtaClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, veh);
                    VehicleAIHandlingInfo aiInfo = VehicleAIHandlingRegistry.GetForVehicle(veh);
                    sb.AppendLine("  vehicle-handling: gtaClass=" + gtaClass
                        + " aiHandling=" + (aiInfo != null ? aiInfo.Name : "(null)")
                        + " Min=" + (aiInfo != null ? aiInfo.MinBrakeDistance.ToString("F1") : "?")
                        + " Max=" + (aiInfo != null ? aiInfo.MaxBrakeDistance.ToString("F1") : "?")
                        + " MaxAt=" + (aiInfo != null ? aiInfo.MaxSpeedAtBrakeDistance.ToString("F1") : "?")
                        + " source=" + VehicleAIHandlingRegistry.LoadedFrom);
                }

                // 12. Most recent drive-assist decisions (newest last). Ring
                //     size was bumped 5 -> 30 in iter-9 Patch F.
                sb.AppendLine("  recent-decisions (newest last):");
                int total = driveDecisionLogCount;
                if (total == 0)
                {
                    sb.AppendLine("    (none recorded)");
                }
                else
                {
                    int show = total < driveDecisionLog.Length ? total : driveDecisionLog.Length;
                    for (int i = show; i >= 1; i--)
                    {
                        int idx = (total - i) % driveDecisionLog.Length;
                        sb.AppendLine("    - " + driveDecisionLog[idx]);
                    }
                }

                sb.AppendLine("==================================================");
                driveLogger.Write(sb.ToString().TrimEnd());
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ===================================================================
        // PATH-AWARE DRIVE ASSIST — implementations
        //
        // BuildPathPolyline is the single source of truth for "where should
        // the vehicle go in the next 5-80 m." Everything downstream (Stanley
        // steering, NPC corridor filtering) consumes the same `pathPolyline`
        // list. The path is recomputed once per detection tick to keep
        // ApplyCachedSteeringInputs cheap.
        // ===================================================================

        private bool HasGpsRoute()
        {
            // GET_GPS_BLIP_ROUTE_FOUND — true when the player has set a
            // waypoint AND the engine has finished computing a path to it.
            try { return Function.Call<bool>((Hash)HASH_GET_GPS_BLIP_ROUTE_FOUND); }
            catch { return false; }
        }

        private bool TrySampleGpsRoute(float distAhead, out GTA.Math.Vector3 outPos)
        {
            // GET_POS_ALONG_GPS_TYPE_ROUTE(out Vector3, BOOL startAtPlayer,
            //                              float dist, int slotType)
            // slotType 0 = Waypoint route. Returns false past route end.
            outPos = GTA.Math.Vector3.Zero;
            try
            {
                OutputArgument outArg = new OutputArgument();
                bool ok = Function.Call<bool>((Hash)HASH_GET_POS_ALONG_GPS_ROUTE,
                    outArg, false, distAhead, 0);
                if (!ok) return false;
                outPos = outArg.GetResult<GTA.Math.Vector3>();
                // Sometimes returns origin when route ends; sanity-check.
                if (outPos.LengthSquared() < 1f) return false;
                return true;
            }
            catch { return false; }
        }

        // =====================================================================
        // ROLLING-HISTORY BUFFER — see field declaration block for rationale.
        // All "ForLastMs" predicates return false when the buffer doesn't yet
        // span the requested window. First ~200 ms after session start or a
        // vehicle change therefore falls back to legacy behavior rather than
        // mis-firing on confident-looking sustained-evidence assertions.
        // =====================================================================
        private bool TryGetSnapshot(int framesBack, out DriveAssistSnapshot s)
        {
            if (framesBack < 0 || framesBack >= historyCount)
            {
                s = default(DriveAssistSnapshot);
                return false;
            }
            int idx = historyHead - 1 - framesBack;
            while (idx < 0) idx += HISTORY_CAPACITY;
            s = history[idx];
            return true;
        }

        private bool HistorySpansAtLeastMs(int ms)
        {
            if (historyCount < 2) return false;
            DriveAssistSnapshot newest, oldest;
            if (!TryGetSnapshot(0, out newest)) return false;
            if (!TryGetSnapshot(historyCount - 1, out oldest)) return false;
            long spanMs = (newest.TimestampTicks - oldest.TimestampTicks) / 10000;
            return spanMs >= ms;
        }

        // All "InLastMs" predicates fail-safe to false unless the buffer has
        // enough span to make the claim. Polled, not subscribed — cheap given
        // the buffer is at most 32 entries.
        private bool WasOnValidRoadForLastMs(int ms)
        {
            if (!HistorySpansAtLeastMs(ms)) return false;
            long nowTicks = DateTime.Now.Ticks;
            long cutoffTicks = nowTicks - (long)ms * 10000;
            for (int i = 0; i < historyCount; i++)
            {
                DriveAssistSnapshot s;
                if (!TryGetSnapshot(i, out s)) return false;
                if (s.TimestampTicks < cutoffTicks) break;
                if (!s.IsOnValidRoad) return false;
            }
            return true;
        }

        private bool AnyLaneKeepOkRawInLastMs(int ms)
        {
            if (!HistorySpansAtLeastMs(ms)) return false;
            long nowTicks = DateTime.Now.Ticks;
            long cutoffTicks = nowTicks - (long)ms * 10000;
            for (int i = 0; i < historyCount; i++)
            {
                DriveAssistSnapshot s;
                if (!TryGetSnapshot(i, out s)) return false;
                if (s.TimestampTicks < cutoffTicks) break;
                if (s.LaneKeepOkRaw) return true;
            }
            return false;
        }

        // Fraction of snapshots in the last `ms` window that report LaneKeepOkRaw.
        // Used by the sustained-failure gate so a single noisy OK frame doesn't
        // veto recovery for the whole confirm window.
        private float FractionLaneKeepOkInLastMs(int ms)
        {
            if (!HistorySpansAtLeastMs(ms)) return 0f;
            long nowTicks = DateTime.Now.Ticks;
            long cutoffTicks = nowTicks - (long)ms * 10000;
            int okCount = 0;
            int total = 0;
            for (int i = 0; i < historyCount; i++)
            {
                DriveAssistSnapshot s;
                if (!TryGetSnapshot(i, out s)) break;
                if (s.TimestampTicks < cutoffTicks) break;
                total++;
                if (s.LaneKeepOkRaw) okCount++;
            }
            if (total == 0) return 0f;
            return (float)okCount / (float)total;
        }

        private float MaxAbsSteerCmdInLastMs(int ms)
        {
            float maxAbs = 0f;
            long nowTicks = DateTime.Now.Ticks;
            long cutoffTicks = nowTicks - (long)ms * 10000;
            for (int i = 0; i < historyCount; i++)
            {
                DriveAssistSnapshot s;
                if (!TryGetSnapshot(i, out s)) break;
                if (s.TimestampTicks < cutoffTicks) break;
                float a = Math.Abs(s.CachedSteerCorrection);
                if (a > maxAbs) maxAbs = a;
            }
            return maxAbs;
        }

        private bool VehicleStoppedForLastMs(float speedMs, int ms)
        {
            if (!HistorySpansAtLeastMs(ms)) return false;
            long nowTicks = DateTime.Now.Ticks;
            long cutoffTicks = nowTicks - (long)ms * 10000;
            for (int i = 0; i < historyCount; i++)
            {
                DriveAssistSnapshot s;
                if (!TryGetSnapshot(i, out s)) return false;
                if (s.TimestampTicks < cutoffTicks) break;
                if (s.SpeedMs > speedMs) return false;
            }
            return true;
        }

        // Single canonical write site — called once per ProcessSteeringAssist
        // tick after all per-frame computation is final.
        private void PushDriveAssistSnapshot(Vehicle veh,
            float steerTTC, float brakeTTC,
            bool hasSteerThreat, bool hasBrakeThreat)
        {
            DriveAssistSnapshot s = new DriveAssistSnapshot();
            s.TimestampTicks = DateTime.Now.Ticks;
            s.FrameCount     = driveLogFrameCount;
            s.DeltaTimeSec   = deltaTime;

            if (veh != null)
            {
                s.SpeedMs   = veh.Speed;
                s.Position  = veh.Position;
                s.Heading   = veh.Heading;
            }

            // iter-13 Patch S2: break the self-fulfilling re-lock. The mode
            // block force-sets isOnValidRoad=true on every LaneKeeping re-entry
            // (so downstream steering has a guidance source during a blip), and
            // WasOnValidRoadForLastMs reads THIS field — so a car pinned 53 deg
            // skewed at 0 speed kept "confirming" it was on-road and re-locking
            // LaneKeeping every ~1.5 s (Sandy Shores trap). Record what is
            // physically true into the rolling history: a demonstrably skewed +
            // stopped car is NOT lane-keeping, so the return-to-LaneKeeping
            // confirm can no longer be satisfied while it sits there. Narrowly
            // conjoined (skew + near-stopped) so normal stop-and-go pointed down
            // the lane (skew ~ 0) still records on-road and returns promptly.
            bool physicallyOnLane = isOnValidRoad
                && !(Math.Abs(roadHeadingDelta) > LANEKEEP_SKEW_FAIL_ANGLE
                     && veh != null && veh.Speed < 1.0f);
            s.IsOnValidRoad       = physicallyOnLane;
            s.RoadHeadingDelta    = roadHeadingDelta;
            s.LaneLateralError    = lastLaneLateralError;
            s.ClosestPolySegIdx   = lastClosestPolySeg;
            s.PolylinePointCount  = pathPolyline != null ? pathPolyline.Count : 0;

            s.Mode                   = currentDriveMode;
            s.LaneKeepFailureStreak  = laneKeepFailureStreak;
            // LaneKeepOkRaw is the pre-hysteresis "are we on the road right
            // now" signal. Mode-transition gates that read the buffer must use
            // this raw signal, NOT the post-hysteresis decision, or they
            // become self-referential.
            s.LaneKeepOkRaw          = isOnValidRoad;

            s.CachedSteerCorrection    = cachedSteerCorrection;
            s.SmoothedSteerCorrection  = smoothedSteerCorrection;
            s.SmoothedRoadCorrection   = smoothedRoadCorrection;
            s.CachedAvoidDirection     = cachedAvoidDirection;
            s.CachedBrakeMagnitude     = cachedBrakeMagnitude;
            s.EmergencyBrakeActive     = emergencyBrakeActive;

            s.SteerTTC               = steerTTC;
            s.BrakeTTC               = brakeTTC;
            s.CachedBrakeThreatPos   = cachedBrakeThreatPos;
            s.CachedBrakeThreatStamp = cachedBrakeThreatStamp;
            s.HasSteerThreat         = hasSteerThreat;
            s.HasBrakeThreat         = hasBrakeThreat;

            s.NavDistCenter = navAssistDistCenter;
            s.NavDistLeft   = navAssistDistLeft;
            s.NavDistRight  = navAssistDistRight;
            s.NavDistBehind = navAssistDistBehind;

            history[historyHead] = s;
            historyHead = (historyHead + 1) % HISTORY_CAPACITY;
            if (historyCount < HISTORY_CAPACITY) historyCount++;
        }

        /// <summary>Composes pathPolyline ahead of the vehicle. Sets
        /// pathPolylineFromGps. Polyline is empty if no source is available
        /// (truly off-road and node dump missing) — callers must handle
        /// that case.</summary>
        private void BuildPathPolyline(Vehicle veh)
        {
            BuildPathPolylineRaw(veh);
            StabilizePolyline();
        }

        /// <summary>Flip hysteresis: if the freshly-rebuilt polyline's lead
        /// direction flips hard versus the last good one, keep the previous
        /// polyline for a few scans. The node search momentarily snapping to a
        /// parallel/oncoming road produced 150-220 deg heading jumps in the
        /// debug log; a genuine turn changes the lead heading gradually and
        /// persists past the short hold window.</summary>
        private void StabilizePolyline()
        {
            if (pathPolyline.Count >= 2 && prevPathPolyline.Count >= 2
                && polylineHoldCount < POLYLINE_MAX_HOLD)
            {
                // Skip hysteresis after a teleport / large relocation — a far
                // p0 means a new area, not a node-snap flip.
                float p0dx = pathPolyline[0].X - prevPathPolyline[0].X;
                float p0dy = pathPolyline[0].Y - prevPathPolyline[0].Y;
                bool sameArea = (p0dx * p0dx + p0dy * p0dy) < (50f * 50f);

                float ax = pathPolyline[1].X - pathPolyline[0].X;
                float ay = pathPolyline[1].Y - pathPolyline[0].Y;
                float bx = prevPathPolyline[1].X - prevPathPolyline[0].X;
                float by = prevPathPolyline[1].Y - prevPathPolyline[0].Y;
                float dot = ax * bx + ay * by;
                float cross = ax * by - ay * bx;
                float diffDeg = (float)(Math.Atan2(cross, dot) * 57.29578);
                if (sameArea && Math.Abs(diffDeg) > POLYLINE_FLIP_ANGLE)
                {
                    pathPolyline.Clear();
                    pathPolyline.AddRange(prevPathPolyline);
                    polylineHoldCount++;
                    return;
                }
            }
            polylineHoldCount = 0;
            prevPathPolyline.Clear();
            prevPathPolyline.AddRange(pathPolyline);
        }

        private void BuildPathPolylineRaw(Vehicle veh)
        {
            pathPolyline.Clear();
            pathPolylineFromGps = false;
            if (veh == null) return;

            GTA.Math.Vector3 vehPos = veh.Position;
            GTA.Math.Vector3 vehFwd = veh.ForwardVector;
            // Flatten — Stanley control runs on the XY plane.
            GTA.Math.Vector3 flatFwd = new GTA.Math.Vector3(vehFwd.X, vehFwd.Y, 0f);
            if (flatFwd.LengthSquared() < 0.0001f) return;
            flatFwd.Normalize();

            // SOURCE 1: GPS route. This is the AI's planned path — exactly
            // what NPC drivers would follow if you handed them the same
            // waypoint. Use it whenever available.
            if (HasGpsRoute())
            {
                GTA.Math.Vector3 prev = vehPos;
                for (float d = PATH_GPS_SAMPLE_MIN; d <= PATH_POLYLINE_MAX_LOOKAHEAD; d += PATH_GPS_SAMPLE_STEP)
                {
                    GTA.Math.Vector3 pt;
                    if (!TrySampleGpsRoute(d, out pt)) break;
                    // Skip near-duplicate consecutive samples.
                    GTA.Math.Vector3 delta = pt - prev;
                    if (delta.X * delta.X + delta.Y * delta.Y < 1f) continue;
                    pathPolyline.Add(pt);
                    prev = pt;
                }
                if (pathPolyline.Count >= 2)
                {
                    pathPolylineFromGps = true;
                    return;
                }
                pathPolyline.Clear();
            }

            // SOURCE 2: static node graph. WalkAlongDirection respects
            // ForwardLaneCount > 0 (one-way correctness) and picks the
            // straightest neighbour at each junction — i.e. "what the AI
            // driver would do here."
            if (NodeGraph.IsLoaded)
            {
                int startIdx = NodeGraph.FindNearestNode(vehPos, STATIC_NODE_SEARCH_RADIUS);
                if (startIdx >= 0)
                {
                    GTA.Math.Vector3 startPos = NodeGraph.GetPosition(startIdx);
                    // Z-GUARD: reject a start node stacked on a different deck.
                    if (Math.Abs(startPos.Z - vehPos.Z) <= PATH_NODE_MAX_Z_DELTA)
                    {
                        var walked = NodeGraph.WalkAlongDirection(startIdx, flatFwd, PATH_POLYLINE_HOPS);
                        pathPolyline.Add(startPos);
                        float prevZ = startPos.Z;
                        for (int i = 0; i < walked.Count; i++)
                        {
                            // Truncate the walk if it jumps decks mid-path.
                            if (Math.Abs(walked[i].Z - prevZ) > PATH_NODE_MAX_Z_DELTA) break;
                            pathPolyline.Add(walked[i]);
                            prevZ = walked[i].Z;
                        }
                        if (pathPolyline.Count >= 2) return;
                        pathPolyline.Clear(); // too short after Z-truncation — fall through
                    }
                }
            }

            // SOURCE 3: live native (last resort). Step forward and ask the
            // game for nearest road nodes at successive distances. Worse
            // than the static graph (this is what the old impl did and it
            // jittered) but better than nothing if the dump is missing.
            GTA.Math.Vector3 lastSample = vehPos;
            for (int step = 1; step <= PATH_POLYLINE_HOPS; step++)
            {
                GTA.Math.Vector3 probe = vehPos + flatFwd * (step * 12f);
                OutputArgument outPos = new OutputArgument();
                OutputArgument outHd  = new OutputArgument();
                OutputArgument outLn  = new OutputArgument();
                bool ok = Function.Call<bool>(Hash.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                    probe.X, probe.Y, probe.Z, 1, outPos, outHd, outLn, 1, 3.0f, 0f);
                if (!ok) break;
                GTA.Math.Vector3 nodePos = outPos.GetResult<GTA.Math.Vector3>();
                // Z-GUARD: skip a node stacked on a different deck.
                if (Math.Abs(nodePos.Z - lastSample.Z) > PATH_NODE_MAX_Z_DELTA) continue;
                GTA.Math.Vector3 delta = nodePos - lastSample;
                if (delta.X * delta.X + delta.Y * delta.Y < 4f) continue; // dedupe
                pathPolyline.Add(nodePos);
                lastSample = nodePos;
            }
        }

        /// <summary>2D distance (XY only) from point to the polyline. Returns
        /// float.MaxValue if polyline is empty.</summary>
        private float DistanceToPolyline2D(GTA.Math.Vector3 point)
        {
            int count = pathPolyline.Count;
            if (count == 0) return float.MaxValue;
            if (count == 1)
            {
                float dx0 = point.X - pathPolyline[0].X;
                float dy0 = point.Y - pathPolyline[0].Y;
                return (float)Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            }
            float bestDsq = float.MaxValue;
            for (int i = 0; i < count - 1; i++)
            {
                GTA.Math.Vector3 a = pathPolyline[i];
                GTA.Math.Vector3 b = pathPolyline[i + 1];
                float abx = b.X - a.X, aby = b.Y - a.Y;
                float lenSq = abx * abx + aby * aby;
                float cx, cy;
                if (lenSq < 0.01f) { cx = a.X; cy = a.Y; }
                else
                {
                    float apx = point.X - a.X, apy = point.Y - a.Y;
                    float t = (apx * abx + apy * aby) / lenSq;
                    if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                    cx = a.X + abx * t;
                    cy = a.Y + aby * t;
                }
                float dx = point.X - cx, dy = point.Y - cy;
                float dsq = dx * dx + dy * dy;
                if (dsq < bestDsq) bestDsq = dsq;
            }
            return (float)Math.Sqrt(bestDsq);
        }

        /// <summary>Is `point` close enough to the upcoming path that the
        /// drive assist should treat it as a threat to react to? When no
        /// polyline is available (truly off-road) returns true — better to
        /// over-react than to ignore NPCs entirely.</summary>
        private bool IsInPathCorridor(GTA.Math.Vector3 point, float corridorM)
        {
            if (pathPolyline.Count < 2) return true;
            return DistanceToPolyline2D(point) <= corridorM;
        }

        /// <summary>Stanley lane-keeping controller against the prebuilt
        /// pathPolyline. Returns a normalized steer input in [-1, 1] where
        /// +1 = full right. Also sets isOnValidRoad / roadHeadingDelta for
        /// the legacy state machine.</summary>
        private float ComputeStanleySteer(Vehicle veh)
        {
            if (pathPolyline.Count < 2)
            {
                isOnValidRoad = false;
                roadHeadingDelta = 0f;
                // iter-13 Patch V: the lane just ran out. If we were moving fast
                // when it vanished, stamp it so ComputeCurveBrake governs speed
                // down instead of letting the car coast off the end at speed.
                if (veh != null && veh.Speed > LANE_END_SAFE_SPEED)
                    laneCollapseAtSpeedTicks = DateTime.Now.Ticks;
                return 0f;
            }

            GTA.Math.Vector3 vehPos = veh.Position;
            GTA.Math.Vector3 vehFwd = veh.ForwardVector;
            GTA.Math.Vector3 vehRight = veh.RightVector;
            float speed = veh.Speed;

            GTA.Math.Vector3 flatFwd = new GTA.Math.Vector3(vehFwd.X, vehFwd.Y, 0f);
            if (flatFwd.LengthSquared() < 0.0001f)
            {
                isOnValidRoad = false; roadHeadingDelta = 0f; return 0f;
            }
            flatFwd.Normalize();
            GTA.Math.Vector3 flatRight = new GTA.Math.Vector3(vehRight.X, vehRight.Y, 0f);
            if (flatRight.LengthSquared() < 0.0001f)
            {
                isOnValidRoad = false; roadHeadingDelta = 0f; return 0f;
            }
            flatRight.Normalize();

            // Front axle ~ half a wheelbase ahead of the vehicle origin.
            GTA.Math.Vector3 frontAxle = vehPos + flatFwd * (PURE_PURSUIT_WHEELBASE * 0.5f);

            // Find the closest polyline segment to the front axle.
            int bestSeg = 0;
            float bestDsq = float.MaxValue;
            float bestCx = pathPolyline[0].X, bestCy = pathPolyline[0].Y;
            for (int i = 0; i < pathPolyline.Count - 1; i++)
            {
                GTA.Math.Vector3 a = pathPolyline[i];
                GTA.Math.Vector3 b = pathPolyline[i + 1];
                float abx = b.X - a.X, aby = b.Y - a.Y;
                float lenSq = abx * abx + aby * aby;
                float cx, cy;
                if (lenSq < 0.01f) { cx = a.X; cy = a.Y; }
                else
                {
                    float apx = frontAxle.X - a.X, apy = frontAxle.Y - a.Y;
                    float t = (apx * abx + apy * aby) / lenSq;
                    if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                    cx = a.X + abx * t;
                    cy = a.Y + aby * t;
                }
                float dx = frontAxle.X - cx, dy = frontAxle.Y - cy;
                float dsq = dx * dx + dy * dy;
                if (dsq < bestDsq) { bestDsq = dsq; bestSeg = i; bestCx = cx; bestCy = cy; }
            }
            // Expose the closest segment for ComputeCurveBrake's lookahead.
            lastClosestPolySeg = bestSeg;

            // Segment tangent at the closest point. For the last segment we
            // already used [bestSeg, bestSeg+1].
            GTA.Math.Vector3 segA = pathPolyline[bestSeg];
            GTA.Math.Vector3 segB = pathPolyline[bestSeg + 1];
            float segDx = segB.X - segA.X, segDy = segB.Y - segA.Y;
            float segLen = (float)Math.Sqrt(segDx * segDx + segDy * segDy);
            if (segLen < 0.01f)
            {
                isOnValidRoad = false; roadHeadingDelta = 0f; return 0f;
            }
            segDx /= segLen; segDy /= segLen;

            // Heading error: signed angle from vehicle forward to segment
            // direction. Positive = segment is to the LEFT of vehicle forward,
            // meaning the vehicle is rotated to the right of the path tangent
            // and must steer left to align.
            float headingDot   = flatFwd.X * segDx + flatFwd.Y * segDy;
            float headingCross = flatFwd.X * segDy - flatFwd.Y * segDx;
            float headingError = (float)Math.Atan2(headingCross, headingDot);

            // Lateral error: signed distance from front axle to closest point.
            // Positive = path is to the RIGHT of the vehicle, so the vehicle
            // needs to steer right.
            float toCx = bestCx - frontAxle.X, toCy = bestCy - frontAxle.Y;
            float lateralError = flatRight.X * toCx + flatRight.Y * toCy;
            // Expose for the haptic layer's lane-edge drift detection.
            lastLaneLateralError = lateralError;

            // Stanley: δ = -ψ_e + atan2(k * e, v + ε).
            // Sign on ψ_e is negated because in our convention
            // "headingCross > 0" means the path heads LEFT of us, which
            // requires LEFT steering (= negative input).
            float crossTrack = (float)Math.Atan2(STANLEY_K * lateralError, speed + STANLEY_SOFT);
            float steerRad = -headingError + crossTrack;
            float maxRad = PURE_PURSUIT_DELTA_MAX;
            if (steerRad > maxRad) steerRad = maxRad;
            else if (steerRad < -maxRad) steerRad = -maxRad;

            // Surface state for the rest of the state machine.
            isOnValidRoad = true;
            // Convert headingError (radians) to the degree convention used by
            // the existing code. Sign matches: positive headingError = path
            // heads left of vehicle = vehicle's heading is right of path's
            // heading = roadHeadingDelta should be NEGATIVE in the existing
            // convention (which measures node_heading - vehicle_heading).
            roadHeadingDelta = -headingError * 57.29578f;

            return steerRad / maxRad;
        }

        /// <summary>Inspects the pathPolyline ahead of the closest segment for a
        /// sharp bend. If one is found and current speed exceeds the safe
        /// cornering speed for it, returns a brake request in [0, CURVE_BRAKE_MAX]
        /// sized to shed the excess speed before the turn; otherwise 0.</summary>
        private float ComputeCurveBrake(Vehicle veh)
        {
            // Iter-10 Patch D: skew-aware brake floor. Independent of the
            // polyline-driven curve detection below, if the car is already
            // significantly skewed against the road tangent at meaningful
            // speed, the physics model has already lost the bend — fire some
            // brake regardless of what the meta vSafe says. Catches the
            // SPORTS_CAR case where meta vSafe @ 22° = 42 m/s and the existing
            // min(physics, meta) gate still lets the car oversteer
            // (driveassist-2026-05-25-231603 F2480: 29 m/s at 16.8° skew with
            // curveBrake=0). All early returns below propagate this floor.
            float skewBrakeFloor = 0f;
            {
                float skewMag = Math.Abs(roadHeadingDelta);
                if (skewMag > 15f && veh.Speed > 8f)
                {
                    skewBrakeFloor = 0.2f + Math.Min(0.4f, (skewMag - 15f) / 30f * 0.4f);
                    if (skewBrakeFloor > CURVE_BRAKE_MAX) skewBrakeFloor = CURVE_BRAKE_MAX;
                }
            }

            // iter-13 Patch V: LANE-END SPEED GOVERNOR. The absolute-skew floor
            // above only fires once the car is ALREADY misaligned. These three
            // terms brake BEFORE the car departs the lane at speed (the Fort
            // Zancudo failure: held a lane at 21 m/s, the lane ended, the car
            // shot off and collided). All terms only RAISE the floor (max), so
            // they can never reduce existing braking.
            long vNow = DateTime.Now.Ticks;
            float curSkewAbs = Math.Abs(roadHeadingDelta);
            // (1) SKEW-RATE: skew climbing fast at speed = the bend is getting
            //     away from us. React before the absolute angle is large.
            if (prevRoadHeadingDeltaTicks != 0 && veh.Speed > 8f)
            {
                float dtSec = (vNow - prevRoadHeadingDeltaTicks) / 10000000f;
                if (dtSec > 0.001f)
                {
                    float skewRate = (curSkewAbs - prevRoadHeadingDeltaAbs) / dtSec;
                    if (skewRate > 40f)
                    {
                        float rateBrake = Math.Min(CURVE_BRAKE_MAX, 0.3f + skewRate / 200f);
                        if (rateBrake > skewBrakeFloor) skewBrakeFloor = rateBrake;
                    }
                }
            }
            prevRoadHeadingDeltaAbs = curSkewAbs;
            prevRoadHeadingDeltaTicks = vNow;
            // (2) POLYLINE COLLAPSE: the road just vanished underneath us at
            //     speed (ComputeStanleySteer stamped laneCollapseAtSpeedTicks).
            //     Apply a firm-but-comfortable floor for a short window instead
            //     of coasting off the end.
            if (laneCollapseAtSpeedTicks != 0
                && (vNow - laneCollapseAtSpeedTicks) < 5000000   // 500 ms window
                && veh.Speed > LANE_END_SAFE_SPEED)
            {
                if (0.5f > skewBrakeFloor) skewBrakeFloor = 0.5f;
            }
            // (3) PATH RUN-OUT: the closest segment is within ~2 of the polyline
            //     end and we're moving fast — shed speed toward a safe end-of-
            //     lane crawl before we reach the last point.
            if (pathPolyline.Count >= 2
                && lastClosestPolySeg >= pathPolyline.Count - 3
                && veh.Speed > LANE_END_SAFE_SPEED + 2f)
            {
                float over = (veh.Speed - LANE_END_SAFE_SPEED) / 12f;
                float runoutBrake = Math.Min(CURVE_BRAKE_MAX, 0.3f + over);
                if (runoutBrake > skewBrakeFloor) skewBrakeFloor = runoutBrake;
            }

            if (pathPolyline.Count < 3) { curveBrakeStreak = 0; return skewBrakeFloor; }
            float speed = veh.Speed;
            if (speed < 4f) { curveBrakeStreak = 0; return skewBrakeFloor; }

            int start = lastClosestPolySeg;
            if (start < 0) start = 0;

            float maxTurnDeg = 0f;
            float distToTurn = 0f;
            float curveSegLen = 0f;
            float arc = 0f;

            for (int i = start; i < pathPolyline.Count - 2
                                && (i - start) < CURVE_BRAKE_LOOKAHEAD_PTS; i++)
            {
                GTA.Math.Vector3 a = pathPolyline[i];
                GTA.Math.Vector3 b = pathPolyline[i + 1];
                GTA.Math.Vector3 c = pathPolyline[i + 2];
                float d1x = b.X - a.X, d1y = b.Y - a.Y;
                float d2x = c.X - b.X, d2y = c.Y - b.Y;
                float l1 = (float)Math.Sqrt(d1x * d1x + d1y * d1y);
                float l2 = (float)Math.Sqrt(d2x * d2x + d2y * d2y);
                if (l1 < 0.5f || l2 < 0.5f) { arc += l1; continue; }
                d1x /= l1; d1y /= l1; d2x /= l2; d2y /= l2;
                float dot = d1x * d2x + d1y * d2y;
                float cross = d1x * d2y - d1y * d2x;
                float turnDeg = Math.Abs((float)(Math.Atan2(cross, dot) * 57.29578));
                if (turnDeg > maxTurnDeg)
                {
                    maxTurnDeg = turnDeg;
                    distToTurn = arc + l1;          // arc length to vertex b (the bend)
                    curveSegLen = Math.Min(l1, l2);
                }
                arc += l1;
            }

            if (maxTurnDeg < CURVE_SHARP_ANGLE_DEG) { curveBrakeStreak = 0; return skewBrakeFloor; }

            // Turn radius from chord/angle: R ~= s / (2 sin(theta/2)).
            float turnRad = maxTurnDeg / 57.29578f;
            float sinHalf = (float)Math.Sin(turnRad / 2f);
            if (sinHalf < 0.01f) { curveBrakeStreak = 0; return skewBrakeFloor; }
            float R = curveSegLen / (2f * sinHalf);
            float vSafePhysics = (float)Math.Sqrt(CURVE_LATERAL_ACCEL_MAX * R);

            // META-INFORMED TARGET SPEED (iter-8). The physics-only vSafe
            // ignores vehicle class — a sports car and a truck see the same
            // limit at the same radius even though their grip differs. GTA V's
            // own AI uses an angle->speed table per vehicle handling class;
            // fold that in as a second upper bound and take the more
            // conservative of the two. min() keeps us safe even if the meta
            // table is generous at a given angle (physics still applies).
            var aiInfo = VehicleAIHandlingRegistry.GetForVehicle(veh);
            float vSafeMeta = aiInfo.MaxSpeedForAngle(maxTurnDeg);
            float vSafe = Math.Min(vSafePhysics, vSafeMeta);

            if (speed <= vSafe) { curveBrakeStreak = 0; return skewBrakeFloor; }
            // Only brake when the bend is close enough to matter. The lookahead
            // distance comes from the meta's class-aware brake-distance
            // triplet (MinBrakeDistance / MaxBrakeDistance /
            // MaxSpeedAtBrakeDistance) instead of the old class-agnostic
            // `speed * 3f`. Trucks (Max=120m) get longer lookahead than
            // sports cars (Max=120m but Min=8m). 1.3x margin so brake has
            // time to ramp up rather than firing at the last possible moment.
            float lookahead = aiInfo.BrakeLookaheadForSpeed(speed) * 1.3f;
            if (lookahead < 6f) lookahead = 6f;
            if (distToTurn < 0.5f || distToTurn > lookahead)
            {
                curveBrakeStreak = 0;
                return skewBrakeFloor;
            }

            // Persistence guard: require the same conclusion two scans running
            // so a single noisy polyline frame can't trigger a brake.
            // (Kept as a local streak instead of migrating to the rolling
            // history buffer — adding a curve-intent field to the snapshot
            // just for this one consumer would be exactly the kind of bloat
            // the buffer rationale calls out.)
            curveBrakeStreak++;
            if (curveBrakeStreak < 2) return skewBrakeFloor;

            float decel = (speed * speed - vSafe * vSafe) / (2f * Math.Max(distToTurn, 1f));
            float brake = decel / 6.0f;            // ~6 m/s^2 firm braking maps to 1.0
            if (brake < 0f) brake = 0f;
            if (brake > CURVE_BRAKE_MAX) brake = CURVE_BRAKE_MAX;
            // Iter-10 Patch D: skew floor wins if larger than the curve-
            // driven brake (the car is already misaligned, so brake harder
            // than the curve table alone would suggest).
            if (skewBrakeFloor > brake) brake = skewBrakeFloor;
            return brake;
        }

    }
}
