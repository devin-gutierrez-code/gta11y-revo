using System;
using System.Collections.Generic;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

namespace SanAndreasWildfires
{
    /// <summary>
    /// Represents an active wildfire that is continuously spreading.
    /// Tracks the center location, its current radius, and active fire entities.
    /// </summary>
    public class WildfireZone
    {
        public Vector3 Center { get; set; }
        public float CurrentRadius { get; set; } = 2.0f;
        public float MaxRadius { get; set; } = 100.0f;
        
        // Keeps track of the script fire handles so we can extinguish them
        public List<int> FireHandles { get; set; } = new List<int>();
        public int LastSpreadTime { get; set; } = 0;
    }

    public class SanAndreasWildfires : Script
    {
        // --- Configuration ---
        // Max distance the fire will spread from the ignition point
        private float maxFireRadius = 100f; 
        
        // Distance from the edge of the max radius before the fire is despawned
        private float extinguishDistance = 200f; 
        
        // Caps the number of script fires per zone to prevent GTA from crashing. 
        // Creates a realistic "Ring of Fire" effect where the perimeter burns.
        private int maxFiresPerZone = 60; 
        
        // How often the fire expands (in milliseconds)
        private int spreadIntervalMs = 1500; 

        // --- Mod State ---
        private bool isModActive = false;
        private bool isQHeld = false;
        private int qHeldStartTime = 0;
        private bool toggledThisHold = false;
        private int lastDetectionTime = 0;

        // Tracks all active wildfires in the world
        private List<WildfireZone> activeZones = new List<WildfireZone>();

        public SanAndreasWildfires()
        {
            // Bind to ScriptHookVDotNet events
            Tick += OnTick;
            Aborted += OnAbort;

            // Optional: Allows users to change settings via an INI file automatically generated in the scripts folder
            LoadSettings();
        }

        private void LoadSettings()
        {
            maxFireRadius = Settings.GetValue("Settings", "MaxRadius", 100f);
            extinguishDistance = Settings.GetValue("Settings", "ExtinguishDistance", 200f);
            Settings.SetValue("Settings", "MaxRadius", maxFireRadius);
            Settings.SetValue("Settings", "ExtinguishDistance", extinguishDistance);
            Settings.Save();
        }

        private void OnTick(object sender, EventArgs e)
        {
            HandleKeyInputs();

            if (isModActive)
            {
                DetectNewFires();
                ProcessWildfireZones();
            }
        }

        private void HandleKeyInputs()
        {
            // Detect if 'Q' is currently being pressed down
            if (Game.IsKeyPressed(Keys.Q))
            {
                if (!isQHeld)
                {
                    isQHeld = true;
                    qHeldStartTime = Game.GameTime;
                }
                // Check if it has been held for 3 seconds (3000 ms)
                else if (!toggledThisHold && (Game.GameTime - qHeldStartTime > 3000))
                {
                    ToggleModState();
                    toggledThisHold = true;
                }
            }
            else
            {
                // Reset when Q is released
                isQHeld = false;
                toggledThisHold = false;
            }
        }

        private void ToggleModState()
        {
            isModActive = !isModActive;

            if (isModActive)
            {
                GTA.UI.Notification.Show("~r~San Andreas Wildfires:~w~ ~g~ACTIVATED~w~");
            }
            else
            {
                GTA.UI.Notification.Show("~r~San Andreas Wildfires:~w~ ~r~DEACTIVATED~w~");
                ExtinguishAllZones();
            }
        }

        private void ExtinguishAllZones()
        {
            foreach (var zone in activeZones)
            {
                CleanUpFireHandles(zone);
            }
            activeZones.Clear();
        }

        private void DetectNewFires()
        {
            // Scan for new fires every 1 second to save CPU
            if (Game.GameTime - lastDetectionTime < 1000) return;
            lastDetectionTime = Game.GameTime;

            Vector3 playerPos = Game.Player.Character.Position;
            float detectionRadius = 150f;

            // Check if any nearby vehicles are on fire
            foreach (Vehicle v in World.GetAllVehicles())
            {
                if (v.IsOnFire && v.Position.DistanceTo(playerPos) < detectionRadius)
                {
                    TryCreateWildfireZone(v.Position);
                }
            }

            // Check if any nearby pedestrians are on fire
            foreach (Ped p in World.GetAllPeds())
            {
                if (p.IsOnFire && p.Position.DistanceTo(playerPos) < detectionRadius)
                {
                    TryCreateWildfireZone(p.Position);
                }
            }
        }

        private void TryCreateWildfireZone(Vector3 ignitionPoint)
        {
            // Ensure this ignition point isn't already inside an active wildfire radius
            foreach (var zone in activeZones)
            {
                if (zone.Center.DistanceTo(ignitionPoint) < zone.MaxRadius)
                {
                    return; // Area is already burning
                }
            }

            WildfireZone newZone = new WildfireZone()
            {
                Center = ignitionPoint,
                MaxRadius = this.maxFireRadius,
                LastSpreadTime = Game.GameTime
            };
            
            activeZones.Add(newZone);
            GTA.UI.Notification.Show("~o~Wildfire Warning:~w~ A new fire has started spreading rapidly!");
        }

        private void ProcessWildfireZones()
        {
            Vector3 playerPos = Game.Player.Character.Position;
            List<WildfireZone> zonesToRemove = new List<WildfireZone>();

            foreach (var zone in activeZones)
            {
                // Distance Check: Has the player moved 200m away from the *edge* of the max radius?
                float distanceToCenter = playerPos.DistanceTo(zone.Center);
                if (distanceToCenter > (zone.MaxRadius + this.extinguishDistance))
                {
                    zonesToRemove.Add(zone);
                    continue;
                }

                // Expansion Logic
                if (Game.GameTime - zone.LastSpreadTime > spreadIntervalMs)
                {
                    zone.LastSpreadTime = Game.GameTime;

                    // Expand the active burning radius, up to the maximum configuration
                    if (zone.CurrentRadius < zone.MaxRadius)
                    {
                        zone.CurrentRadius += 2.0f; 
                    }

                    SpawnFiresOnPerimeter(zone);
                }
            }

            // Clean up zones the player walked away from
            foreach (var zone in zonesToRemove)
            {
                CleanUpFireHandles(zone);
                activeZones.Remove(zone);
                GTA.UI.Notification.Show("~b~Wildfire Extinguished:~w~ You left the danger zone.");
            }
        }

        private void SpawnFiresOnPerimeter(WildfireZone zone)
        {
            Random rnd = new Random();
            int firesToSpawnPerTick = 4; // Spawn a few fires every interval

            for (int i = 0; i < firesToSpawnPerTick; i++)
            {
                // Pick a random angle to spawn the fire on the circular perimeter
                double angle = rnd.NextDouble() * Math.PI * 2;
                
                // Allow slight variance in the exact radius to make the fire look organic
                float radiusVariance = (float)(rnd.NextDouble() * 6f - 3f);
                float actualRadius = Math.Min(zone.CurrentRadius + radiusVariance, zone.MaxRadius);

                float targetX = zone.Center.X + actualRadius * (float)Math.Cos(angle);
                float targetY = zone.Center.Y + actualRadius * (float)Math.Sin(angle);
                float targetZ = zone.Center.Z;

                // Safely snap the Z coordinate to the ground so fires don't float in the air
                OutputArgument outZ = new OutputArgument();
                // Hash: GET_GROUND_Z_FOR_3D_COORD
                Function.Call((Hash)0xC906A7DAB05C8D2B, targetX, targetY, targetZ + 100f, outZ, false);
                float groundZ = outZ.GetResult<float>();
                
                if (groundZ != 0f) 
                {
                    targetZ = groundZ;
                }

                // Spawn the fire via native. Hash: START_SCRIPT_FIRE
                int fireHandle = Function.Call<int>((Hash)0x6B83617E04503888, targetX, targetY, targetZ, 25, false);
                zone.FireHandles.Add(fireHandle);
            }

            // Enforce limit: Remove the oldest fires. 
            // This stops game crashes and creates a realistic burnt-out center ("Ring of Fire").
            while (zone.FireHandles.Count > maxFiresPerZone)
            {
                int oldestFireHandle = zone.FireHandles[0];
                // Hash: REMOVE_SCRIPT_FIRE
                Function.Call((Hash)0x790125633649A4B3, oldestFireHandle);
                zone.FireHandles.RemoveAt(0);
            }
        }

        private void CleanUpFireHandles(WildfireZone zone)
        {
            foreach (int handle in zone.FireHandles)
            {
                Function.Call((Hash)0x790125633649A4B3, handle); // REMOVE_SCRIPT_FIRE
            }
            zone.FireHandles.Clear();
        }

        private void OnAbort(object sender, EventArgs e)
        {
            // Crucial: Always clean up native entities when the script is terminated/reloaded
            ExtinguishAllZones();
        }
    }
}