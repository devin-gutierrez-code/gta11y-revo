// Loader + accessor for GTA V's vehicleaihandlinginfo.meta data.
//
// The drive-assist consults this table for two things:
//   1. CURVE BRAKING — per-class lookup of "max safe speed for turn angle X"
//      (AICurvePoints). Folded in alongside the existing physics-derived
//      vSafe in ComputeCurveBrake so curve behaviour matches what GTA V's
//      own AI considers safe for the vehicle the player is driving.
//   2. DETECTION RANGE — per-class brake-lookahead distance derived from
//      MinBrakeDistance / MaxBrakeDistance / MaxSpeedAtBrakeDistance.
//      Replaces the older class-agnostic `vehicleSpeed * 3f` formulae.
//
// Load order on mod init:
//   1. <MyDocuments>\Rockstar Games\GTA V\ModSettings\vehicleaihandlinginfo.meta
//   2. <mod DLL folder>\vehicleaihandlinginfo.meta   (bundled with the mod)
//   3. Hardcoded defaults (always available).
//
// Parse errors fall through to the next path. The loader emits a single line
// to the drive-assist log noting which path won.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GTA;
using GTA.Native;

namespace GrandTheftAccessibility
{
    public enum VehicleAIHandlingClass { SPORTS_CAR, AVERAGE, CRAP, TRUCK }

    public class VehicleAIHandlingInfo
    {
        public string Name;
        public float MinBrakeDistance;
        public float MaxBrakeDistance;
        public float MaxSpeedAtBrakeDistance;
        public float AbsoluteMinSpeed;
        // Sorted-by-angle pairs. Angles are 0..180 degrees; speeds are in
        // the same units as the meta file (GTA convention is m/s here; the
        // U-turn entry of 5 ≈ 11 mph and AbsoluteMinSpeed=1.0 fit m/s well).
        public float[] CurveAngles;
        public float[] CurveSpeeds;

        // Linear interpolation between the bracketing curve points. Inputs
        // below the first angle clamp to the first speed; inputs above the
        // last angle clamp to the last. Returns 999f if no curve points
        // (defensive — shouldn't happen post-load).
        public float MaxSpeedForAngle(float angleDeg)
        {
            if (CurveAngles == null || CurveAngles.Length == 0) return 999f;
            float a = Math.Abs(angleDeg);
            if (a <= CurveAngles[0]) return CurveSpeeds[0];
            int last = CurveAngles.Length - 1;
            if (a >= CurveAngles[last]) return CurveSpeeds[last];
            for (int i = 0; i < last; i++)
            {
                if (a >= CurveAngles[i] && a <= CurveAngles[i + 1])
                {
                    float span = CurveAngles[i + 1] - CurveAngles[i];
                    float t = span > 0f ? (a - CurveAngles[i]) / span : 0f;
                    return CurveSpeeds[i] + t * (CurveSpeeds[i + 1] - CurveSpeeds[i]);
                }
            }
            return CurveSpeeds[last];
        }

        // Brake-lookahead distance: linear blend between MinBrakeDistance at
        // zero speed and MaxBrakeDistance at MaxSpeedAtBrakeDistance, then
        // clamps at MaxBrakeDistance for any higher speed. Matches the
        // implicit formula in GTA V's AI handling.
        public float BrakeLookaheadForSpeed(float speedMs)
        {
            if (MaxSpeedAtBrakeDistance <= 0.001f) return MaxBrakeDistance;
            float t = speedMs / MaxSpeedAtBrakeDistance;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            return MinBrakeDistance + t * (MaxBrakeDistance - MinBrakeDistance);
        }
    }

    public static class VehicleAIHandlingRegistry
    {
        public static VehicleAIHandlingInfo Sports { get; private set; }
        public static VehicleAIHandlingInfo Average { get; private set; }
        public static VehicleAIHandlingInfo Crap { get; private set; }
        public static VehicleAIHandlingInfo Truck { get; private set; }
        public static string LoadedFrom { get; private set; } = "(not loaded)";

        // Per-vehicle handle cache: only re-resolve when the player changes
        // vehicles. <1 µs per ProcessSteeringAssist call.
        private static int cachedHandle = 0;
        private static int cachedClassId = -1;
        private static VehicleAIHandlingInfo cachedInfo;

        public static void LoadOrFallback(string modDllFolder, string modSettingsFolder)
        {
            // 1) User override in ModSettings.
            if (!string.IsNullOrEmpty(modSettingsFolder))
            {
                string overridePath = Path.Combine(modSettingsFolder, "vehicleaihandlinginfo.meta");
                if (File.Exists(overridePath) && TryLoadFromXml(overridePath, "ModSettings"))
                    return;
            }
            // 2) Bundled copy next to the DLL.
            if (!string.IsNullOrEmpty(modDllFolder))
            {
                string bundledPath = Path.Combine(modDllFolder, "vehicleaihandlinginfo.meta");
                if (File.Exists(bundledPath) && TryLoadFromXml(bundledPath, "bundled"))
                    return;
            }
            // 3) Hardcoded fallback.
            LoadHardcoded();
            LoadedFrom = "hardcoded fallback";
        }

        private static bool TryLoadFromXml(string path, string label)
        {
            try
            {
                var doc = XDocument.Load(path);
                var infos = doc.Descendants("Item")
                    .Where(e => (string)e.Attribute("type") == "CAIHandlingInfo")
                    .ToList();
                VehicleAIHandlingInfo sports = null, avg = null, crap = null, truck = null;
                foreach (var item in infos)
                {
                    var info = ParseInfoElement(item);
                    if (info == null) continue;
                    switch (info.Name)
                    {
                        case "SPORTS_CAR": sports = info; break;
                        case "AVERAGE":    avg    = info; break;
                        case "CRAP":       crap   = info; break;
                        case "TRUCK":      truck  = info; break;
                    }
                }
                // Require all four to be present for a successful load —
                // partial files fall through to the next path so the mod
                // never runs with a half-populated registry.
                if (sports == null || avg == null || crap == null || truck == null)
                    return false;
                Sports = sports;
                Average = avg;
                Crap = crap;
                Truck = truck;
                LoadedFrom = label + ": " + path;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static VehicleAIHandlingInfo ParseInfoElement(XElement item)
        {
            string name = (string)item.Element("Name");
            if (string.IsNullOrEmpty(name)) return null;
            var info = new VehicleAIHandlingInfo { Name = name };
            info.MinBrakeDistance        = ReadValueAttr(item, "MinBrakeDistance",        10f);
            info.MaxBrakeDistance        = ReadValueAttr(item, "MaxBrakeDistance",        90f);
            info.MaxSpeedAtBrakeDistance = ReadValueAttr(item, "MaxSpeedAtBrakeDistance", 40f);
            info.AbsoluteMinSpeed        = ReadValueAttr(item, "AbsoluteMinSpeed",        1f);
            var pts = item.Element("AICurvePoints");
            if (pts != null)
            {
                var children = pts.Elements("Item").ToList();
                info.CurveAngles = new float[children.Count];
                info.CurveSpeeds = new float[children.Count];
                for (int i = 0; i < children.Count; i++)
                {
                    info.CurveAngles[i] = ReadValueAttr(children[i], "Angle", 0f);
                    info.CurveSpeeds[i] = ReadValueAttr(children[i], "Speed", 999f);
                }
            }
            else
            {
                info.CurveAngles = new float[0];
                info.CurveSpeeds = new float[0];
            }
            return info;
        }

        private static float ReadValueAttr(XElement parent, string childName, float fallback)
        {
            var child = parent.Element(childName);
            if (child == null) return fallback;
            string s = (string)child.Attribute("value");
            float v;
            if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v))
                return v;
            return fallback;
        }

        private static void LoadHardcoded()
        {
            // Mirrors the user's vehicleaihandlinginfo.meta verbatim. Update
            // these if the bundled .meta file is also updated; keeping them
            // in sync makes the fallback indistinguishable from the parsed
            // file for downstream code.
            Sports = new VehicleAIHandlingInfo
            {
                Name = "SPORTS_CAR",
                MinBrakeDistance        = 8f,
                MaxBrakeDistance        = 120f,
                MaxSpeedAtBrakeDistance = 70f,
                AbsoluteMinSpeed        = 1f,
                CurveAngles = new float[] { 0f,  2f,  6f, 12f, 22f, 28f, 40f, 90f, 180f },
                CurveSpeeds = new float[] { 130f, 115f, 88f, 60f, 42f, 34f, 26f, 15f,   6f },
            };
            Average = new VehicleAIHandlingInfo
            {
                Name = "AVERAGE",
                MinBrakeDistance        = 10f,
                MaxBrakeDistance        = 90f,
                MaxSpeedAtBrakeDistance = 40f,
                AbsoluteMinSpeed        = 1f,
                CurveAngles = new float[] { 0f,  2f,  6f, 12f, 22f, 28f, 40f, 90f, 180f },
                CurveSpeeds = new float[] { 120f, 110f, 80f, 50f, 35f, 26f, 20f, 12f,   5f },
            };
            Crap = new VehicleAIHandlingInfo
            {
                Name = "CRAP",
                MinBrakeDistance        = 10f,
                MaxBrakeDistance        = 100f,
                MaxSpeedAtBrakeDistance = 40f,
                AbsoluteMinSpeed        = 1f,
                CurveAngles = new float[] { 0f,  2f,  6f, 12f, 22f, 28f, 40f, 90f, 180f },
                CurveSpeeds = new float[] { 110f, 100f, 78f, 47f, 32f, 23f, 17f, 10f,   4f },
            };
            Truck = new VehicleAIHandlingInfo
            {
                Name = "TRUCK",
                MinBrakeDistance        = 10f,
                MaxBrakeDistance        = 120f,
                MaxSpeedAtBrakeDistance = 40f,
                AbsoluteMinSpeed        = 1f,
                CurveAngles = new float[] { 0f,  2f,  6f, 12f, 22f, 28f, 40f, 90f, 180f },
                CurveSpeeds = new float[] { 100f,  96f, 60f, 45f, 30f, 21f, 14f,  8f,   3f },
            };
        }

        // Map a GTA V vehicle class (Hash.GET_VEHICLE_CLASS, 0..22) to one
        // of our four AI handling classes. CRAP isn't reachable from GTA
        // class alone (it's a damage-state concept), so it's loaded but not
        // selected by this function.
        //   4  Muscle
        //   6  Sports
        //   7  SuperCars
        //   8  SportsClassics            -> SPORTS_CAR
        //   10 Industrial
        //   11 Utility
        //   17 Service
        //   19 Commercial                -> TRUCK
        //   everything else              -> AVERAGE
        public static VehicleAIHandlingInfo InfoForGtaClass(int classId)
        {
            switch (classId)
            {
                case 4: case 6: case 7: case 8:           return Sports ?? Average;
                case 10: case 11: case 17: case 19:        return Truck  ?? Average;
                default:                                   return Average;
            }
        }

        // Cached-by-handle lookup. The class only changes when the player
        // swaps vehicles, so we skip the native call on the common case.
        // Returns Average as a defensive fallback if the registry isn't
        // loaded yet (called before LoadOrFallback for some reason).
        public static VehicleAIHandlingInfo GetForVehicle(Vehicle veh)
        {
            if (Average == null) LoadHardcoded();
            if (veh == null) return Average;
            int handle = veh.Handle;
            if (handle == cachedHandle && cachedInfo != null)
                return cachedInfo;
            int classId = Function.Call<int>(Hash.GET_VEHICLE_CLASS, veh);
            cachedHandle = handle;
            cachedClassId = classId;
            cachedInfo = InfoForGtaClass(classId);
            return cachedInfo;
        }

        // Test hook so unit-ish runtime checks (or a manual /diag print) can
        // see what the player's vehicle resolved to without going through
        // the cache invalidation.
        public static int LastResolvedClassId => cachedClassId;
    }
}
