using GTA;
using GTA.Native;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.IO;
using System.IO.MemoryMappedFiles;
using DavyKager;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GrandTheftAccessibility
{
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

        // Collision prediction tracking
        private float threatTimeToCollision = 999f;
        private string threatDirection = "none";
        private string threatType = "none";

        // Cached values for continuous input application (must be applied every tick)
        private float cachedSteerCorrection = 0f;
        private float cachedBrakeMagnitude = 0f;
        private int cachedAvoidDirection = 0;
        private bool cachedIsFullMode = false;
        private bool cachedIsBraking = false; // True when system is actively braking (blocks throttle in full mode)
        private bool wasObstacleInBrakeZone = false; // Track if obstacle was in brake zone last frame (for first-contact detection)

        // Nav assist distances shared with drive assist for improved obstacle avoidance
        private float navAssistDistLeft = 999f;
        private float navAssistDistCenter = 999f;
        private float navAssistDistRight = 999f;
        private float navAssistDistBehind = 999f;
        private string navAssistTypeLeft = "none";
        private string navAssistTypeCenter = "none";
        private string navAssistTypeRight = "none";
        private string navAssistTypeBehind = "none";

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
        private const float ROAD_CLOSE_THRESHOLD_BASE = 15f; // Base max distance to be considered "on road"
        private const float ROAD_FAR_THRESHOLD_BASE = 10f;   // Base distance for timer (increased from 8)
        private const long ROAD_TELEPORT_DELAY_TICKS = 50000000; // 5 seconds in ticks
        private const long ROAD_TELEPORT_COOLDOWN_TICKS = 30000000; // 3 second cooldown between teleports
        private long lastTeleportTicks = 0;              // Last time we teleported

        // Audio feedback
        private WaveOutEvent outSteerAssist;
        private SignalGenerator steerAssistBeep;

        // Thresholds (seconds to collision)
        private const float STEER_SMOOTHING_RATE = 8.0f; // Units per second (frame-rate independent)
        private const float BRAKE_THRESHOLD_FULL = 1.5f;       // Only brake when collision is very imminent
        private const float BRAKE_THRESHOLD_ASSIST = 1.0f;
        private const float MIN_BRAKE_DISTANCE = 3f;          // Don't brake for obstacles closer than 3m (too late anyway)
        private const float MIN_BRAKE_DISTANCE_FULL = 5f;     // Full mode has slightly more buffer
        private const float STEER_THRESHOLD_FULL = 4.0f;
        private const float STEER_THRESHOLD_ASSIST = 2.5f;
        private const float BASE_COLLISION_RADIUS = 2.5f;     // Base collision radius, scaled by vehicle size

        // Reverse driving support
        private bool isReversing = false;                     // True when vehicle is moving backward

        // Road-following pathfinding
        private float roadSteerCorrection = 0f;       // Steering needed to stay on road
        private float smoothedRoadCorrection = 0f;    // Smoothed road correction
        private bool isOnValidRoad = false;           // Whether we found a valid road node
        private float roadHeadingDelta = 0f;          // Difference between vehicle heading and road heading

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
        // Multi-ray cone pattern for better terrain/obstacle detection
        // Horizontal ray spread angles (degrees) - covers wider detection area
        private static readonly float[] SHAPE_CAST_H_ANGLES_FAST = { 0f, -15f, 15f, -30f, 30f };
        private static readonly float[] SHAPE_CAST_H_ANGLES_SLOW = { 0f, -22f, 22f };
        // Vertical ray spread angles (degrees) - negative = downward for terrain
        private static readonly float[] SHAPE_CAST_V_ANGLES = { 0f, -10f, 10f };
        // Speed threshold for switching between fast/slow ray patterns (m/s)
        private const float SHAPE_CAST_SPEED_THRESHOLD = 15f;

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

        // Detection Radius Setting (0=10m, 1=25m, 2=50m, 3=100m)
        private int detectionRadiusIndex = 1; // Default 25m
        private float[] detectionRadiusOptions = { 10f, 25f, 50f, 100f };

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

        // Auto-navigation mode tracking: "drive", "fly", "walk"
        private string autonavMode = "drive";
        // Aircraft autopilot cruise altitude (meters above ground)
        private float autopilotAltitude = 200f;

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
        private void UpdateAutodriveSpeed()
        {
            if (!isAutodriving) return;

            Vehicle veh = Game.Player.Character.CurrentVehicle;
            if (veh == null) return;

            Ped driver = Game.Player.Character;
            int drivingStyle = GetDrivingStyleFromFlags();

            if (autodriveWanderMode)
            {
                // Restart wander task with new speed/flags
                Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER,
                    driver, veh,
                    autodriveSpeed, drivingStyle);
            }
            else
            {
                // Restart waypoint task with new speed/flags
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    driver, veh,
                    autodriveDestination.X, autodriveDestination.Y, autodriveDestination.Z,
                    autodriveSpeed, drivingStyle, 20f);
            }
        }

        private bool[] headings = new bool[8];
        private bool climbing = false;
        private bool shifting = false;

        // ============================================
        // PHONE & MENU ACCESSIBILITY SYSTEM
        // ============================================
        private bool wasPhoneOut = false;
        private bool wasPauseMenuActive = false;
        private int lastPhoneAppIndex = -1;
        private int lastContactIndex = -1;
        private int lastPauseMenuState = -1;
        private int lastPauseMenuTab = -1;        // Track current pause menu tab
        private int lastPauseMenuSelection = -1;  // Track current pause menu selection
        private long phoneCheckTicks = 0;
        private long menuCheckTicks = 0;
        private long phoneOpenedTicks = 0;      // When phone was first detected as open
        private long menuOpenedTicks = 0;       // When menu was first detected as open
        private bool phoneAnnouncedOpen = false; // Have we announced phone open yet?
        private bool menuAnnouncedOpen = false;  // Have we announced menu open yet?
        private const long PHONE_OPEN_DELAY = 8000000;  // 800ms delay for phone animation
        private const long MENU_OPEN_DELAY = 5000000;   // 500ms delay for menu animation

        // Debug logging for menu/phone state
        private int lastLoggedMenuState = -999;
        private int lastLoggedPhoneRenderId = -999;
        private bool lastLoggedCanPhoneBeSeen = false;

        // Background thread for pause menu/phone detection
        // This runs independently of the game tick, so it works even when paused
        private Thread menuMonitorThread = null;
        private volatile bool menuMonitorRunning = false;
        private volatile bool bgMenuWasOpen = false;
        private volatile bool bgPhoneWasOpen = false;
        private string bgLastOcrText = "";

        // Global keyboard hook for OCR during pause menu
        private IntPtr globalKeyboardHook = IntPtr.Zero;
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc keyboardProcDelegate; // Must keep reference to prevent GC
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_F12 = 0x7B; // F12 key for manual OCR trigger

        // ============================================
        // SHARED MEMORY FOR EXTERNAL MENU HELPER
        // Communicates game state to external process
        // ============================================
        private const string SHARED_MEMORY_NAME = "GTA11Y_GameState";
        private const int SHARED_MEMORY_SIZE = 256;
        private MemoryMappedFile sharedMemory = null;
        private MemoryMappedViewAccessor sharedMemoryAccessor = null;

        // Shared memory structure offsets:
        // Offset 0: byte - isPauseMenuActive (0 or 1)
        // Offset 1: byte - pauseMenuState (0-10 for different tabs)
        // Offset 2: byte - isPhoneVisible (0 or 1)
        // Offset 3: byte - pauseMenuSelection (current selection index)
        // Offset 4: uint - timestamp (last update tick count)
        // Offset 8-207: string - last spoken text (200 chars max, null terminated)

        // Phone app names (indices match game's internal app ordering)
        private static readonly string[] PHONE_APPS = {
            "Contacts",      // 0
            "Job List",      // 1
            "Text Messages", // 2
            "Emails",        // 3
            "Snapmatic",     // 4
            "Internet",      // 5
            "Quick GPS",     // 6
            "Settings",      // 7
            "Trackify"       // 8 (Trevor only)
        };

        // Known contacts lookup - populated with main story contacts
        private static readonly Dictionary<int, string> KNOWN_CONTACTS = new Dictionary<int, string>()
        {
            // Main Characters
            {0, "Michael"},
            {1, "Franklin"},
            {2, "Trevor"},
            // Common Contacts (approximate indices, may vary)
            {3, "Amanda"},
            {4, "Jimmy"},
            {5, "Tracey"},
            {6, "Lester"},
            {7, "Lamar"},
            {8, "Ron"},
            {9, "Wade"},
            {10, "Dave Norton"},
            {11, "Simeon"},
            {12, "Devin Weston"},
            {13, "Solomon Richards"},
            {14, "Denise"},
            {15, "Tonya"},
            {16, "Tanisha"},
            {17, "Martin Madrazo"},
            {18, "Emergency Services"},
            {19, "Downtown Cab Co"},
            {20, "Merryweather"},
            {21, "Pegasus"},
            {22, "Mors Mutual"},
            {23, "Mechanic"}
        };

        // Pause menu tab names
        private static readonly string[] PAUSE_MENU_TABS = {
            "Map",
            "Brief",
            "Stats",
            "Settings",
            "Game",
            "Gallery",
            "Social"
        };

        // ============================================
        // WINDOWS OCR SYSTEM
        // ============================================
        private OcrEngine ocrEngine = null;
        private bool ocrInitialized = false;
        private bool ocrInProgress = false;
        private long lastOcrTicks = 0;
        private string lastOcrText = "";
        private const long OCR_COOLDOWN_TICKS = 5000000; // 500ms between OCR attempts

        // OCR region constants (percentage of screen)
        // Phone region - the phone appears in CENTER-RIGHT of screen when character holds it
        private const float PHONE_REGION_LEFT = 0.35f;    // 35% from left
        private const float PHONE_REGION_TOP = 0.15f;     // 15% from top
        private const float PHONE_REGION_WIDTH = 0.40f;   // 40% of screen width
        private const float PHONE_REGION_HEIGHT = 0.70f;  // 70% of screen height

        // Pause menu typically covers most of the screen
        private const float MENU_REGION_LEFT = 0.05f;     // 5% from left
        private const float MENU_REGION_TOP = 0.05f;      // 5% from top
        private const float MENU_REGION_WIDTH = 0.90f;    // 90% of screen width
        private const float MENU_REGION_HEIGHT = 0.90f;   // 90% of screen height

        // Debug flag for OCR - set to true to log to file
        private bool ocrDebug = true;
        private bool ocrSaveScreenshots = true; // Save screenshots to debug OCR regions
        private string ocrLogPath = null; // Will be set to scripts folder path
        private int ocrScreenshotCount = 0;

        // DllImports for screen capture
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
            IntPtr hdcSrc, int xSrc, int ySrc, int Rop);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        // Global keyboard hook imports
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const int SRCCOPY = 0x00CC0020;
        private const uint PW_RENDERFULLCONTENT = 0x00000002; // Windows 8.1+ - captures DirectX content
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

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
            Tolk.Speak("Mod Ready");

            // Initialize shared memory for external MenuHelper communication
            // This allows the external process to read game state even when scripts pause
            InitializeSharedMemory();

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

            funMenu.Add("Blow up all nearby vehicles");
            funMenu.Add("Make all nearby pedestrians attack each other.");
            funMenu.Add("instantly kill all nearby pedestrians.");
            funMenu.Add("Raise Wanted Level. ");
            funMenu.Add("Clear Wanted Level. ");

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

            setupSettings();
        }

        private void onTick(object sender, EventArgs e)
        {
            // ============================================
            // PAUSE MENU ACCESSIBILITY
            // Prevent game from pausing so we can read menu items
            // ============================================
            HandlePauseMenuAccessibility();

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
                            Tolk.Speak("Autopilot disengaged. You exited the aircraft.");
                        }
                        else if (autodriveWanderMode)
                        {
                            // Hovering/circling - periodic altitude and location updates
                            if (DateTime.Now.Ticks - autodriveCheckTicks > 150000000) // 15 seconds
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
                            // Use 2D distance for aircraft (ignore altitude difference for arrival)
                            float horizontalDist = (float)Math.Sqrt(
                                Math.Pow(playerPos.X - autodriveDestination.X, 2) +
                                Math.Pow(playerPos.Y - autodriveDestination.Y, 2));

                            if (horizontalDist < 75f)
                            {
                                isAutodriving = false;
                                Game.Player.Character.Task.ClearAll();
                                autonavMode = "drive";
                                Tolk.Speak("Arrived at destination. Autopilot disengaged.");
                            }
                            else if (DateTime.Now.Ticks - autodriveCheckTicks > 100000000) // 10 seconds
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
                    else
                    {
                        // GROUND VEHICLE AUTO-DRIVE MONITORING (existing behavior)
                        if (!Game.Player.Character.IsInVehicle())
                        {
                            isAutodriving = false;
                            autodriveWanderMode = false;
                            Tolk.Speak("Auto-drive stopped. You exited the vehicle.");
                        }
                        else if (autodriveWanderMode)
                        {
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

                        // Detection range - larger in vehicles and scales with speed
                        float maxRange = 5f;
                        if (inVehicle)
                        {
                            float speedFactor = Math.Min(vehicleSpeed / 25f, 1f);
                            maxRange = 6f + (speedFactor * 20f); // 6m to 26m based on speed
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
                            if (ped == Game.Player.Character || ped.IsDead) continue;

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

                        if (useShapeCast)
                        {
                            // --- SHAPE CASTING MODE: Multi-ray cone pattern for better coverage ---
                            GTA.Math.Vector3 hitPos;

                            // Center zone - shape cast forward
                            float centerDist = PerformShapeCast(startPos, forwardVec, rightVec, maxRange,
                                IntersectFlags.Map, Game.Player.Character, out hitPos, vehicleSpeed);
                            if (centerDist > 0 && centerDist >= minDist && centerDist < distCenter)
                            {
                                distCenter = centerDist; typeCenter = "world"; nameCenter = "Obstacle";
                            }

                            // Left zone - shape cast left-diagonal (45 degrees)
                            GTA.Math.Vector3 leftDir = GTA.Math.Vector3.Normalize(forwardVec - rightVec);
                            float leftDist = PerformShapeCast(startPos, leftDir, rightVec, maxRange,
                                IntersectFlags.Map, Game.Player.Character, out hitPos, vehicleSpeed);
                            if (leftDist > 0 && leftDist >= minDist && leftDist < distLeft)
                            {
                                distLeft = leftDist; typeLeft = "world"; nameLeft = "Obstacle";
                            }

                            // Right zone - shape cast right-diagonal (45 degrees)
                            GTA.Math.Vector3 rightDir = GTA.Math.Vector3.Normalize(forwardVec + rightVec);
                            float rightDist = PerformShapeCast(startPos, rightDir, rightVec, maxRange,
                                IntersectFlags.Map, Game.Player.Character, out hitPos, vehicleSpeed);
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
                                float sideLeftDist = PerformShapeCast(startPos, pureLeft, rightVec, sideRange,
                                    IntersectFlags.Map, Game.Player.Character, out hitPos, vehicleSpeed);
                                if (sideLeftDist > 0 && sideLeftDist >= minDist && sideLeftDist < distLeft)
                                {
                                    distLeft = sideLeftDist; typeLeft = "world"; nameLeft = "Side Obstacle";
                                }

                                float sideRightDist = PerformShapeCast(startPos, rightVec, rightVec, sideRange,
                                    IntersectFlags.Map, Game.Player.Character, out hitPos, vehicleSpeed);
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
                                float behindDist = PerformShapeCast(startPos, behindDir, rightVec, maxRange,
                                    IntersectFlags.Map, Game.Player.Character, out hitPos, vehicleSpeed);
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

                        // Only active when moving (>2 m/s)
                        if (vehicleSpeed > 2f)
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
                        }
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
                // Checks every 2 seconds for hostile peds
                // Plays directional beeps at enemy positions
                // ============================================

                // Check for enemies every 2 seconds
                if (DateTime.Now.Ticks - enemyCheckTicks > 20000000) // 2 seconds
                {
                    enemyCheckTicks = DateTime.Now.Ticks;
                    trackedEnemies.Clear();

                    // Scan for hostile peds within 100 meters
                    Ped[] nearbyPeds = World.GetNearbyPeds(Game.Player.Character.Position, 100f);
                    foreach (Ped ped in nearbyPeds)
                    {
                        if (ped == Game.Player.Character || ped.IsDead) continue;

                        // Only detect peds actively in combat against the player
                        // This is the most reliable indicator of an actual threat
                        bool isHostile = ped.IsInCombatAgainst(Game.Player.Character);

                        if (isHostile)
                        {
                            trackedEnemies.Add(ped);
                        }
                    }

                    // Announce if enemies detected
                    if (trackedEnemies.Count > 0)
                    {
                        Tolk.Speak(trackedEnemies.Count + " hostile" + (trackedEnemies.Count > 1 ? "s" : "") + " detected", true);
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
                if (DateTime.Now.Ticks - serviceCheckTicks > 30000000) // 3 seconds
                {
                    serviceCheckTicks = DateTime.Now.Ticks;

                    // Service blip types: 110=Ammu-Nation, 61=Hospital, 408=Clothing, 73=Mod Shop, 207=Barber, 362=Tattoo, 89=ATM
                    int[] serviceTypes = { 110, 61, 408, 73, 207, 362, 89 };
                    string[] serviceNames = { "Ammu-Nation", "Hospital", "Clothing Store", "Mod Shop", "Barber", "Tattoo Parlor", "ATM" };

                    float userRadius = GetDetectionRadius();
                    GTA.Math.Vector3 playerPos = Game.Player.Character.Position;

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
                                break;
                            }

                            blipHandle = Function.Call<int>(Hash.GET_NEXT_BLIP_INFO_ID, serviceTypes[i]);
                        }
                    }

                    // Reset if moved far enough
                    if (lastAnnouncedServiceBlip != -1)
                    {
                        // Simple reset after some time
                        lastAnnouncedServiceBlip = -1;
                    }
                }

                // ============================================
                // PAUSE MENU ACCESSIBILITY
                // Using proper natives for menu state detection
                // ============================================
                // Check every tick for menu changes (not throttled - selection changes are one-frame events)
                {
                    // GET_PAUSE_MENU_STATE: 0=closed, 1=opening, 2=open, 3=closing
                    int menuState = Function.Call<int>(Hash.GET_PAUSE_MENU_STATE);

                    // Also check IS_PAUSE_MENU_ACTIVE as fallback
                    bool isPauseMenuActive = Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE);

                    // Log menu state changes for debugging
                    if (menuState != lastLoggedMenuState)
                    {
                        OcrLog($"Menu state changed: {lastLoggedMenuState} -> {menuState}, IS_PAUSE_MENU_ACTIVE={isPauseMenuActive}");
                        lastLoggedMenuState = menuState;
                    }

                    // Use IS_PAUSE_MENU_ACTIVE as primary check since GET_PAUSE_MENU_STATE might not work
                    bool pauseMenuActive = isPauseMenuActive;

                    if (pauseMenuActive && !wasPauseMenuActive)
                    {
                        // Pause menu just opened
                        OcrLog("Pause menu opened - announcing");
                        Tolk.Speak("Pause menu", true);
                        lastPauseMenuSelection = -1;
                        lastPauseMenuTab = -1;
                    }
                    else if (!pauseMenuActive && wasPauseMenuActive)
                    {
                        // Pause menu closed
                        OcrLog("Pause menu closed - announcing");
                        Tolk.Speak("Menu closed", true);
                    }
                    // NOTE: Pause menu item reading was attempted via native plugin but GTA V does not
                    // expose menu text through any known natives - only numerical IDs are available.
                    // This feature has been shelved until a viable approach is found.

                    wasPauseMenuActive = pauseMenuActive;
                }

                // ============================================
                // PHONE ACCESSIBILITY
                // Using multiple methods to detect phone visibility
                // ============================================
                {
                    // Method 1: GET_MOBILE_PHONE_RENDER_ID
                    OutputArgument outRenderId = new OutputArgument();
                    Function.Call(Hash.GET_MOBILE_PHONE_RENDER_ID, outRenderId);
                    int phoneRenderId = outRenderId.GetResult<int>();

                    // Method 2: CAN_PHONE_BE_SEEN_ON_SCREEN (unreliable but log it)
                    bool canPhoneBeSeen = Function.Call<bool>(Hash.CAN_PHONE_BE_SEEN_ON_SCREEN);

                    // Method 3: Check if player is in phone animation/state
                    bool isPhoneCallOngoing = Function.Call<bool>((Hash)0x7497D2CE2C30D24C); // IS_MOBILE_PHONE_CALL_ONGOING

                    // Log phone state changes
                    if (phoneRenderId != lastLoggedPhoneRenderId || canPhoneBeSeen != lastLoggedCanPhoneBeSeen)
                    {
                        OcrLog($"Phone state: renderId={phoneRenderId}, canBeSeen={canPhoneBeSeen}, callOngoing={isPhoneCallOngoing}");
                        lastLoggedPhoneRenderId = phoneRenderId;
                        lastLoggedCanPhoneBeSeen = canPhoneBeSeen;
                    }

                    // Phone is visible if render ID is non-zero
                    bool phoneVisible = phoneRenderId > 0;

                    if (phoneVisible && !wasPhoneOut)
                    {
                        // Phone just became visible
                        OcrLog("Phone opened - announcing");
                        Tolk.Speak("Phone", true);
                        lastOcrText = "";
                        // Trigger OCR to read initial phone state
                        TriggerPhoneOcr();
                    }
                    else if (!phoneVisible && wasPhoneOut)
                    {
                        // Phone was put away
                        Tolk.Speak("Phone closed", true);
                        lastOcrText = "";
                    }
                    else if (phoneVisible)
                    {
                        // Phone is open - periodically check for changes via OCR
                        // Throttle to every 500ms
                        if (DateTime.Now.Ticks - phoneCheckTicks > 5000000)
                        {
                            phoneCheckTicks = DateTime.Now.Ticks;
                            TriggerPhoneOcr();
                        }
                    }

                    wasPhoneOut = phoneVisible;
                }
            }

            // ============================================
            // PAUSE MENU DETECTION - OUTSIDE Game.IsLoading CHECK
            // ScriptHookVDotNet scripts still run during pause menu
            // but the above code is inside if(!Game.IsLoading)
            // ============================================
            // This runs every tick regardless of loading state
            {
                int menuState = Function.Call<int>(Hash.GET_PAUSE_MENU_STATE);
                bool isPauseMenuActive = Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE);

                // Log every state change
                if (menuState != lastLoggedMenuState)
                {
                    OcrLog($"[OUTSIDE] Menu state: {menuState}, IS_PAUSE_MENU_ACTIVE={isPauseMenuActive}");
                    lastLoggedMenuState = menuState;

                    // Announce based on state
                    if (isPauseMenuActive && !wasPauseMenuActive)
                    {
                        Tolk.Speak("Pause menu", true);
                    }
                    else if (!isPauseMenuActive && wasPauseMenuActive)
                    {
                        Tolk.Speak("Menu closed", true);
                    }

                    wasPauseMenuActive = isPauseMenuActive;
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

            // Manual OCR trigger - Ctrl+NumPad0
            // Works regardless of keys_disabled state to always allow screen reading
            // Also works in pause menu and phone menus
            if (e.KeyCode == Keys.NumPad0 && shifting && !keyState[19])
            {
                keyState[19] = true;
                // Suppress the key event so it doesn't pass to the game
                e.Handled = true;
                e.SuppressKeyPress = true;
                // Disable frontend controls for this frame to prevent menu interaction
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 201, true); // INPUT_FRONTEND_ACCEPT
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 202, true); // INPUT_FRONTEND_CANCEL
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 217, true); // INPUT_FRONTEND_SELECT
                TriggerManualOcr();
            }

            if (!keys_disabled)
            {
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

                        if (getSetting("onscreen") == 0 && hashes.ContainsKey(prop.Model.NativeValue.ToString()) && prop.IsVisible && !prop.IsAttachedTo(Game.Player.Character) && (hashes[prop.Model.NativeValue.ToString()].Contains("door") == false || !hashes[prop.Model.NativeValue.ToString()].Contains("gate") == false))
                            valid = true;

                        if (getSetting("onscreen") == 1 && hashes.ContainsKey(prop.Model.NativeValue.ToString()) && prop.IsVisible && prop.IsOnScreen && !prop.IsAttachedTo(Game.Player.Character) && (hashes[prop.Model.NativeValue.ToString()].Contains("door") == false || !hashes[prop.Model.NativeValue.ToString()].Contains("gate") == false))
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
                    GTA.Audio.PlaySoundFrontend("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
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

                    // Auto-Drive menu - NumPad2 stops auto-navigation (when active)
                    if (mainMenuIndex == 3)
                    {
                        if (isAutodriving)
                        {
                            isAutodriving = false;
                            autodriveWanderMode = false;
                            Game.Player.Character.Task.ClearAll();
                            if (autonavMode == "fly")
                                Tolk.Speak("Autopilot disengaged. You have control.");
                            else if (autonavMode == "walk")
                                Tolk.Speak("Auto-walk cancelled. You have control.");
                            else
                                Tolk.Speak("Auto-drive cancelled. You have control.");
                            autonavMode = "drive";
                        }
                        else
                        {
                            Tolk.Speak("Auto-drive not running. Use NumPad Multiply to start.");
                        }
                    }

                    if (mainMenuIndex == 1)
                    {
                        Vehicle vehicle = World.CreateVehicle(spawns[spawnMenuIndex].id, Game.Player.Character.Position + Game.Player.Character.ForwardVector * 2.0f, Game.Player.Character.Heading + 90);
                        vehicle.PlaceOnGround();
                        if (getSetting("warpInsideVehicle") == 1)
                        {
                            Game.Player.Character.SetIntoVehicle(vehicle, VehicleSeat.Driver);
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


                    }


                    // Auto-Drive menu (mainMenuIndex == 3)
                    if (mainMenuIndex == 3)
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

                    // Settings menu (mainMenuIndex == 4)
                    if (mainMenuIndex == 4)
                    {
                        // Special handling for detection radius (cycles through 10m/25m/50m/100m)
                        if (settingsMenu[settingsMenuIndex].id == "detectionRadius")
                        {
                            int currentVal = settingsMenu[settingsMenuIndex].value;
                            currentVal = (currentVal + 1) % 4; // Cycle 0->1->2->3->0
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
                    GTA.Audio.PlaySoundFrontend("NAV_LEFT_RIGHT", "HUD_FRONTEND_DEFAULT_SOUNDSET");

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

                    // Auto-Drive menu navigation (cycle through 32 flags) - works while driving
                    if (mainMenuIndex == 3)
                    {
                        if (autodriveFlagMenuIndex > 0)
                            autodriveFlagMenuIndex--;
                        else
                            autodriveFlagMenuIndex = 31; // Wrap to end

                        string flagState = autodriveFlags[autodriveFlagMenuIndex] ? "ON" : "OFF";
                        Tolk.Speak((autodriveFlagMenuIndex + 1) + ". " + autodriveFlagNames[autodriveFlagMenuIndex] + ", " + flagState);
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

                }

                if (e.KeyCode == Keys.NumPad3 & !keyState[3])
                {
                    GTA.Audio.PlaySoundFrontend("NAV_LEFT_RIGHT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
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

                    // Auto-Drive menu navigation (cycle through 32 flags)
                    // Auto-Drive menu navigation - works while driving
                    if (mainMenuIndex == 3)
                    {
                        if (autodriveFlagMenuIndex < 31)
                            autodriveFlagMenuIndex++;
                        else
                            autodriveFlagMenuIndex = 0; // Wrap to start

                        string flagState = autodriveFlags[autodriveFlagMenuIndex] ? "ON" : "OFF";
                        Tolk.Speak((autodriveFlagMenuIndex + 1) + ". " + autodriveFlagNames[autodriveFlagMenuIndex] + ", " + flagState);
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


                }

                if (e.KeyCode == Keys.NumPad7 && !keyState[7])
                {
                    GTA.Audio.PlaySoundFrontend("NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET");
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
                    GTA.Audio.PlaySoundFrontend("NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET");
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
                        Ped driver = Game.Player.Character;
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
                                    // TASK_PLANE_MISSION: ped, vehicle, targetVeh(0), targetPed(0), x, y, z, missionType, speed, radius, heading, maxAlt, minAlt, precise
                                    // MissionType 4 = go to coord
                                    Function.Call(Hash.TASK_PLANE_MISSION,
                                        driver, veh, 0, 0,
                                        waypointPos.X, waypointPos.Y, waypointPos.Z,
                                        4,                    // mission type: go to coord
                                        autodriveSpeed,       // cruise speed
                                        20f,                  // target radius
                                        -1f,                  // heading (-1 = any)
                                        (int)(waypointPos.Z + 100), // max altitude
                                        (int)(waypointPos.Z - 50),  // min altitude
                                        true);                // precise
                                }

                                isAutodriving = true;
                                autodriveWanderMode = false;
                                autodriveCheckTicks = DateTime.Now.Ticks;

                                int speedMph = (int)Math.Round(autodriveSpeed * 2.23694);
                                string acType = isHeli ? "Helicopter" : "Plane";
                                Tolk.Speak(acType + " autopilot engaged. Flying to waypoint at " + speedMph + " mph. " + (int)autodriveStartDistance + " meters. Altitude: " + (int)autopilotAltitude + " meters.");
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
                                    // Circle for planes
                                    Function.Call(Hash.TASK_PLANE_MISSION,
                                        driver, veh, 0, 0,
                                        currentPos.X, currentPos.Y, currentAlt,
                                        4, autodriveSpeed, 200f, -1f,
                                        (int)(currentAlt + 100), (int)(currentAlt - 50),
                                        false);
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
                    result = result + "Currently " + modeStr + ". Press NumPad 2 to cancel.";
                }
                else
                {
                    // Show current flag and state, plus speed
                    string flagState = autodriveFlags[autodriveFlagMenuIndex] ? "ON" : "OFF";
                    int speedMph = (int)Math.Round(autodriveSpeed * 2.23694); // Convert m/s to mph
                    result = result + "Flag " + (autodriveFlagMenuIndex + 1) + " of 32: " + autodriveFlagNames[autodriveFlagMenuIndex] + ", " + flagState + ". Speed: " + speedMph + " mph. Flight altitude: " + (int)autopilotAltitude + " meters. NumPad Multiply to start. Left/Right arrows adjust speed. Up/Down arrows adjust flight altitude. Works in vehicles, aircraft, and on foot.";
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

            Tolk.Speak(result, true);

        }

        void setupSettings()
        {
            Dictionary<string, int> dictionary = new Dictionary<string, int>();
            string json;
            string[] ids = { "announceHeadings", "announceZones", "announceTime", "altitudeIndicator", "targetPitchIndicator", "navigationAssist", "navAssistBeeps", "pickupDetection", "coverDetection", "waterHazardDetection", "vehicleHealthFeedback", "staminaFeedback", "interactableDetection", "trafficAwareness", "wantedLevelDetails", "slopeTerrainFeedback", "turnByTurnNavigation", "detectionRadius", "radioOff", "warpInsideVehicle", "onscreen", "speed", "godMode", "policeIgnore", "vehicleGodMode", "infiniteAmmo", "neverWanted", "superJump", "runFaster", "swimFaster", "exsplosiveAmmo", "fireAmmo", "explosiveMelee", "aimAutolock", "steeringAssist", "shapeCasting", "roadTeleport", "waypointDriveAssist" };
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
            catch (Exception e)
            {
                System.IO.File.Delete(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/gta11ySettings.json");
                setupSettings();
            }
            string current = "";
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
            catch (Exception e)
            {
                System.IO.File.Delete(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/gta11ySettings.json");
                setupSettings();
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

            // Scan nearby peds
            Ped[] nearbyPeds = World.GetNearbyPeds(playerPos, detectionRange);
            foreach (Ped ped in nearbyPeds)
            {
                if (ped == Game.Player.Character || ped.IsDead) continue;
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
                }

                // Update braking TTC ONLY for threats ahead (not to the side)
                if (dir == "ahead" && ttc < closestBrakeTTC)
                {
                    closestBrakeTTC = ttc;
                    closestBrakeDistance = dist;
                }
            }

            // Scan nearby vehicles
            Vehicle[] nearbyVehs = World.GetNearbyVehicles(playerPos, detectionRange);
            foreach (Vehicle veh in nearbyVehs)
            {
                if (veh == playerVeh) continue;
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
                }

                // Update braking TTC ONLY for threats ahead
                if (dir == "ahead" && ttc < closestBrakeTTC)
                {
                    closestBrakeTTC = ttc;
                    closestBrakeDistance = dist;
                }
            }

            // ============================================
            // MULTI-RAYCAST FOR WORLD GEOMETRY
            // Uses flattened forward vector for hill compensation
            // Multiple rays provide better coverage for obstacles
            // Direction depends on whether we're reversing
            // ============================================
            GTA.Math.Vector3 startPos = playerPos + new GTA.Math.Vector3(0, 0, 0.5f);
            float rayRange = Math.Min(detectionRange, vehicleSpeed * 2.5f);

            // Use the direction we're actually traveling
            GTA.Math.Vector3 travelDir = isReversing ? -flatForward : flatForward;
            GTA.Math.Vector3 travelRight = isReversing ? -rightVec : rightVec;

            // Cast multiple rays: center, slight left, slight right (for better obstacle coverage)
            float[] rayOffsets = { 0f, -0.3f, 0.3f }; // Center, left 17°, right 17°
            float closestRayDist = float.MaxValue;
            int bestAvoidDir = 0;

            foreach (float offset in rayOffsets)
            {
                GTA.Math.Vector3 rayDir = GTA.Math.Vector3.Normalize(travelDir + travelRight * offset);
                RaycastResult ray = World.Raycast(startPos, startPos + (rayDir * rayRange),
                    IntersectFlags.Map | IntersectFlags.Objects, playerVeh);

                if (ray.DidHit)
                {
                    float dist = World.GetDistance(startPos, ray.HitPosition);

                    if (dist < closestRayDist)
                    {
                        closestRayDist = dist;
                        // Determine avoid direction based on which ray hit
                        if (offset < 0) bestAvoidDir = 1;       // Left ray hit, steer right
                        else if (offset > 0) bestAvoidDir = -1; // Right ray hit, steer left
                        else bestAvoidDir = GetClearerSide(playerVeh, rayRange); // Center hit
                    }
                }
            }

            // Process the closest raycast hit
            if (closestRayDist < rayRange)
            {
                float ttc = closestRayDist / Math.Max(vehicleSpeed, 1f);

                // Both steering and braking (raycast is in travel direction = "ahead")
                if (ttc < closestSteerTTC)
                {
                    closestSteerTTC = ttc;
                    closestType = "obstacle";
                    closestDir = "ahead";
                    avoidDirection = bestAvoidDir;
                }
                if (ttc < closestBrakeTTC)
                {
                    closestBrakeTTC = ttc;
                    closestBrakeDistance = closestRayDist;
                }
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
                }
                if (ttc < closestBrakeTTC)
                {
                    closestBrakeTTC = ttc;
                    closestBrakeDistance = navAssistDistCenter;
                }
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
                }
                // NOTE: Intentionally NOT updating closestBrakeTTC - side threats don't trigger braking
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
                }
                // NOTE: Intentionally NOT updating closestBrakeTTC - side threats don't trigger braking
            }

            // Store for reference - use steer TTC for general threat tracking
            threatTimeToCollision = closestSteerTTC;
            threatDirection = closestDir;
            threatType = closestType;

            // Get road guidance for lane keeping
            roadSteerCorrection = GetRoadCurveGuidance(playerVeh);

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

            // Steering threat: any direction (use steerTTC)
            bool hasSteerThreat = closestSteerTTC < steerThreshold;

            // Braking threat: ONLY if obstacle is ahead, within brake TTC, AND at a reasonable distance
            // This prevents braking from distant obstacles at low speeds (which was limiting speed to 6-7 mph)
            bool hasBrakeThreat = closestBrakeTTC < brakeThreshold && closestBrakeDistance > minBrakeDist;

            // ============================================
            // FIRST-CONTACT EMERGENCY STOP (IMPROVED)
            // If an obstacle suddenly appears within minimum brake distance (too close for normal braking)
            // AND we weren't already braking, apply rapid deceleration instead of instant stop.
            // This is less jarring and more realistic than zeroing velocity instantly.
            // ============================================
            bool obstacleInCriticalZone = closestBrakeDistance <= minBrakeDist && closestBrakeDistance < 999f;

            if (obstacleInCriticalZone && !wasObstacleInBrakeZone && !cachedIsBraking && vehicleSpeed > 1f)
            {
                // First contact with obstacle in critical zone - apply aggressive but not instant stop
                // Reduce velocity significantly rather than zeroing it completely
                float emergencyBrakeFactor = Math.Max(0.2f, closestBrakeDistance / minBrakeDist);

                // Scale deceleration based on speed - faster = more aggressive braking needed
                if (vehicleSpeed > 15f)
                {
                    // High speed: reduce to 30% of current velocity
                    playerVeh.Velocity = playerVeh.Velocity * 0.3f;
                }
                else if (vehicleSpeed > 5f)
                {
                    // Medium speed: reduce to 50% of current velocity
                    playerVeh.Velocity = playerVeh.Velocity * 0.5f;
                }
                else
                {
                    // Low speed: can safely stop more abruptly
                    playerVeh.Velocity = playerVeh.Velocity * 0.2f;
                }

                // Also try to steer away from the obstacle if there's a clear direction
                if (avoidDirection != 0 && isFullMode)
                {
                    smoothedSteerCorrection = avoidDirection * 0.8f;
                }

                if (DateTime.Now.Ticks - lastAssistAnnounceTicks > 10000000)
                {
                    Tolk.Speak("Emergency brake!", true);
                    lastAssistAnnounceTicks = DateTime.Now.Ticks;
                }
            }

            // Update tracking for next frame
            wasObstacleInBrakeZone = obstacleInCriticalZone;

            bool hasThreat = hasSteerThreat || hasBrakeThreat;

            // Check if we need to auto-teleport back to road
            CheckRoadTeleport(playerVeh, isFullMode, closestSteerTTC);

            // Always activate if we have road guidance or a threat
            if (hasThreat || (isOnValidRoad && Math.Abs(roadSteerCorrection) > 0.05f) || needsHandbrakeTurn)
            {
                steeringAssistActive = true;
                // Pass both steer and brake TTCs to the assist function
                ApplySteeringAssist(playerVeh, closestSteerTTC, closestBrakeTTC, closestBrakeDistance, avoidDirection, isFullMode, steerThreshold, brakeThreshold, hasSteerThreat, hasBrakeThreat, needsHandbrakeTurn, handbrakeSteerDir);
            }
            else if (steeringAssistActive)
            {
                steeringAssistActive = false;
                smoothedSteerCorrection = 0f;
                smoothedRoadCorrection = 0f;
                cachedHandbrakeMagnitude = 0f;
            }
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

                // Only consider it a threat if it's VERY directly ahead (>0.85 = within ~30 degrees)
                // This prevents parked cars on the side of the road from being false positives
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
            try
            {
                // Use vehicle class to estimate size since GetDimensions isn't available
                VehicleClass vehClass = veh.ClassType;

                switch (vehClass)
                {
                    case VehicleClass.Motorcycles:
                    case VehicleClass.Cycles:
                        return BASE_COLLISION_RADIUS; // Smallest
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
                        return BASE_COLLISION_RADIUS + 1f; // Wide but low
                    default:
                        return BASE_COLLISION_RADIUS + 1f; // Default for unknown
                }
            }
            catch
            {
                return BASE_COLLISION_RADIUS + 1f; // Default fallback
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

            // When reversing, "ahead" means behind the vehicle (direction of travel)
            if (isReversing)
            {
                if (dotForward < -0.5f) return "ahead";  // Behind vehicle = ahead when reversing
                if (dotForward > 0.5f) return "behind";  // Front of vehicle = behind when reversing
                if (dotRight > 0.3f) return "left";      // Inverted for reversing
                if (dotRight < -0.3f) return "right";
                return "ahead";
            }
            else
            {
                if (dotForward > 0.5f) return "ahead";
                if (dotForward < -0.5f) return "behind";  // Now properly returns "behind"
                if (dotRight > 0.3f) return "right";
                if (dotRight < -0.3f) return "left";
                return "ahead"; // Default for edge cases (directly to side)
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

            // Look ahead based on speed (minimum 15m, up to 50m at high speed)
            float lookAhead = Math.Max(15f, Math.Min(50f, vehicleSpeed * 2f));

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

                // FILTER: Skip nodes that are not closely aligned (>45° difference)
                // Narrower filter prevents steering toward adjacent lanes on complex freeways
                if (Math.Abs(headingDiff) > 45f)
                {
                    continue;
                }

                // Found a valid same-direction node
                isOnValidRoad = true;
                roadHeadingDelta = headingDiff;

                // Also consider lateral offset from the road center
                GTA.Math.Vector3 toNode = GTA.Math.Vector3.Normalize(nodePos - playerPos);
                float dotRight = GTA.Math.Vector3.Dot(playerVeh.RightVector, toNode);

                // Combine heading correction and lateral offset correction (1.5x more aggressive)
                float headingCorrection = headingDiff / 60f;
                float lateralCorrection = dotRight * 0.75f;

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

            // Check multiple points ahead (near, medium, far)
            float[] distances = { 10f, 25f, 45f };
            float[] weights = { 0.5f, 0.3f, 0.2f }; // Near points weighted more

            for (int i = 0; i < distances.Length; i++)
            {
                float lookAhead = Math.Max(distances[i], vehicleSpeed * (i + 1) * 0.5f);
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

                        // FILTER: Skip nodes that are not closely aligned (>45° difference)
                        // Narrower filter prevents steering toward adjacent lanes on complex freeways
                        if (Math.Abs(headingDiff) > 45f)
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

                    // Direction to node for lateral correction (1.5x more aggressive)
                    GTA.Math.Vector3 toNode = GTA.Math.Vector3.Normalize(bestNodePos - playerPos);
                    float dotRight = GTA.Math.Vector3.Dot(playerVeh.RightVector, toNode);

                    float correction = (bestHeadingDiff / 60f) + (dotRight * 0.45f); // 1.5x more aggressive
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
        /// Checks road distance and handles auto-teleport logic.
        /// Should be called from ProcessSteeringAssist when drive assist is active.
        /// </summary>
        private void CheckRoadTeleport(Vehicle playerVeh, bool isFullMode, float threatTTC)
        {
            // Only works if setting is enabled and drive assist is on
            if (getSetting("roadTeleport") != 1) return;

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

                // If there's any road node nearby, we're probably on a curve - don't teleport
                if (anyNodeNearby)
                {
                    wasCloseToRoad = true;
                    offRoadStartTicks = 0;
                    return;
                }
            }

            // Condition 1: Collision imminent AND far from road - teleport immediately
            // Only if threat is VERY imminent (< 0.3s) and we're genuinely far from road
            if (threatTTC < 0.3f && currentRoadDistance > roadFarThreshold * 1.5f)
            {
                if (TeleportToNearestRoad(playerVeh))
                {
                    lastTeleportTicks = now;
                }
                return;
            }

            // Condition 2: Extremely far from any valid road node (scaled by speed)
            if (currentRoadDistance > roadCloseThreshold * 1.5f)
            {
                if (TeleportToNearestRoad(playerVeh))
                {
                    lastTeleportTicks = now;
                }
                return;
            }

            // Condition 3: Moderately far from road for 5+ seconds
            if (currentRoadDistance > roadFarThreshold)
            {
                if (wasCloseToRoad)
                {
                    // Just went off-road, start timer
                    offRoadStartTicks = now;
                    wasCloseToRoad = false;
                }
                else if (offRoadStartTicks > 0 && (now - offRoadStartTicks) > ROAD_TELEPORT_DELAY_TICKS)
                {
                    // Been off-road for 5+ seconds
                    if (TeleportToNearestRoad(playerVeh))
                    {
                        lastTeleportTicks = now;
                    }
                    return;
                }
            }
            else
            {
                // Close to road - reset tracking
                wasCloseToRoad = true;
                offRoadStartTicks = 0;
            }
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
                // OBSTACLE AVOIDANCE: Calculate steering to avoid collision (1.5x more aggressive)
                float steerUrgency = Math.Max(0f, 1f - (steerTTC / steerThreshold));
                float maxSteer = isFullMode ? 1.0f : 0.75f;
                targetSteer = avoidDir * steerUrgency * steerUrgency * maxSteer * 1.5f;

                // Smooth the obstacle avoidance steering
                smoothedSteerCorrection += (targetSteer - smoothedSteerCorrection) * smoothFactor;
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
                brakeMag = brakeUrgency * brakeUrgency;
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

            // ROAD FOLLOWING: Apply road guidance for lane keeping
            if (isOnValidRoad)
            {
                // Scale road correction based on mode (1.5x more aggressive for better road tracking)
                float roadStrength = isFullMode ? 0.9f : 0.45f;
                float targetRoadSteer = roadSteerCorrection * roadStrength;

                // Use slower smoothing for road following (more gradual)
                float roadSmoothFactor = Math.Min(1.0f, STEER_SMOOTHING_RATE * 0.5f * deltaTime);
                smoothedRoadCorrection += (targetRoadSteer - smoothedRoadCorrection) * roadSmoothFactor;
            }
            else
            {
                // No valid road - decay road correction
                smoothedRoadCorrection *= (1f - smoothFactor);
            }

            // COMBINE: Balance obstacle avoidance with road following
            // Road following should be primary unless there's a serious imminent threat
            float combinedSteer;

            // Calculate how urgent the STEERING threat is (0 = no threat, 1 = imminent collision)
            float threatUrgency = hasSteerThreat ? Math.Max(0f, Math.Min(1f, 1f - (steerTTC / steerThreshold))) : 0f;

            if (isOnValidRoad)
            {
                // On a valid road - prioritize road following unless threat is urgent
                if (threatUrgency > 0.7f && Math.Abs(smoothedSteerCorrection) > 0.2f)
                {
                    // URGENT threat (TTC very low) - let obstacle avoidance take over
                    // But still blend some road correction if they're going the same way
                    if (Math.Sign(smoothedSteerCorrection) == Math.Sign(smoothedRoadCorrection))
                    {
                        combinedSteer = smoothedSteerCorrection * 0.8f + smoothedRoadCorrection * 0.2f;
                    }
                    else
                    {
                        combinedSteer = smoothedSteerCorrection;
                    }
                }
                else if (threatUrgency > 0.3f && Math.Abs(smoothedSteerCorrection) > 0.1f)
                {
                    // MODERATE threat - blend both, favoring road if directions conflict
                    if (Math.Sign(smoothedSteerCorrection) == Math.Sign(smoothedRoadCorrection) ||
                        Math.Abs(smoothedRoadCorrection) < 0.1f)
                    {
                        // Same direction - use stronger of the two
                        combinedSteer = Math.Abs(smoothedSteerCorrection) > Math.Abs(smoothedRoadCorrection)
                            ? smoothedSteerCorrection * 0.6f + smoothedRoadCorrection * 0.4f
                            : smoothedRoadCorrection * 0.6f + smoothedSteerCorrection * 0.4f;
                    }
                    else
                    {
                        // Conflicting - favor road following, obstacle avoidance is probably wrong
                        combinedSteer = smoothedRoadCorrection * 0.7f + smoothedSteerCorrection * 0.3f;
                    }
                }
                else
                {
                    // LOW/NO threat - road following is primary
                    combinedSteer = smoothedRoadCorrection + smoothedSteerCorrection * 0.2f;
                }
            }
            else
            {
                // NOT on a valid road - obstacle avoidance is all we have
                if (hasSteerThreat)
                {
                    combinedSteer = smoothedSteerCorrection;
                }
                else
                {
                    combinedSteer = smoothedSteerCorrection * 0.5f; // Decay if no threat and no road
                }
            }

            combinedSteer = Math.Max(-1f, Math.Min(1f, combinedSteer));

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
        private void ApplyCachedSteeringInputs(Vehicle playerVeh)
        {
            // _SET_CONTROL_NORMAL hash: 0xE8A25867FBA3B05E
            // Control IDs: 59=Steer, 71=Accelerate, 72=Brake, 76=Handbrake

            // Apply steering
            if (Math.Abs(cachedSteerCorrection) > 0.05f)
            {
                if (cachedIsFullMode)
                {
                    // Full mode: override player steering
                    Function.Call((Hash)0xE8A25867FBA3B05E, 0, 59, cachedSteerCorrection);
                }
                else
                {
                    // Assistive mode: blend with player input (1.5x more aggressive blend)
                    float playerSteer = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, 59);
                    float blended = Math.Max(-1f, Math.Min(1f, playerSteer + cachedSteerCorrection * 1.05f)); // 0.7 * 1.5 = 1.05
                    Function.Call((Hash)0xE8A25867FBA3B05E, 0, 59, blended);
                }
            }

            // Apply braking
            if (cachedBrakeMagnitude > 0.1f)
            {
                Function.Call((Hash)0xE8A25867FBA3B05E, 0, 72, cachedBrakeMagnitude);

                // In full mode during ANY braking, completely disable player throttle input
                // This prevents the player from countering the safety braking
                if (cachedIsFullMode && cachedIsBraking)
                {
                    // DISABLE_CONTROL_ACTION - prevents player from using throttle while system is braking
                    Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 71, true); // Disable accelerate
                    Function.Call((Hash)0xE8A25867FBA3B05E, 0, 71, 0f);      // Force throttle to 0
                }
            }

            // Apply handbrake for corrective turns (Full mode only)
            if (cachedHandbrakeMagnitude > 0.1f && cachedIsFullMode)
            {
                Function.Call((Hash)0xE8A25867FBA3B05E, 0, 76, cachedHandbrakeMagnitude); // Handbrake

                // During handbrake turns, also disable player throttle to prevent fighting the maneuver
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 71, true);
                Function.Call((Hash)0xE8A25867FBA3B05E, 0, 71, 0f);
            }

            // Emergency measures for imminent collision
            if (cachedBrakeMagnitude >= 1.0f)
            {
                Function.Call((Hash)0xE8A25867FBA3B05E, 0, 76, 1.0f); // Full handbrake
                Function.Call((Hash)0xE8A25867FBA3B05E, 0, 71, 0f);   // Cut throttle

                // In Full mode, if collision is truly unavoidable, force stop
                if (cachedIsFullMode && threatTimeToCollision < 0.3f && playerVeh != null)
                {
                    playerVeh.Velocity = GTA.Math.Vector3.Zero;
                    if (DateTime.Now.Ticks - lastAssistAnnounceTicks > 10000000)
                    {
                        Tolk.Speak("Emergency halt!", true);
                        lastAssistAnnounceTicks = DateTime.Now.Ticks;
                    }
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
            if (id == "announceTime")
                result = "Time of Day Announcements. ";
            if (id == "announceHeadings")
                result = "Heading Change Announcements. ";
            if (id == "announceZones")
                result = "Street and Zone Change Announcements. ";

            //if (id == )
            return result;


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
        private float PerformShapeCast(GTA.Math.Vector3 startPos, GTA.Math.Vector3 forwardVec,
            GTA.Math.Vector3 rightVec, float maxRange, IntersectFlags flags, Entity exclude,
            out GTA.Math.Vector3 hitPosition, float vehicleSpeed = 0f)
        {
            hitPosition = GTA.Math.Vector3.Zero;
            float closestDist = float.MaxValue;

            // Calculate up vector for vertical rays
            GTA.Math.Vector3 upVec = new GTA.Math.Vector3(0, 0, 1);

            // Select ray pattern based on speed - more rays at higher speeds
            float[] hAngles = vehicleSpeed > SHAPE_CAST_SPEED_THRESHOLD
                ? SHAPE_CAST_H_ANGLES_FAST
                : SHAPE_CAST_H_ANGLES_SLOW;

            foreach (float hAngle in hAngles)
            {
                foreach (float vAngle in SHAPE_CAST_V_ANGLES)
                {
                    // Skip extreme combinations (far diagonal corners) for performance
                    if (Math.Abs(hAngle) > 20f && Math.Abs(vAngle) > 5f)
                        continue;

                    // Calculate ray direction with horizontal and vertical rotation
                    GTA.Math.Vector3 rayDir = RotateVector(forwardVec, rightVec, upVec, hAngle, vAngle);

                    RaycastResult ray = World.Raycast(startPos, startPos + (rayDir * maxRange),
                        flags, exclude);

                    if (ray.DidHit)
                    {
                        float dist = World.GetDistance(startPos, ray.HitPosition);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            hitPosition = ray.HitPosition;
                        }

                        // Early termination: if we found something very close, stop scanning
                        if (dist < maxRange * 0.2f)
                        {
                            return closestDist;
                        }
                    }
                }
            }

            return closestDist < float.MaxValue ? closestDist : -1f;
        }

        /// <summary>
        /// Rotates a vector by horizontal (yaw) and vertical (pitch) angles in degrees.
        /// </summary>
        private GTA.Math.Vector3 RotateVector(GTA.Math.Vector3 forward, GTA.Math.Vector3 right,
            GTA.Math.Vector3 up, float horizontalDegrees, float verticalDegrees)
        {
            // Convert to radians
            float hRad = horizontalDegrees * (float)(Math.PI / 180.0);
            float vRad = verticalDegrees * (float)(Math.PI / 180.0);

            // Horizontal rotation (around up axis): blend forward and right
            GTA.Math.Vector3 hRotated = forward * (float)Math.Cos(hRad) + right * (float)Math.Sin(hRad);
            hRotated = GTA.Math.Vector3.Normalize(hRotated);

            // Vertical rotation (around right axis): blend horizontal result with up
            GTA.Math.Vector3 result = hRotated * (float)Math.Cos(vRad) + up * (float)Math.Sin(vRad);
            return GTA.Math.Vector3.Normalize(result);
        }

        // ============================================
        // WINDOWS OCR METHODS
        // ============================================

        /// <summary>
        /// Writes a debug message to the OCR log file in the scripts folder.
        /// </summary>
        private void OcrLog(string message)
        {
            if (!ocrDebug) return;

            try
            {
                // Initialize log path if not set
                if (ocrLogPath == null)
                {
                    // Try multiple methods to find the scripts folder
                    string scriptsFolder = null;

                    // Method 1: AppDomain base directory + scripts
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string scriptsPath1 = Path.Combine(baseDir, "scripts");
                    if (Directory.Exists(scriptsPath1))
                    {
                        scriptsFolder = scriptsPath1;
                    }
                    // Method 2: Just use base directory (GTA V root)
                    else if (Directory.Exists(baseDir))
                    {
                        scriptsFolder = baseDir;
                    }
                    // Method 3: Try assembly location as fallback
                    else
                    {
                        string dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        if (!string.IsNullOrEmpty(dllPath))
                        {
                            scriptsFolder = Path.GetDirectoryName(dllPath);
                        }
                    }

                    // Final fallback: use temp folder
                    if (string.IsNullOrEmpty(scriptsFolder) || !Directory.Exists(scriptsFolder))
                    {
                        scriptsFolder = Path.GetTempPath();
                    }

                    ocrLogPath = Path.Combine(scriptsFolder, "OCR_output_log.txt");

                    // Clear the log file on first write
                    File.WriteAllText(ocrLogPath, $"=== OCR Debug Log Started {DateTime.Now} ===\r\n");
                    File.AppendAllText(ocrLogPath, $"Log path: {ocrLogPath}\r\n");
                }

                // Append timestamped message
                string logLine = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n";
                File.AppendAllText(ocrLogPath, logLine);
            }
            catch (Exception ex)
            {
                // Try to write error to a known location as last resort
                try
                {
                    string emergencyLog = Path.Combine(Path.GetTempPath(), "OCR_output_log.txt");
                    File.AppendAllText(emergencyLog, $"[{DateTime.Now}] Log error: {ex.Message}\r\n");
                }
                catch { }
            }
        }

        /// <summary>
        /// Initializes the Windows OCR engine if available.
        /// </summary>
        private void InitializeOcr()
        {
            if (ocrInitialized) return;

            try
            {
                OcrLog("Initializing OCR engine...");

                // Try to create OCR engine with English language
                var language = new Windows.Globalization.Language("en-US");
                if (OcrEngine.IsLanguageSupported(language))
                {
                    ocrEngine = OcrEngine.TryCreateFromLanguage(language);
                    OcrLog("OCR engine created for English (en-US)");
                }

                // Fall back to user profile language if English isn't available
                if (ocrEngine == null)
                {
                    ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                    if (ocrEngine != null)
                    {
                        OcrLog("OCR engine created from user profile languages");
                    }
                }

                ocrInitialized = true;

                if (ocrEngine == null)
                {
                    OcrLog("ERROR: OCR engine failed to initialize - no supported language found");
                }
            }
            catch (Exception ex)
            {
                ocrInitialized = true; // Don't keep retrying
                ocrEngine = null;
                OcrLog("ERROR: OCR init exception: " + ex.Message);
            }
        }

        /// <summary>
        /// Captures a region of the screen as a Bitmap using PrintWindow API.
        /// This works better with DirectX games than GDI CopyFromScreen.
        /// Requires Windows 8.1+ for PW_RENDERFULLCONTENT flag.
        /// </summary>
        private Bitmap CaptureScreenRegion(float leftPct, float topPct, float widthPct, float heightPct)
        {
            try
            {
                // Get the foreground window (should be GTA V)
                IntPtr hwnd = GetForegroundWindow();

                // Try to get DWM extended frame bounds (more accurate for DWM-composited windows)
                RECT windowRect;
                int dwmResult = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                    out windowRect, Marshal.SizeOf(typeof(RECT)));

                // Fall back to GetWindowRect if DWM fails
                if (dwmResult != 0)
                {
                    GetWindowRect(hwnd, out windowRect);
                }

                int windowWidth = windowRect.Right - windowRect.Left;
                int windowHeight = windowRect.Bottom - windowRect.Top;

                // Ensure valid window size
                if (windowWidth < 100 || windowHeight < 100)
                {
                    OcrLog($"Window too small: {windowWidth}x{windowHeight}");
                    return null;
                }

                // Capture the entire window using PrintWindow with PW_RENDERFULLCONTENT
                // This flag (Windows 8.1+) captures DirectX/hardware-accelerated content
                Bitmap fullWindow = new Bitmap(windowWidth, windowHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(fullWindow))
                {
                    IntPtr hdc = g.GetHdc();
                    bool success = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                    g.ReleaseHdc(hdc);

                    if (!success)
                    {
                        OcrLog("PrintWindow failed, falling back to CopyFromScreen");
                        // Fall back to traditional screen capture
                        g.CopyFromScreen(windowRect.Left, windowRect.Top, 0, 0,
                            new Size(windowWidth, windowHeight));
                    }
                }

                // Calculate and extract the requested region
                int regionLeft = (int)(windowWidth * leftPct);
                int regionTop = (int)(windowHeight * topPct);
                int regionWidth = (int)(windowWidth * widthPct);
                int regionHeight = (int)(windowHeight * heightPct);

                // Ensure minimum size
                if (regionWidth < 50 || regionHeight < 50)
                {
                    fullWindow.Dispose();
                    return null;
                }

                // Clamp to window bounds
                regionLeft = Math.Max(0, Math.Min(regionLeft, windowWidth - regionWidth));
                regionTop = Math.Max(0, Math.Min(regionTop, windowHeight - regionHeight));
                regionWidth = Math.Min(regionWidth, windowWidth - regionLeft);
                regionHeight = Math.Min(regionHeight, windowHeight - regionTop);

                // Extract the region
                Bitmap regionBitmap = new Bitmap(regionWidth, regionHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(regionBitmap))
                {
                    g.DrawImage(fullWindow,
                        new Rectangle(0, 0, regionWidth, regionHeight),
                        new Rectangle(regionLeft, regionTop, regionWidth, regionHeight),
                        GraphicsUnit.Pixel);
                }
                fullWindow.Dispose();

                // Save debug screenshots (first 3 only to avoid filling disk)
                if (ocrSaveScreenshots && ocrScreenshotCount < 3)
                {
                    try
                    {
                        string screenshotPath = Path.Combine(
                            Path.GetDirectoryName(ocrLogPath) ?? Path.GetTempPath(),
                            $"OCR_debug_{ocrScreenshotCount++}.png");
                        regionBitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
                        OcrLog($"Saved debug screenshot: {screenshotPath}");
                    }
                    catch (Exception ssEx)
                    {
                        OcrLog($"Failed to save screenshot: {ssEx.Message}");
                    }
                }

                return regionBitmap;
            }
            catch (Exception ex)
            {
                OcrLog("Screen capture failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Converts a System.Drawing.Bitmap to a Windows.Graphics.Imaging.SoftwareBitmap for OCR.
        /// </summary>
        private async Task<SoftwareBitmap> ConvertToSoftwareBitmap(Bitmap bitmap)
        {
            try
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    // Save bitmap to memory stream as PNG
                    bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    stream.Position = 0;

                    // Create IRandomAccessStream from memory stream
                    var randomAccessStream = new InMemoryRandomAccessStream();
                    await randomAccessStream.WriteAsync(stream.ToArray().AsBuffer());
                    randomAccessStream.Seek(0);

                    // Decode to SoftwareBitmap
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                    SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                    return softwareBitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Bitmap conversion failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Performs OCR on a screen region and returns the recognized text.
        /// </summary>
        private async Task<string> PerformOcrOnRegion(float leftPct, float topPct, float widthPct, float heightPct)
        {
            if (ocrEngine == null)
            {
                InitializeOcr();
                if (ocrEngine == null)
                {
                    OcrLog("PerformOcrOnRegion: OCR engine is null after init attempt");
                    return null;
                }
            }

            try
            {
                OcrLog($"PerformOcrOnRegion: Capturing region L={leftPct:P0} T={topPct:P0} W={widthPct:P0} H={heightPct:P0}");

                // Capture the screen region
                using (Bitmap screenshot = CaptureScreenRegion(leftPct, topPct, widthPct, heightPct))
                {
                    if (screenshot == null)
                    {
                        OcrLog("PerformOcrOnRegion: Screen capture returned null");
                        return null;
                    }

                    OcrLog($"PerformOcrOnRegion: Captured {screenshot.Width}x{screenshot.Height} bitmap");

                    // Convert to SoftwareBitmap
                    SoftwareBitmap softwareBitmap = await ConvertToSoftwareBitmap(screenshot);
                    if (softwareBitmap == null)
                    {
                        OcrLog("PerformOcrOnRegion: SoftwareBitmap conversion returned null");
                        return null;
                    }

                    try
                    {
                        OcrLog("PerformOcrOnRegion: Running OCR recognition...");

                        // Perform OCR
                        OcrResult result = await ocrEngine.RecognizeAsync(softwareBitmap);

                        if (result != null && result.Lines.Count > 0)
                        {
                            OcrLog($"PerformOcrOnRegion: Found {result.Lines.Count} lines of text");

                            // Combine all lines into a single string
                            List<string> lines = new List<string>();
                            foreach (var line in result.Lines)
                            {
                                string lineText = line.Text.Trim();
                                if (!string.IsNullOrWhiteSpace(lineText))
                                {
                                    lines.Add(lineText);
                                }
                            }
                            return string.Join(". ", lines);
                        }
                        else
                        {
                            OcrLog("PerformOcrOnRegion: OCR returned no lines");
                        }
                    }
                    finally
                    {
                        softwareBitmap?.Dispose();
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                OcrLog("PerformOcrOnRegion ERROR: " + ex.Message);
                return null;
            }
        }

        // ============================================
        // SHARED MEMORY GAME STATE COMMUNICATION
        // Writes game state to shared memory for external MenuHelper
        // ============================================

        /// <summary>
        /// Initializes the shared memory for communication with external MenuHelper.
        /// </summary>
        private void InitializeSharedMemory()
        {
            try
            {
                // Create or open the shared memory file
                sharedMemory = MemoryMappedFile.CreateOrOpen(
                    SHARED_MEMORY_NAME,
                    SHARED_MEMORY_SIZE,
                    MemoryMappedFileAccess.ReadWrite);

                sharedMemoryAccessor = sharedMemory.CreateViewAccessor(0, SHARED_MEMORY_SIZE);
                OcrLog("Shared memory initialized: " + SHARED_MEMORY_NAME);

                // Initialize with zeros
                for (int i = 0; i < SHARED_MEMORY_SIZE; i++)
                {
                    sharedMemoryAccessor.Write(i, (byte)0);
                }
            }
            catch (Exception ex)
            {
                OcrLog("Failed to initialize shared memory: " + ex.Message);
                sharedMemory = null;
                sharedMemoryAccessor = null;
            }
        }

        /// <summary>
        /// Writes current game state to shared memory for external process to read.
        /// Called every tick. When script pauses, the last state remains readable.
        /// </summary>
        private void HandlePauseMenuAccessibility()
        {
            try
            {
                // Get current game state from natives
                bool menuActive = Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE);
                int menuState = Function.Call<int>(Hash.GET_PAUSE_MENU_STATE);
                bool phoneVisible = Function.Call<bool>(Hash.CAN_PHONE_BE_SEEN_ON_SCREEN);

                // Only consider phone visible if game is fully loaded
                if (phoneVisible && Game.IsLoading)
                {
                    phoneVisible = false;
                }

                // NOTE: Pause menu selection reading was attempted but GTA V only exposes
                // numerical menu IDs, not the actual text content. Feature shelved.
                int menuSelection = 0;

                // Write state to shared memory for external MenuHelper to read
                WriteGameStateToSharedMemory(menuActive, menuState, phoneVisible, menuSelection);

                // Also handle state changes for in-process announcements (when not paused)
                if (menuActive && !pauseMenuWasActive)
                {
                    OcrLog($"Pause menu opened, state={menuState}");
                    // Don't announce here - let external helper handle it
                    pauseMenuWasActive = true;
                }
                else if (!menuActive && pauseMenuWasActive)
                {
                    OcrLog("Pause menu closed");
                    pauseMenuWasActive = false;
                }

                // Track phone state changes
                if (phoneVisible && !bgPhoneWasOpen)
                {
                    OcrLog("Phone opened");
                    bgPhoneWasOpen = true;
                }
                else if (!phoneVisible && bgPhoneWasOpen)
                {
                    OcrLog("Phone closed");
                    bgPhoneWasOpen = false;
                }
            }
            catch (Exception ex)
            {
                OcrLog("HandlePauseMenuAccessibility error: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes game state to shared memory.
        /// </summary>
        private void WriteGameStateToSharedMemory(bool menuActive, int menuState, bool phoneVisible, int menuSelection)
        {
            if (sharedMemoryAccessor == null) return;

            try
            {
                // Offset 0: isPauseMenuActive
                sharedMemoryAccessor.Write(0, (byte)(menuActive ? 1 : 0));

                // Offset 1: pauseMenuState
                sharedMemoryAccessor.Write(1, (byte)menuState);

                // Offset 2: isPhoneVisible
                sharedMemoryAccessor.Write(2, (byte)(phoneVisible ? 1 : 0));

                // Offset 3: menuSelection
                sharedMemoryAccessor.Write(3, (byte)menuSelection);

                // Offset 4-7: timestamp (Environment.TickCount)
                sharedMemoryAccessor.Write(4, Environment.TickCount);
            }
            catch (Exception ex)
            {
                OcrLog("WriteGameStateToSharedMemory error: " + ex.Message);
            }
        }

        /// <summary>
        /// Cleanup shared memory on script abort.
        /// </summary>
        private void CleanupSharedMemory()
        {
            try
            {
                // Write "not active" state before closing
                if (sharedMemoryAccessor != null)
                {
                    sharedMemoryAccessor.Write(0, (byte)0); // menu not active
                    sharedMemoryAccessor.Write(2, (byte)0); // phone not visible
                    sharedMemoryAccessor.Dispose();
                    sharedMemoryAccessor = null;
                }
                if (sharedMemory != null)
                {
                    sharedMemory.Dispose();
                    sharedMemory = null;
                }
                OcrLog("Shared memory cleaned up");
            }
            catch { }
        }

        private bool pauseMenuWasActive = false;

        /// <summary>
        /// Announces the current pause menu tab based on state index.
        /// </summary>
        private void AnnounceMenuTab(int state)
        {
            string tabName;
            switch (state)
            {
                case 0: tabName = "Map"; break;
                case 1: tabName = "Brief"; break;
                case 2: tabName = "Stats"; break;
                case 3: tabName = "Settings"; break;
                case 4: tabName = "Game"; break;
                case 5: tabName = "Gallery"; break;
                case 6: tabName = "Info"; break;
                case 7: tabName = "Store"; break;
                case 8: tabName = "Social Club"; break;
                case 9: tabName = "Friends"; break;
                case 10: tabName = "Crews"; break;
                default: tabName = $"Tab {state}"; break;
            }
            Tolk.Speak(tabName, true);
        }

        /// <summary>
        /// Triggers OCR for phone UI and speaks the result.
        /// </summary>
        private async void TriggerPhoneOcr()
        {
            if (ocrInProgress) return;
            if (DateTime.Now.Ticks - lastOcrTicks < OCR_COOLDOWN_TICKS) return;

            ocrInProgress = true;
            lastOcrTicks = DateTime.Now.Ticks;

            try
            {
                OcrLog("Phone OCR triggered");

                string ocrText = await PerformOcrOnRegion(
                    PHONE_REGION_LEFT, PHONE_REGION_TOP,
                    PHONE_REGION_WIDTH, PHONE_REGION_HEIGHT);

                if (string.IsNullOrWhiteSpace(ocrText))
                {
                    OcrLog("Phone OCR: No text found");
                }
                else if (ocrText != lastOcrText)
                {
                    OcrLog("Phone OCR result: " + ocrText);
                    lastOcrText = ocrText;
                    Tolk.Speak(ocrText, true);
                }
                else
                {
                    OcrLog("Phone OCR: Same text as before, not speaking");
                }
            }
            catch (Exception ex)
            {
                OcrLog("Phone OCR error: " + ex.Message);
            }
            finally
            {
                ocrInProgress = false;
            }
        }

        /// <summary>
        /// Triggers OCR for pause menu UI and speaks the result.
        /// </summary>
        private async void TriggerMenuOcr()
        {
            if (ocrInProgress) return;
            if (DateTime.Now.Ticks - lastOcrTicks < OCR_COOLDOWN_TICKS) return;

            ocrInProgress = true;
            lastOcrTicks = DateTime.Now.Ticks;

            try
            {
                OcrLog("Menu OCR triggered");

                string ocrText = await PerformOcrOnRegion(
                    MENU_REGION_LEFT, MENU_REGION_TOP,
                    MENU_REGION_WIDTH, MENU_REGION_HEIGHT);

                if (string.IsNullOrWhiteSpace(ocrText))
                {
                    OcrLog("Menu OCR: No text found");
                }
                else if (ocrText != lastOcrText)
                {
                    OcrLog("Menu OCR result: " + ocrText);
                    lastOcrText = ocrText;
                    Tolk.Speak(ocrText, true);
                }
                else
                {
                    OcrLog("Menu OCR: Same text as before, not speaking");
                }
            }
            catch (Exception ex)
            {
                OcrLog("Menu OCR error: " + ex.Message);
            }
            finally
            {
                ocrInProgress = false;
            }
        }

        /// <summary>
        /// Manual OCR trigger (can be bound to a key).
        /// Reads the center of the screen.
        /// </summary>
        private async void TriggerManualOcr()
        {
            if (ocrInProgress)
            {
                Tolk.Speak("OCR in progress", true);
                return;
            }

            ocrInProgress = true;
            OcrLog("Manual OCR triggered");
            Tolk.Speak("Reading screen", true);

            try
            {
                // Read a large center region
                string ocrText = await PerformOcrOnRegion(0.10f, 0.10f, 0.80f, 0.80f);

                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    OcrLog("Manual OCR result: " + ocrText);
                    Tolk.Speak(ocrText, true);
                }
                else
                {
                    OcrLog("Manual OCR: No text detected");
                    Tolk.Speak("No text detected", true);
                }
            }
            catch (Exception ex)
            {
                OcrLog("Manual OCR error: " + ex.Message);
                Tolk.Speak("OCR failed", true);
            }
            finally
            {
                ocrInProgress = false;
            }
        }

        // ============================================
        // GLOBAL KEYBOARD HOOK FOR PAUSE MENU OCR
        // Works even when ScriptHookVDotNet is paused
        // ============================================

        /// <summary>
        /// Called when the script is aborted - clean up resources
        /// </summary>
        private void onAborted(object sender, EventArgs e)
        {
            CleanupSharedMemory();
            StopMenuMonitorThread();
            UninstallKeyboardHook();
        }

        /// <summary>
        /// Installs a low-level keyboard hook to capture F12 for OCR during pause menu
        /// </summary>
        private void InstallKeyboardHook()
        {
            if (globalKeyboardHook != IntPtr.Zero)
                return; // Already installed

            try
            {
                // Keep a reference to prevent garbage collection
                keyboardProcDelegate = KeyboardHookCallback;

                using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
                using (var curModule = curProcess.MainModule)
                {
                    globalKeyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProcDelegate,
                        GetModuleHandle(curModule.ModuleName), 0);
                }

                if (globalKeyboardHook != IntPtr.Zero)
                {
                    OcrLog("Global keyboard hook installed - F12 for OCR");
                }
                else
                {
                    OcrLog("Failed to install keyboard hook: " + Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                OcrLog("Keyboard hook install error: " + ex.Message);
            }
        }

        /// <summary>
        /// Removes the global keyboard hook
        /// </summary>
        private void UninstallKeyboardHook()
        {
            if (globalKeyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(globalKeyboardHook);
                globalKeyboardHook = IntPtr.Zero;
                OcrLog("Global keyboard hook uninstalled");
            }
        }

        /// <summary>
        /// Keyboard hook callback - triggered on every keypress system-wide
        /// </summary>
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);

                // F12 = Trigger OCR reading of the screen
                if (vkCode == VK_F12)
                {
                    OcrLog("F12 pressed - triggering OCR from keyboard hook");
                    // Run OCR on a background thread to avoid blocking the hook
                    Task.Run(() => PerformHookTriggeredOcr());
                }
            }
            return CallNextHookEx(globalKeyboardHook, nCode, wParam, lParam);
        }

        /// <summary>
        /// OCR triggered from keyboard hook - runs on background thread
        /// </summary>
        private void PerformHookTriggeredOcr()
        {
            if (ocrInProgress)
            {
                Tolk.Speak("Reading in progress", true);
                return;
            }

            ocrInProgress = true;
            OcrLog("Hook-triggered OCR starting");
            Tolk.Speak("Reading", true);

            try
            {
                // Use synchronous OCR since we're already on a background thread
                using (Bitmap screenshot = CaptureScreenRegion(0.05f, 0.05f, 0.90f, 0.90f))
                {
                    if (screenshot == null)
                    {
                        Tolk.Speak("Capture failed", true);
                        return;
                    }

                    // Wait for async OCR to complete
                    var task = PerformOcrOnBitmap(screenshot);
                    task.Wait();
                    string result = task.Result;

                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        OcrLog("Hook OCR result: " + result);
                        Tolk.Speak(result, true);
                    }
                    else
                    {
                        Tolk.Speak("No text found", true);
                    }
                }
            }
            catch (Exception ex)
            {
                OcrLog("Hook OCR error: " + ex.Message);
                Tolk.Speak("Read failed", true);
            }
            finally
            {
                ocrInProgress = false;
            }
        }

        // ============================================
        // BACKGROUND MENU/PHONE MONITOR THREAD
        // Runs independently of game tick - works during pause menu
        // ============================================

        /// <summary>
        /// Starts the background menu monitor thread
        /// </summary>
        private void StartMenuMonitorThread()
        {
            if (menuMonitorThread != null && menuMonitorThread.IsAlive)
                return;

            menuMonitorRunning = true;
            menuMonitorThread = new Thread(MenuMonitorLoop);
            menuMonitorThread.IsBackground = true;
            menuMonitorThread.Name = "GTA11Y_MenuMonitor";
            menuMonitorThread.Start();
            OcrLog("Background menu monitor thread started");
        }

        /// <summary>
        /// Stops the background menu monitor thread
        /// </summary>
        private void StopMenuMonitorThread()
        {
            menuMonitorRunning = false;
            if (menuMonitorThread != null)
            {
                menuMonitorThread.Join(1000); // Wait up to 1 second
                menuMonitorThread = null;
            }
            OcrLog("Background menu monitor thread stopped");
        }

        /// <summary>
        /// Background thread loop that monitors for pause menu/phone via screen analysis
        /// </summary>
        private void MenuMonitorLoop()
        {
            OcrLog("MenuMonitorLoop started");

            while (menuMonitorRunning)
            {
                try
                {
                    // Check screen for pause menu indicators
                    // The pause menu has a dark overlay and specific UI elements
                    bool menuDetected = DetectPauseMenuViaScreen();
                    bool phoneDetected = DetectPhoneViaScreen();

                    // Handle pause menu state changes
                    if (menuDetected && !bgMenuWasOpen)
                    {
                        OcrLog("[BG] Pause menu detected via screen analysis");
                        Tolk.Speak("Pause menu", true);
                        bgMenuWasOpen = true;
                        bgLastOcrText = "";

                        // Give menu time to fully render, then OCR
                        Thread.Sleep(300);
                        PerformBackgroundMenuOcr();
                    }
                    else if (!menuDetected && bgMenuWasOpen)
                    {
                        OcrLog("[BG] Pause menu closed");
                        Tolk.Speak("Menu closed", true);
                        bgMenuWasOpen = false;
                        bgLastOcrText = "";
                    }
                    else if (menuDetected && bgMenuWasOpen)
                    {
                        // Menu still open - periodically check for changes
                        PerformBackgroundMenuOcr();
                    }

                    // Handle phone state changes (only when menu not open)
                    if (!menuDetected)
                    {
                        if (phoneDetected && !bgPhoneWasOpen)
                        {
                            OcrLog("[BG] Phone detected via screen analysis");
                            Tolk.Speak("Phone", true);
                            bgPhoneWasOpen = true;
                            bgLastOcrText = "";

                            Thread.Sleep(500); // Let phone animation complete
                            PerformBackgroundPhoneOcr();
                        }
                        else if (!phoneDetected && bgPhoneWasOpen)
                        {
                            OcrLog("[BG] Phone closed");
                            Tolk.Speak("Phone closed", true);
                            bgPhoneWasOpen = false;
                            bgLastOcrText = "";
                        }
                        else if (phoneDetected && bgPhoneWasOpen)
                        {
                            // Phone still open - periodically check for changes
                            PerformBackgroundPhoneOcr();
                        }
                    }

                    // Sleep between checks
                    Thread.Sleep(200); // Check 5 times per second
                }
                catch (Exception ex)
                {
                    OcrLog("[BG] Error in MenuMonitorLoop: " + ex.Message);
                    Thread.Sleep(500);
                }
            }

            OcrLog("MenuMonitorLoop ended");
        }

        /// <summary>
        /// Detects if pause menu is visible by checking screen characteristics
        /// The pause menu has a dark semi-transparent overlay
        /// </summary>
        private bool DetectPauseMenuViaScreen()
        {
            try
            {
                // Capture a small region at the top-left where menu header typically appears
                // The pause menu has a dark background with specific brightness levels
                using (Bitmap screenshot = CaptureScreenRegion(0.0f, 0.0f, 0.15f, 0.10f))
                {
                    if (screenshot == null) return false;

                    // Calculate average brightness of the region
                    // Pause menu has a dark overlay (low brightness)
                    long totalBrightness = 0;
                    int pixelCount = 0;

                    // Sample every 4th pixel for speed
                    for (int x = 0; x < screenshot.Width; x += 4)
                    {
                        for (int y = 0; y < screenshot.Height; y += 4)
                        {
                            Color pixel = screenshot.GetPixel(x, y);
                            totalBrightness += (pixel.R + pixel.G + pixel.B) / 3;
                            pixelCount++;
                        }
                    }

                    float avgBrightness = (float)totalBrightness / pixelCount;

                    // Also check a region in the center where menu content appears
                    using (Bitmap centerShot = CaptureScreenRegion(0.35f, 0.10f, 0.30f, 0.15f))
                    {
                        if (centerShot == null) return false;

                        // Check if center has text-like patterns (higher contrast)
                        // Pause menu typically has white/yellow text on dark background
                        int brightPixels = 0;
                        int darkPixels = 0;

                        for (int x = 0; x < centerShot.Width; x += 4)
                        {
                            for (int y = 0; y < centerShot.Height; y += 4)
                            {
                                Color pixel = centerShot.GetPixel(x, y);
                                int brightness = (pixel.R + pixel.G + pixel.B) / 3;
                                if (brightness > 200) brightPixels++;
                                else if (brightness < 50) darkPixels++;
                            }
                        }

                        // Pause menu: dark edges (low avgBrightness) + center has both bright text and dark background
                        bool hasMenuCharacteristics = avgBrightness < 40 && darkPixels > 50 && brightPixels > 10;

                        return hasMenuCharacteristics;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Detects if phone is visible by checking screen characteristics
        /// </summary>
        private bool DetectPhoneViaScreen()
        {
            try
            {
                // The phone appears in the center-right area with a distinctive shape
                // It has a bright screen area with specific aspect ratio
                using (Bitmap screenshot = CaptureScreenRegion(0.40f, 0.20f, 0.25f, 0.50f))
                {
                    if (screenshot == null) return false;

                    // Phone screen is typically bright with UI elements
                    // Check for high brightness concentration in phone-shaped region
                    int brightPixels = 0;
                    int totalSamples = 0;

                    for (int x = 0; x < screenshot.Width; x += 4)
                    {
                        for (int y = 0; y < screenshot.Height; y += 4)
                        {
                            Color pixel = screenshot.GetPixel(x, y);
                            int brightness = (pixel.R + pixel.G + pixel.B) / 3;
                            if (brightness > 150) brightPixels++;
                            totalSamples++;
                        }
                    }

                    // Phone typically has a bright screen area
                    float brightRatio = (float)brightPixels / totalSamples;

                    // If more than 15% of pixels are bright, might be phone
                    // This is a heuristic and may need tuning
                    return brightRatio > 0.15f && brightRatio < 0.6f;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Performs OCR on the pause menu from background thread
        /// </summary>
        private void PerformBackgroundMenuOcr()
        {
            try
            {
                // Initialize OCR if needed (thread-safe)
                if (ocrEngine == null)
                {
                    InitializeOcr();
                    if (ocrEngine == null) return;
                }

                using (Bitmap screenshot = CaptureScreenRegion(
                    MENU_REGION_LEFT, MENU_REGION_TOP,
                    MENU_REGION_WIDTH, MENU_REGION_HEIGHT))
                {
                    if (screenshot == null) return;

                    // Run OCR synchronously in background thread
                    var task = PerformOcrOnBitmap(screenshot);
                    task.Wait();
                    string ocrText = task.Result;

                    if (!string.IsNullOrWhiteSpace(ocrText) && ocrText != bgLastOcrText)
                    {
                        OcrLog("[BG] Menu OCR: " + ocrText);
                        bgLastOcrText = ocrText;
                        Tolk.Speak(ocrText, true);
                    }
                }
            }
            catch (Exception ex)
            {
                OcrLog("[BG] Menu OCR error: " + ex.Message);
            }
        }

        /// <summary>
        /// Performs OCR on the phone from background thread
        /// </summary>
        private void PerformBackgroundPhoneOcr()
        {
            try
            {
                if (ocrEngine == null)
                {
                    InitializeOcr();
                    if (ocrEngine == null) return;
                }

                using (Bitmap screenshot = CaptureScreenRegion(
                    PHONE_REGION_LEFT, PHONE_REGION_TOP,
                    PHONE_REGION_WIDTH, PHONE_REGION_HEIGHT))
                {
                    if (screenshot == null) return;

                    var task = PerformOcrOnBitmap(screenshot);
                    task.Wait();
                    string ocrText = task.Result;

                    if (!string.IsNullOrWhiteSpace(ocrText) && ocrText != bgLastOcrText)
                    {
                        OcrLog("[BG] Phone OCR: " + ocrText);
                        bgLastOcrText = ocrText;
                        Tolk.Speak(ocrText, true);
                    }
                }
            }
            catch (Exception ex)
            {
                OcrLog("[BG] Phone OCR error: " + ex.Message);
            }
        }

        /// <summary>
        /// Performs OCR on a bitmap and returns the text
        /// </summary>
        private async Task<string> PerformOcrOnBitmap(Bitmap bitmap)
        {
            try
            {
                SoftwareBitmap softwareBitmap = await ConvertToSoftwareBitmap(bitmap);
                if (softwareBitmap == null) return null;

                try
                {
                    OcrResult result = await ocrEngine.RecognizeAsync(softwareBitmap);

                    if (result != null && result.Lines.Count > 0)
                    {
                        List<string> lines = new List<string>();
                        foreach (var line in result.Lines)
                        {
                            string lineText = line.Text.Trim();
                            if (!string.IsNullOrWhiteSpace(lineText))
                            {
                                lines.Add(lineText);
                            }
                        }
                        return string.Join(". ", lines);
                    }
                }
                finally
                {
                    softwareBitmap?.Dispose();
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

    }
}