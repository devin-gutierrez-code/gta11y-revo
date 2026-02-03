using GTA;
using GTA.Native;
using System;
using System.Drawing;
using System.Windows.Forms;
		using System.Collections.Generic;
using System.Linq;
using DavyKager;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json;

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
		private int spawnMenuIndex= 0;
		private int mainMenuIndex = 0;
		private List<string> mainMenu = new List<string>();
		private int funMenuIndex = 0;
		private List<string> funMenu = new List<string>();
		private int driveMenuIndex = 0;
		private List<string> driveMenu = new List<string>();
		private int settingsMenuIndex = 0;
		private List <Setting> settingsMenu = new List<Setting>();

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
		private float autodriveSpeed = 20f; // Speed in m/s (adjustable with arrow keys)
		private int autodriveFlagMenuIndex = 0; // Which flag is selected in menu
		private bool[] autodriveFlags = new bool[32]; // Individual flag states
		
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
			Tolk.Load();
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

			locations.Add( new Location("MICHAEL'S HOUSE", new GTA.Math.Vector3(-852.4f, 160.0f, 65.6f)));
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
			autodriveSpeed = 20f;      // Default ~45 mph


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
			
			setupSettings();
		}

		private void onTick(object sender, EventArgs e)
		{
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

if (GTA.GameplayCamera.RelativePitch - p > 1f || GTA.GameplayCamera.RelativePitch -p < -1f)
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
				// AUTO-DRIVE MONITORING
				// ============================================
				if (isAutodriving)
				{
					// Check if player exited vehicle
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
								Game.Player.Character.CurrentVehicle.Speed * 2.237f : 0f;
							
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
						int ammoInClip = wep.AmmoInClip;
						int totalAmmo = wep.Ammo - ammoInClip;
						ammoInfo = ", " + ammoInClip + " in magazine, " + totalAmmo + " total";
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
					int currentClip = wep.AmmoInClip;
					
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
							
							// Minimum distance to avoid self-detection
							float minDist = inVehicle ? 1.5f : 0.5f;
							
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
								 if (dist < distCenter) { distCenter = dist; typeCenter = "ped"; nameCenter = "Pedestrian"; }
								}
								else if (dotForward < -0.5f && isMovingBackwards)
								{
								// BEHIND zone (only when moving backwards)
								if (dist < distBehind) { distBehind = dist; typeBehind = "ped"; nameBehind = "Pedestrian"; }
								}
								else if (dotForward > -0.2f) // Not behind us
								{
								if (dotRight < -0.3f && dist < distLeft)
								{
								  distLeft = dist; typeLeft = "ped"; nameLeft = "Pedestrian";
											}
											else if (dotRight > 0.3f && dist < distRight)
											{
												distRight = dist; typeRight = "ped"; nameRight = "Pedestrian";
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
								if (dist < distCenter) { distCenter = dist; typeCenter = "vehicle"; nameCenter = veh.LocalizedName; }
								}
								else if (dotForward < -0.5f && isMovingBackwards)
								{
								// BEHIND zone (only when moving backwards)
								if (dist < distBehind) { distBehind = dist; typeBehind = "vehicle"; nameBehind = veh.LocalizedName; }
								}
								else if (dotForward > -0.2f)
								{
								if (dotRight < -0.3f && dist < distLeft)
								{
								 distLeft = dist; typeLeft = "vehicle"; nameLeft = veh.LocalizedName;
								 }
											else if (dotRight > 0.3f && dist < distRight)
											{
												distRight = dist; typeRight = "vehicle"; nameRight = veh.LocalizedName;
											}
										}
							}
							
							// --- RAYCAST FOR WORLD GEOMETRY (3 directions) ---
							float rayHeight = inVehicle ? 0.5f : 1.0f;
							GTA.Math.Vector3 startPos = playerPos + new GTA.Math.Vector3(0, 0, rayHeight);
							
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
							
							// Side rays (90 degrees) - only when in vehicle for tight spaces
							if (inVehicle)
							{
							float sideRange = Math.Min(maxRange, 8f); // Side detection max 8m
							
							// Pure left ray
							GTA.Math.Vector3 pureLeft = new GTA.Math.Vector3(-rightVec.X, -rightVec.Y, 0);
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
										
										// Behind ray (only when moving backwards)
										if (isMovingBackwards)
										{
											GTA.Math.Vector3 behindDir = new GTA.Math.Vector3(-forwardVec.X, -forwardVec.Y, 0);
											RaycastResult rayBehind = World.Raycast(startPos, startPos + (behindDir * maxRange), IntersectFlags.Map, Game.Player.Character);
											if (rayBehind.DidHit)
											{
												float d = World.GetDistance(startPos, rayBehind.HitPosition);
												if (d >= minDist && d < distBehind) { distBehind = d; typeBehind = "world"; nameBehind = "Wall Behind"; }
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
							// ============================================
							
							// LEFT beep (panned left)
							if (beepLeft)
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
							if (beepCenter)
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
							if (beepRight)
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
										if (beepBehind)
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
						// If already auto-driving, cancel it
						if (isAutodriving)
						{
							isAutodriving = false;
							autodriveWanderMode = false;
							Game.Player.Character.Task.ClearAll();
							Tolk.Speak("Auto-drive cancelled. You have control.");
						}
						// Otherwise, TOGGLE the current flag
						else
						{
							autodriveFlags[autodriveFlagMenuIndex] = !autodriveFlags[autodriveFlagMenuIndex];
							string flagState = autodriveFlags[autodriveFlagMenuIndex] ? "ON" : "OFF";
							Tolk.Speak(autodriveFlagNames[autodriveFlagMenuIndex] + ", " + flagState);
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

					// Auto-Drive menu navigation (cycle through 32 flags)
					if (mainMenuIndex == 3)
					{
						if (!isAutodriving)
						{
							if (autodriveFlagMenuIndex > 0)
								autodriveFlagMenuIndex--;
							else
								autodriveFlagMenuIndex = 31; // Wrap to end
							
							string flagState = autodriveFlags[autodriveFlagMenuIndex] ? "ON" : "OFF";
							Tolk.Speak((autodriveFlagMenuIndex + 1) + ". " + autodriveFlagNames[autodriveFlagMenuIndex] + ", " + flagState);
						}
						else
						{
							Tolk.Speak("Stop auto-driving first with NumPad 2");
						}
					}

					if (mainMenuIndex == 4)
					{
						if (settingsMenuIndex > 0)
						{
							settingsMenuIndex--;
							if (settingsMenu[settingsMenuIndex].id == "detectionRadius")
							{
								int radiusIndex = settingsMenu[settingsMenuIndex].value;
								if (radiusIndex < 0 || radiusIndex >= detectionRadiusOptions.Length)
									radiusIndex = 1;
								Tolk.Speak("Detection Radius: " + (int)detectionRadiusOptions[radiusIndex] + " meters");
							}
							else
							{
								string toggle = "";
								if (settingsMenu[settingsMenuIndex].value == 0)
									toggle = "Off";
								if (settingsMenu[settingsMenuIndex].value == 1)
									toggle = "On";
								Tolk.Speak(settingsMenu[settingsMenuIndex].displayName + toggle);
							}

						}

						else
						{
							settingsMenuIndex = settingsMenu.Count - 1;
							if (settingsMenu[settingsMenuIndex].id == "detectionRadius")
							{
								int radiusIndex = settingsMenu[settingsMenuIndex].value;
								if (radiusIndex < 0 || radiusIndex >= detectionRadiusOptions.Length)
									radiusIndex = 1;
								Tolk.Speak("Detection Radius: " + (int)detectionRadiusOptions[radiusIndex] + " meters");
							}
							else
							{
								string toggle = "";
								if (settingsMenu[settingsMenuIndex].value == 0)
									toggle = "Off";
								if (settingsMenu[settingsMenuIndex].value == 1)
									toggle = "On";
								Tolk.Speak(settingsMenu[settingsMenuIndex].displayName + toggle);
							}

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
					if (mainMenuIndex == 3)
					{
						if (!isAutodriving)
						{
							if (autodriveFlagMenuIndex < 31)
								autodriveFlagMenuIndex++;
							else
								autodriveFlagMenuIndex = 0; // Wrap to start
							
							string flagState = autodriveFlags[autodriveFlagMenuIndex] ? "ON" : "OFF";
							Tolk.Speak((autodriveFlagMenuIndex + 1) + ". " + autodriveFlagNames[autodriveFlagMenuIndex] + ", " + flagState);
						}
						else
						{
							Tolk.Speak("Stop auto-driving first with NumPad 2");
						}
					}

					if (mainMenuIndex == 4)
					{
						if (settingsMenuIndex < settingsMenu.Count - 1)
						{
							settingsMenuIndex++;
							if (settingsMenu[settingsMenuIndex].id == "detectionRadius")
							{
								int radiusIndex = settingsMenu[settingsMenuIndex].value;
								if (radiusIndex < 0 || radiusIndex >= detectionRadiusOptions.Length)
									radiusIndex = 1;
								Tolk.Speak("Detection Radius: " + (int)detectionRadiusOptions[radiusIndex] + " meters");
							}
							else
							{
								string toggle = "";
								if (settingsMenu[settingsMenuIndex].value == 0)
									toggle = "Off";
								if (settingsMenu[settingsMenuIndex].value == 1)
									toggle = "On";
								Tolk.Speak(settingsMenu[settingsMenuIndex].displayName + toggle);
							}

						}
						else
						{
							settingsMenuIndex = 0;
							if (settingsMenu[settingsMenuIndex].id == "detectionRadius")
							{
								int radiusIndex = settingsMenu[settingsMenuIndex].value;
								if (radiusIndex < 0 || radiusIndex >= detectionRadiusOptions.Length)
									radiusIndex = 1;
								Tolk.Speak("Detection Radius: " + (int)detectionRadiusOptions[radiusIndex] + " meters");
							}
							else
							{
								string toggle = "";
								if (settingsMenu[settingsMenuIndex].value == 0)
									toggle = "Off";
								if (settingsMenu[settingsMenuIndex].value == 1)
									toggle = "On";
								Tolk.Speak(settingsMenu[settingsMenuIndex].displayName + toggle);
							}

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
				// AUTO-DRIVE SPEED CONTROL (Arrow Keys)
				// Left arrow = decrease speed, Right arrow = increase speed
				// Only active in Auto-Drive menu when not driving
				// ============================================
				if (e.KeyCode == Keys.Left && !keyState[13])
				{
					keyState[13] = true;
					if (mainMenuIndex == 3 && !isAutodriving)
					{
						autodriveSpeed = Math.Max(5f, autodriveSpeed - 5f);
						int speedMph = (int)Math.Round(autodriveSpeed * 2.237);
						Tolk.Speak("Speed: " + speedMph + " mph");
					}
				}

				if (e.KeyCode == Keys.Right && !keyState[14])
				{
					keyState[14] = true;
					if (mainMenuIndex == 3 && !isAutodriving)
					{
						autodriveSpeed = Math.Min(100f, autodriveSpeed + 5f);
						int speedMph = (int)Math.Round(autodriveSpeed * 2.237);
						Tolk.Speak("Speed: " + speedMph + " mph");
					}
				}

				// ============================================
				// AUTO-DRIVE START (NumPad Multiply)
				// Starts driving to waypoint or wandering
				// ============================================
				if (e.KeyCode == Keys.Multiply && !keyState[15])
				{
					keyState[15] = true;
					
					if (isAutodriving)
					{
						// Cancel if already driving
						isAutodriving = false;
						autodriveWanderMode = false;
						Game.Player.Character.Task.ClearAll();
						Tolk.Speak("Auto-drive cancelled. You have control.");
					}
					else if (!Game.Player.Character.IsInVehicle())
					{
						Tolk.Speak("You must be in a vehicle to use auto-drive");
					}
					else
					{
						// Calculate driving style from flags
						int drivingStyle = GetDrivingStyleFromFlags();
						
						// Start the driving task
						Vehicle veh = Game.Player.Character.CurrentVehicle;
						Ped driver = Game.Player.Character;
						
						// Set driver ability for better AI driving
						Function.Call(Hash.SET_DRIVER_ABILITY, driver, 1.0f);
						Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, 0.5f);
						
						// Check if waypoint exists
						bool hasWaypoint = Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE);
						
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
							
							int speedMph = (int)Math.Round(autodriveSpeed * 2.237);
							Tolk.Speak("Auto-driving to waypoint at " + speedMph + " mph. " + (int)autodriveStartDistance + " meters.");
						}
						else
						{
							// WANDER MODE - Drive randomly
							Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, 
								driver, veh, 
								autodriveSpeed, drivingStyle);
							
							isAutodriving = true;
							autodriveWanderMode = true;
							autodriveCheckTicks = DateTime.Now.Ticks;
							
							int speedMph = (int)Math.Round(autodriveSpeed * 2.237);
							Tolk.Speak("Wandering at " + speedMph + " mph.");
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
					string modeStr = autodriveWanderMode ? "wandering" : "driving to waypoint";
					result = result + "Currently " + modeStr + ". Press NumPad 2 to cancel.";
				}
				else
				{
					// Show current flag and state, plus speed
					string flagState = autodriveFlags[autodriveFlagMenuIndex] ? "ON" : "OFF";
					int speedMph = (int)Math.Round(autodriveSpeed * 2.237); // Convert m/s to mph
					result = result + "Flag " + (autodriveFlagMenuIndex + 1) + " of 32: " + autodriveFlagNames[autodriveFlagMenuIndex] + ", " + flagState + ". Speed: " + speedMph + " mph. NumPad Multiply to start. Arrows to adjust speed. Drives to waypoint if set, otherwise wanders.";
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
			string[] ids = {"announceHeadings", "announceZones", "announceTime", "altitudeIndicator", "targetPitchIndicator", "navigationAssist", "pickupDetection", "coverDetection", "waterHazardDetection", "vehicleHealthFeedback", "staminaFeedback", "interactableDetection", "trafficAwareness", "wantedLevelDetails", "slopeTerrainFeedback", "turnByTurnNavigation", "detectionRadius", "radioOff", "warpInsideVehicle", "onscreen", "speed", "godMode", "policeIgnore", "vehicleGodMode", "infiniteAmmo", "neverWanted", "superJump", "runFaster", "swimFaster", "exsplosiveAmmo", "fireAmmo", "explosiveMelee"};
			System.IO.StreamWriter fileOut;

			if (!System.IO.Directory.Exists(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings"))
				System.IO.Directory.CreateDirectory(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings");

				if (!System.IO.File.Exists(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/gta11ySettings.json"))
				{
				fileOut = new System.IO.StreamWriter(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/gta11ySettings.json");

				foreach (string i in ids)
				{
					if (i == "announceHeadings" || i == "announceZones" || i == "altitudeIndicator" || i == "announceTime" || i == "turnByTurnNavigation")
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
catch(Exception e)
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
							if (i == "announceHeadings" || i == "announceZones" || i == "altitudeIndicator" || i == "announceTime" || i == "targetPitchIndicator" || i == "speed" || i == "turnByTurnNavigation")
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
						catch(Exception e)
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
				System.IO.StreamWriter  fileOut = new System.IO.StreamWriter(@Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/Rockstar Games/GTA V/ModSettings/gta11ySettings.json");
				string result = JsonConvert.SerializeObject(dictionary, Formatting.Indented);
			fileOut.Write(result);
			fileOut.Close();
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
for (int i = 0; i < settingsMenu.Count; i ++)
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

	}
	}