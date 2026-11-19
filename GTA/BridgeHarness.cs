using GTA;
using GTA.Native;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GrandTheftAccessibility
{
    /// <summary>
    /// iter-71: the diagnostic bridge's test-harness half.
    ///
    /// iter-65 shipped a bridge that could only READ. Every drive-assist
    /// investigation this project has queued since then has been written as an
    /// instruction to a human driver — "run the same La Puerta route", "provoke a
    /// wedge", "flip one gate between epochs" — and this mod's only driver is blind.
    /// That is why the archive is full of 3.7-minute sessions dirtied by trainer
    /// spawns and six-kilometre teleports: the measurement apparatus, not the
    /// controller, was the binding constraint.
    ///
    /// So this file is the rig. It places the car, sets the gates, sets the world
    /// conditions, and sequences the whole thing from outside the game, and every
    /// command returns what it ACHIEVED rather than what it was asked for — because
    /// a readback is something a blind tester can verify and a screenshot is not.
    ///
    /// Everything here runs on the script tick: DiagnosticBridge's socket threads
    /// only queue, and Pump() calls the handler. That contract is what makes it safe
    /// to touch GTA objects at all, and nothing in this file may break it — in
    /// particular nothing here may block, because the socket abandons a request
    /// after five seconds and Pump() services a bounded batch per tick.
    ///
    /// There is deliberately no arbitrary-native dispatcher. Every scenario the
    /// review asked for is reachable through the managed API, and iter-69 has just
    /// finished removing a build-specific poke at unidentified game memory; adding a
    /// general one back through a socket would undo that on the same afternoon.
    /// </summary>
    partial class GTA11Y
    {
        // World writes are a separate privilege from reading. A bridge enabled to
        // diagnose a live session must not be able to teleport the player's car out
        // from under them, so this is its own flag in gta11y-bridge.json and it
        // defaults off even when the bridge itself is enabled.
        private bool bridgeAllowWorldWrites;

        // Per-frame density natives have to be re-asserted every tick or they lapse,
        // so the requested values live here and TickBridgeHarness applies them.
        private float? bridgeVehicleDensity;
        private float? bridgePedDensity;

        // Set by settings.set so CheckGateToggles can attribute the change. Without
        // it every bridge-driven epoch boundary would be logged as src=menu, which is
        // exactly the confusion the iter-61 gate-drift work existed to remove.
        private string bridgeGateSource;

        /// <summary>
        /// The places the drive-assist protocol actually keeps returning to. Naming
        /// them is not a convenience: a scenario that reads "la-puerta-fwy" is
        /// reproducible across sessions and one that reads three floats retyped from
        /// a digest is not.
        /// </summary>
        private static readonly Dictionary<string, BridgeLocation> BRIDGE_LOCATIONS =
            new Dictionary<string, BridgeLocation>(StringComparer.OrdinalIgnoreCase)
            {
                // The two the iteration protocol has used since iter-57.
                ["sandy-shores"] = new BridgeLocation(1407f, 3596f, 35f, 210f,
                    "Sandy Shores, Alhambra Dr - the protocol's rural arm"),
                ["la-puerta"] = new BridgeLocation(-528f, -2247f, 6f, 90f,
                    "La Puerta, New Empire Way - the protocol's urban arm"),
                // 08-19 incident 29: ACC held full throttle from 15.4 to 32.4 m/s
                // while the mod's own lateral estimate crossed the carriageway.
                // This is the stretch accLateralInterlockV2 was written against.
                ["la-puerta-fwy"] = new BridgeLocation(-1000f, -2350f, 13f, 60f,
                    "La Puerta Freeway westbound - the incident-29 ACC overshoot run"),
                ["elgin"] = new BridgeLocation(-680f, -830f, 24f, 180f,
                    "Elgin Ave - the 08-15 signalised-queue cluster"),
                ["vinewood-hills"] = new BridgeLocation(-180f, 720f, 200f, 300f,
                    "Vinewood Hills - curved two-lane, the lane-target A/B route"),
                ["capital-blvd"] = new BridgeLocation(280f, -900f, 29f, 340f,
                    "Capital Blvd - multi-lane one-way, for lane offset checks"),
            };

        private sealed class BridgeLocation
        {
            public readonly float X, Y, Z, Heading;
            public readonly string Note;
            public BridgeLocation(float x, float y, float z, float heading, string note)
            { X = x; Y = y; Z = z; Heading = heading; Note = note; }
            public GTA.Math.Vector3 Pos { get { return new GTA.Math.Vector3(X, Y, Z); } }
        }

        // ================= dispatch =================

        /// <summary>Returns true when the command was a harness command and handled.</summary>
        private bool TryHandleHarnessCommand(string command, JObject args, out object result)
        {
            result = null;
            switch (command)
            {
                case "settings.get": result = BridgeSettingsGet(args); return true;
                case "settings.set": result = BridgeSettingsSet(args); return true;
                case "world.locations": result = BridgeWorldLocations(); return true;
                case "world.teleport": result = BridgeWorldTeleport(args); return true;
                case "world.conditions": result = BridgeWorldConditions(args); return true;
                case "vehicle.spawn": result = BridgeVehicleSpawn(args); return true;
                case "vehicle.set": result = BridgeVehicleSet(args); return true;
                case "scenario.start": result = BridgeScenarioStart(args); return true;
                case "scenario.status": result = BridgeScenarioStatus(); return true;
                case "scenario.abort": result = BridgeScenarioAbort("client"); return true;
                default: return false;
            }
        }

        private static readonly string[] HARNESS_COMMANDS =
        {
            "settings.get", "settings.set", "world.locations", "world.teleport",
            "world.conditions", "vehicle.spawn", "vehicle.set",
            "scenario.start", "scenario.status", "scenario.abort",
        };

        private void RequireWorldWrites(string command)
        {
            if (!bridgeAllowWorldWrites)
                throw new InvalidOperationException(
                    "'" + command + "' changes the game world and this bridge is read-only. "
                    + "Set \"allow_world_writes\": true in scripts/gta11y-bridge.json and reload the script.");
        }

        private Vehicle BridgeCurrentVehicle(bool required)
        {
            Ped ped = Game.Player.Character;
            Vehicle veh = ped != null && ped.Exists() && ped.IsInVehicle() ? ped.CurrentVehicle : null;
            if (required && (veh == null || !veh.Exists()))
                throw new InvalidOperationException("the player is not in a vehicle");
            return veh;
        }

        // ================= settings / gates =================

        private object BridgeSettingsGet(JObject args)
        {
            string one = args.Value<string>("id");
            var wanted = new List<string>();
            if (!string.IsNullOrWhiteSpace(one)) wanted.Add(one);
            JArray many = args["ids"] as JArray;
            if (many != null) foreach (JToken t in many) wanted.Add(t.ToString());

            var rows = new List<object>();
            foreach (Setting s in settingsMenu)
            {
                if (wanted.Count > 0 && !wanted.Any(w => string.Equals(w, s.id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                rows.Add(new
                {
                    id = s.id,
                    name = s.displayName,
                    value = s.value,
                    shipped_default = DefaultForSetting(s.id),
                    is_gate = GATE_SETTINGS.Contains(s.id),
                    instrumented = GATE_SETTINGS.Contains(s.id) && !GATE_UNINSTRUMENTED.Contains(s.id),
                });
            }
            // A requested id that does not exist is a typo the caller must see, not a
            // silently empty result they will read as "the gate is off".
            foreach (string w in wanted)
                if (!settingsMenu.Any(s => string.Equals(s.id, w, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("no such setting '" + w + "'; call settings.get with no id to list them");

            return new { count = rows.Count, settings = rows };
        }

        /// <summary>
        /// Batch gate write. This is the command the whole harness exists for: it
        /// turns "edit gta11ySettings.json before launching, never from the in-game
        /// menu" into a mid-session epoch boundary that the log records properly.
        ///
        /// setSetting() re-serialises the entire settings file per call, so eleven of
        /// them in one frame is eleven disk writes during a drive. Write straight into
        /// settingsMenu and save once — the same reason ApplyDriveAssistBaseline does.
        /// CheckGateToggles then diffs GATE_SETTINGS on the next frame by itself and
        /// emits a gate-toggle per gate plus a fresh manifest, which is exactly what
        /// triage-log.py cuts epochs on. Nothing here needs to log the change.
        /// </summary>
        private object BridgeSettingsSet(JObject args)
        {
            RequireWorldWrites("settings.set");
            var requested = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            JObject values = args["values"] as JObject;
            if (values != null)
                foreach (var pair in values)
                {
                    int v;
                    if (!int.TryParse(pair.Value.ToString(), out v))
                        throw new ArgumentException("setting '" + pair.Key + "' needs an integer value");
                    requested[pair.Key] = v;
                }

            string single = args.Value<string>("id");
            if (!string.IsNullOrWhiteSpace(single))
            {
                int? v = args.Value<int?>("value");
                if (v == null) throw new ArgumentException("'value' is required alongside 'id'");
                requested[single] = v.Value;
            }
            if (requested.Count == 0)
                throw new ArgumentException("pass 'values' as an object of id -> int, or 'id' and 'value'");

            var changes = new List<object>();
            foreach (var pair in requested)
            {
                Setting target = settingsMenu.FirstOrDefault(
                    s => string.Equals(s.id, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    throw new ArgumentException("no such setting '" + pair.Key + "'; nothing was written");
                changes.Add(new { id = target.id, from = target.value, to = pair.Value, changed = target.value != pair.Value });
            }

            // Validated everything before writing anything: a half-applied gate set is
            // an epoch nobody can interpret.
            foreach (var pair in requested)
            {
                Setting target = settingsMenu.First(
                    s => string.Equals(s.id, pair.Key, StringComparison.OrdinalIgnoreCase));
                target.value = pair.Value;
            }
            bridgeGateSource = "bridge";
            saveSettings();

            return new
            {
                applied = changes.Count,
                changes,
                note = "EVENT gate-toggle and a fresh EVENT gate-manifest follow on the next frame for any GATE_SETTINGS id; that pair is the epoch boundary triage-log.py splits on",
            };
        }

        // ================= world placement =================

        private object BridgeWorldLocations()
        {
            return new
            {
                count = BRIDGE_LOCATIONS.Count,
                locations = BRIDGE_LOCATIONS.Select(p => new
                {
                    name = p.Key,
                    x = p.Value.X,
                    y = p.Value.Y,
                    z = p.Value.Z,
                    heading = p.Value.Heading,
                    note = p.Value.Note,
                }).ToArray(),
            };
        }

        /// <summary>
        /// Place the player (and their vehicle) precisely, then report what was
        /// actually achieved. The readback is the point: it is how a tester who
        /// cannot see the screen confirms the run started where it was supposed to,
        /// and how a scenario knows whether to proceed.
        /// </summary>
        private object BridgeWorldTeleport(JObject args)
        {
            RequireWorldWrites("world.teleport");
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists()) throw new InvalidOperationException("player ped unavailable");

            GTA.Math.Vector3 target;
            float heading;
            string named = args.Value<string>("location");
            if (!string.IsNullOrWhiteSpace(named))
            {
                BridgeLocation loc;
                if (!BRIDGE_LOCATIONS.TryGetValue(named, out loc))
                    throw new ArgumentException("unknown location '" + named + "'; call world.locations");
                target = loc.Pos;
                heading = args.Value<float?>("heading") ?? loc.Heading;
            }
            else
            {
                float? x = args.Value<float?>("x"), y = args.Value<float?>("y"), z = args.Value<float?>("z");
                if (x == null || y == null || z == null)
                    throw new ArgumentException("pass 'location', or all of 'x', 'y' and 'z'");
                target = new GTA.Math.Vector3(x.Value, y.Value, z.Value);
                heading = args.Value<float?>("heading") ?? 0f;
            }

            Vehicle veh = BridgeCurrentVehicle(false);
            bool snap = args.Value<bool?>("snap_to_road") ?? false;
            string snapResult = "not requested";

            if (veh != null && veh.Exists())
            {
                veh.Position = target;
                veh.Heading = heading;
                if (snap)
                {
                    // Reuse the recovery ladder's own node search so a scenario starts
                    // on the same kind of node the assist would have chosen.
                    GTA.Math.Vector3 nodePos; float nodeHeading; float nodeDist;
                    if (FindNearestSameDirectionRoadNode(veh, out nodePos, out nodeHeading, out nodeDist))
                    {
                        veh.Position = new GTA.Math.Vector3(nodePos.X, nodePos.Y, nodePos.Z + 0.5f);
                        veh.Heading = args.Value<float?>("heading") ?? nodeHeading;
                        snapResult = "snapped " + nodeDist.ToString("F1") + " m";
                    }
                    else snapResult = "no road node found; left at the requested point";
                }
                try { Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, veh); } catch { }

                float speed = Math.Max(0f, Math.Min(80f, args.Value<float?>("speed_mps") ?? 0f));
                veh.Velocity = speed > 0.01f ? veh.ForwardVector * speed : GTA.Math.Vector3.Zero;
            }
            else
            {
                ped.Position = target;
                ped.Heading = heading;
            }

            // A teleport moves the car under the assist's feet, and the mod's own
            // teleport detector needs to see it or the next scan reads the jump as a
            // catastrophic lateral error. Say so in the log the same way a real
            // teleport does.
            BridgeNoteInLog("bridge-teleport", "to=" + FmtV(target)
                + " heading=" + heading.ToString("F1")
                + " snap=" + snap
                + " veh=" + (veh != null ? veh.DisplayName : "onfoot"));

            return BridgeAchievedPose(snapResult);
        }

        private object BridgeAchievedPose(string snapResult)
        {
            Ped ped = Game.Player.Character;
            Vehicle veh = BridgeCurrentVehicle(false);
            GTA.Math.Vector3 pos = veh != null && veh.Exists() ? veh.Position : ped.Position;
            return new
            {
                achieved = new
                {
                    position = Vec(pos),
                    heading = veh != null && veh.Exists() ? veh.Heading : ped.Heading,
                    speed_mps = veh != null && veh.Exists() ? veh.Speed : 0f,
                    on_all_wheels = veh != null && veh.Exists() && veh.IsOnAllWheels,
                    in_air = veh != null && veh.Exists() && veh.IsInAir,
                },
                street = World.GetStreetName(pos),
                zone = Function.Call<string>(Hash.GET_NAME_OF_ZONE, pos.X, pos.Y, pos.Z),
                snap = snapResult,
                note = "compare 'achieved' against what you asked for; placement is verified by this readback, not by looking at the screen",
            };
        }

        /// <summary>
        /// Clock, weather and traffic density. Two runs of one scenario are not
        /// comparable unless these match, and until now nothing could set them —
        /// which is a large part of why the archive's sessions do not compare.
        /// </summary>
        private object BridgeWorldConditions(JObject args)
        {
            RequireWorldWrites("world.conditions");
            var applied = new List<string>();

            int? hour = args.Value<int?>("hour");
            if (hour != null)
            {
                int h = Math.Max(0, Math.Min(23, hour.Value));
                int m = Math.Max(0, Math.Min(59, args.Value<int?>("minute") ?? 0));
                Function.Call(Hash.SET_CLOCK_TIME, h, m, 0);
                applied.Add("clock=" + h.ToString("00") + ":" + m.ToString("00"));
            }

            string weather = args.Value<string>("weather");
            if (!string.IsNullOrWhiteSpace(weather))
            {
                // Allowlisted rather than passed through: an unrecognised weather
                // string is accepted silently by the native and leaves the tester
                // wondering why two runs still differ.
                string[] ok = { "EXTRASUNNY", "CLEAR", "CLOUDS", "SMOG", "FOGGY", "OVERCAST",
                                "RAIN", "THUNDER", "CLEARING", "NEUTRAL", "SNOW", "BLIZZARD",
                                "SNOWLIGHT", "XMAS", "HALLOWEEN" };
                string w = weather.ToUpperInvariant();
                if (!ok.Contains(w))
                    throw new ArgumentException("weather must be one of: " + string.Join(", ", ok));
                Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, w);
                applied.Add("weather=" + w);
            }

            float? vd = args.Value<float?>("vehicle_density");
            if (vd != null)
            {
                bridgeVehicleDensity = Math.Max(0f, Math.Min(1f, vd.Value));
                applied.Add("vehicle_density=" + bridgeVehicleDensity.Value.ToString("F2"));
            }
            float? pd = args.Value<float?>("ped_density");
            if (pd != null)
            {
                bridgePedDensity = Math.Max(0f, Math.Min(1f, pd.Value));
                applied.Add("ped_density=" + bridgePedDensity.Value.ToString("F2"));
            }
            if (args.Value<bool?>("clear_density") == true)
            {
                bridgeVehicleDensity = null;
                bridgePedDensity = null;
                applied.Add("density=default");
            }

            if (applied.Count > 0) BridgeNoteInLog("bridge-conditions", string.Join(" ", applied));

            return new
            {
                applied,
                current = new
                {
                    clock = Function.Call<int>(Hash.GET_CLOCK_HOURS).ToString("00") + ":"
                          + Function.Call<int>(Hash.GET_CLOCK_MINUTES).ToString("00"),
                    vehicle_density = bridgeVehicleDensity,
                    ped_density = bridgePedDensity,
                },
                note = "density multipliers are per-frame natives; the harness re-asserts them every tick until cleared",
            };
        }

        // ================= vehicle =================

        private object BridgeVehicleSpawn(JObject args)
        {
            RequireWorldWrites("vehicle.spawn");
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists()) throw new InvalidOperationException("player ped unavailable");

            string name = args.Value<string>("model");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("'model' is required, e.g. \"tezeract\"");

            GTA.Math.Vector3 pos;
            float heading;
            string named = args.Value<string>("location");
            if (!string.IsNullOrWhiteSpace(named))
            {
                BridgeLocation loc;
                if (!BRIDGE_LOCATIONS.TryGetValue(named, out loc))
                    throw new ArgumentException("unknown location '" + named + "'; call world.locations");
                pos = loc.Pos;
                heading = args.Value<float?>("heading") ?? loc.Heading;
            }
            else
            {
                float? x = args.Value<float?>("x"), y = args.Value<float?>("y"), z = args.Value<float?>("z");
                pos = (x != null && y != null && z != null)
                    ? new GTA.Math.Vector3(x.Value, y.Value, z.Value)
                    : ped.Position + ped.ForwardVector * 6f;
                heading = args.Value<float?>("heading") ?? ped.Heading;
            }

            Model model = new Model(name);
            if (!model.IsVehicle) throw new ArgumentException("'" + name + "' is not a vehicle model");
            // Deliberately a SHORT request. The socket abandons a request after five
            // seconds and Pump() must not stall the game thread waiting on streaming;
            // a cold model simply asks the caller to repeat the command, by which time
            // the request already placed is usually satisfied.
            model.Request(800);
            if (!model.IsLoaded)
                throw new InvalidOperationException("model '" + name + "' is still streaming in; send the same command again in a moment");

            Vehicle spawned = World.CreateVehicle(model, pos, heading);
            model.MarkAsNoLongerNeeded();
            if (spawned == null || !spawned.Exists())
                throw new InvalidOperationException("the game refused to create '" + name + "' at that position");

            spawned.IsPersistent = true;
            try { Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, spawned); } catch { }
            spawned.IsEngineRunning = true;

            Vehicle previous = BridgeCurrentVehicle(false);
            if (args.Value<bool?>("warp") ?? true)
                ped.SetIntoVehicle(spawned, VehicleSeat.Driver);
            if ((args.Value<bool?>("delete_previous") ?? false) && previous != null && previous.Exists()
                && previous.Handle != spawned.Handle)
            {
                previous.IsPersistent = false;
                previous.Delete();
            }

            BridgeNoteInLog("bridge-spawn", "model=" + name + " at=" + FmtV(pos)
                + " heading=" + heading.ToString("F1"));

            return new
            {
                handle = spawned.Handle,
                model = spawned.Model.Hash,
                name = spawned.LocalizedName,
                pose = BridgeAchievedPose("not requested"),
            };
        }

        /// <summary>
        /// Set vehicle condition directly. This exists so the iter-71 damage telemetry
        /// (on-fire, petrol tank, attitude during a large engine drawdown) can be
        /// exercised without crashing a car over and over to provoke one — a test
        /// that is slow for anyone and genuinely hard to aim without sight.
        /// </summary>
        private object BridgeVehicleSet(JObject args)
        {
            RequireWorldWrites("vehicle.set");
            Vehicle veh = BridgeCurrentVehicle(true);
            var applied = new List<string>();

            if (args.Value<bool?>("repair") == true)
            {
                veh.Repair();
                applied.Add("repair");
            }
            float? bh = args.Value<float?>("body_health");
            if (bh != null) { veh.BodyHealth = Math.Max(0f, Math.Min(1000f, bh.Value)); applied.Add("body_health"); }
            float? eh = args.Value<float?>("engine_health");
            if (eh != null) { veh.EngineHealth = Math.Max(-4000f, Math.Min(1000f, eh.Value)); applied.Add("engine_health"); }
            float? pt = args.Value<float?>("petrol_tank_health");
            if (pt != null) { veh.PetrolTankHealth = Math.Max(-4000f, Math.Min(1000f, pt.Value)); applied.Add("petrol_tank_health"); }
            float? dirt = args.Value<float?>("dirt");
            if (dirt != null) { veh.DirtLevel = Math.Max(0f, Math.Min(15f, dirt.Value)); applied.Add("dirt"); }

            bool? fire = args.Value<bool?>("on_fire");
            if (fire == true) { Function.Call(Hash.START_ENTITY_FIRE, veh); applied.Add("on_fire=true"); }
            else if (fire == false) { Function.Call(Hash.STOP_ENTITY_FIRE, veh); applied.Add("on_fire=false"); }

            float? speed = args.Value<float?>("speed_mps");
            if (speed != null)
            {
                float s = Math.Max(0f, Math.Min(80f, speed.Value));
                veh.Velocity = s > 0.01f ? veh.ForwardVector * s : GTA.Math.Vector3.Zero;
                applied.Add("speed_mps");
            }

            if (applied.Count > 0)
                BridgeNoteInLog("bridge-vehicle-set", string.Join(",", applied)
                    + " bh=" + veh.BodyHealth.ToString("F0") + " eh=" + veh.EngineHealth.ToString("F0"));

            return new { applied, vehicle = BridgeVehicle() };
        }

        // ================= scenario runner =================

        private sealed class BridgeScenarioStep
        {
            public string Op;
            public JObject Args;
            public long StartedTicks;
            public string Outcome;
        }

        private sealed class BridgeScenario
        {
            public string Name;
            public List<BridgeScenarioStep> Steps = new List<BridgeScenarioStep>();
            public int Index;
            public string State = "running";   // running | done | aborted | failed
            public string Detail = "";
            public long StartedTicks;
            public readonly List<string> Trace = new List<string>();
        }

        private BridgeScenario bridgeScenario;

        private static readonly string[] SCENARIO_OPS =
        {
            "gates", "teleport", "vehicle", "conditions", "marker",
            "hold", "release", "wait", "wait_until", "capture",
        };

        private object BridgeScenarioStart(JObject args)
        {
            RequireWorldWrites("scenario.start");
            if (bridgeScenario != null && bridgeScenario.State == "running")
                throw new InvalidOperationException(
                    "scenario '" + bridgeScenario.Name + "' is still running at step "
                    + (bridgeScenario.Index + 1) + "; call scenario.abort first");

            JArray steps = args["steps"] as JArray;
            if (steps == null || steps.Count == 0)
                throw new ArgumentException("'steps' must be a non-empty array; each step needs an 'op' of: "
                    + string.Join(", ", SCENARIO_OPS));
            if (steps.Count > 64) throw new ArgumentException("a scenario is limited to 64 steps");

            var scenario = new BridgeScenario
            {
                Name = args.Value<string>("name") ?? "unnamed",
                StartedTicks = ModClock.Ticks,
            };
            foreach (JToken token in steps)
            {
                JObject step = token as JObject;
                if (step == null) throw new ArgumentException("every step must be an object");
                string op = step.Value<string>("op");
                if (string.IsNullOrWhiteSpace(op) || !SCENARIO_OPS.Contains(op))
                    throw new ArgumentException("step op must be one of: " + string.Join(", ", SCENARIO_OPS));
                scenario.Steps.Add(new BridgeScenarioStep { Op = op, Args = step });
            }

            bridgeScenario = scenario;
            BridgeNoteInLog("bridge-scenario", "start name=" + scenario.Name + " steps=" + scenario.Steps.Count);
            return new
            {
                started = scenario.Name,
                steps = scenario.Steps.Count,
                note = "poll scenario.status; the runner ticks with the game and never blocks the socket",
            };
        }

        private object BridgeScenarioStatus()
        {
            if (bridgeScenario == null) return new { state = "idle" };
            BridgeScenario s = bridgeScenario;
            return new
            {
                name = s.Name,
                state = s.State,
                detail = s.Detail,
                step = s.Index + 1,
                steps = s.Steps.Count,
                current_op = s.Index < s.Steps.Count ? s.Steps[s.Index].Op : null,
                elapsed_ms = (ModClock.Ticks - s.StartedTicks) / 10000L,
                trace = s.Trace.ToArray(),
                pose = s.State == "running" ? null : BridgeAchievedPose("not requested"),
            };
        }

        private object BridgeScenarioAbort(string why)
        {
            if (bridgeScenario == null) return new { state = "idle" };
            bridgeScenario.State = "aborted";
            bridgeScenario.Detail = "aborted by " + why;
            bridgeHeldControls.Clear();
            BridgeNoteInLog("bridge-scenario", "abort name=" + bridgeScenario.Name + " why=" + why);
            return BridgeScenarioStatus();
        }

        /// <summary>
        /// Called once per tick from onTick. Advances at most one step per tick, which
        /// keeps every step's effect on its own frame and therefore separable in the
        /// drive log — a scenario that fired four things into one frame would be
        /// indistinguishable from a bug.
        /// </summary>
        private void TickBridgeHarness()
        {
            // Density natives lapse every frame, so re-assert whatever was asked for.
            if (bridgeVehicleDensity != null)
            {
                try
                {
                    Function.Call(Hash.SET_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, bridgeVehicleDensity.Value);
                    Function.Call(Hash.SET_RANDOM_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, bridgeVehicleDensity.Value);
                    Function.Call(Hash.SET_PARKED_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, bridgeVehicleDensity.Value);
                }
                catch { }
            }
            if (bridgePedDensity != null)
            {
                try { Function.Call(Hash.SET_PED_DENSITY_MULTIPLIER_THIS_FRAME, bridgePedDensity.Value); } catch { }
            }

            BridgeScenario s = bridgeScenario;
            if (s == null || s.State != "running") return;
            if (s.Index >= s.Steps.Count)
            {
                s.State = "done";
                s.Detail = "all steps complete";
                BridgeNoteInLog("bridge-scenario", "done name=" + s.Name);
                return;
            }

            BridgeScenarioStep step = s.Steps[s.Index];
            if (step.StartedTicks == 0) step.StartedTicks = ModClock.Ticks;
            long elapsedMs = (ModClock.Ticks - step.StartedTicks) / 10000L;

            try
            {
                bool advance = RunScenarioStep(step, elapsedMs);
                if (!advance) return;
                s.Trace.Add("step " + (s.Index + 1) + " " + step.Op + ": " + (step.Outcome ?? "ok"));
                s.Index++;
            }
            catch (Exception ex)
            {
                // A scenario that fails must not leave the car under a held throttle.
                bridgeHeldControls.Clear();
                s.State = "failed";
                s.Detail = "step " + (s.Index + 1) + " (" + step.Op + "): " + ex.Message;
                s.Trace.Add(s.Detail);
                BridgeNoteInLog("bridge-scenario", "failed name=" + s.Name + " detail=" + s.Detail);
            }
        }

        /// <summary>Returns true when the step is finished and the runner should advance.</summary>
        private bool RunScenarioStep(BridgeScenarioStep step, long elapsedMs)
        {
            switch (step.Op)
            {
                case "gates":
                    BridgeSettingsSet(step.Args);
                    step.Outcome = "gates applied";
                    return true;

                case "teleport":
                    BridgeWorldTeleport(step.Args);
                    step.Outcome = "placed";
                    return true;

                case "vehicle":
                    if (!string.IsNullOrWhiteSpace(step.Args.Value<string>("model")))
                        BridgeVehicleSpawn(step.Args);
                    else BridgeVehicleSet(step.Args);
                    step.Outcome = "vehicle set";
                    return true;

                case "conditions":
                    BridgeWorldConditions(step.Args);
                    step.Outcome = "conditions set";
                    return true;

                case "marker":
                    BridgeLogWrite(step.Args);
                    step.Outcome = "marker written";
                    return true;

                case "hold":
                    BridgeInputHold(step.Args);
                    step.Outcome = "holding";
                    return true;

                case "release":
                    bridgeHeldControls.Clear();
                    step.Outcome = "released";
                    return true;

                case "capture":
                    BridgeScreenshot(step.Args);
                    step.Outcome = "captured";
                    return true;

                case "wait":
                {
                    long want = Math.Max(1, Math.Min(120000, step.Args.Value<long?>("ms") ?? 1000));
                    if (elapsedMs < want) return false;
                    step.Outcome = "waited " + elapsedMs + " ms";
                    return true;
                }

                case "wait_until":
                {
                    long timeout = Math.Max(1, Math.Min(120000, step.Args.Value<long?>("timeout_ms") ?? 15000));
                    string field = step.Args.Value<string>("field");
                    string comparison = step.Args.Value<string>("compare") ?? ">=";
                    float want = step.Args.Value<float?>("value") ?? 0f;
                    float? actual = BridgeScenarioField(field);

                    if (actual != null && BridgeCompare(actual.Value, comparison, want))
                    {
                        step.Outcome = field + "=" + actual.Value.ToString("F2") + " after " + elapsedMs + " ms";
                        return true;
                    }
                    if (elapsedMs >= timeout)
                    {
                        // A timeout is a RESULT, not an error: "the car never reached
                        // 25 m/s" is exactly the kind of finding a scenario exists to
                        // produce, so record it and carry on rather than aborting.
                        step.Outcome = "TIMEOUT after " + elapsedMs + " ms; " + field + "="
                            + (actual != null ? actual.Value.ToString("F2") : "unavailable")
                            + " never satisfied " + comparison + " " + want.ToString("F2");
                        return true;
                    }
                    return false;
                }

                default:
                    throw new ArgumentException("unhandled step op '" + step.Op + "'");
            }
        }

        private float? BridgeScenarioField(string field)
        {
            Vehicle veh = BridgeCurrentVehicle(false);
            switch ((field ?? "").ToLowerInvariant())
            {
                case "speed": return veh != null && veh.Exists() ? (float?)veh.Speed : null;
                case "lateral_error": return lastLaneLateralError;
                case "skew": return vehicleSkewAngle;
                case "stuck_layer": return stuckLayer;
                case "body_health": return veh != null && veh.Exists() ? (float?)veh.BodyHealth : null;
                case "engine_health": return veh != null && veh.Exists() ? (float?)veh.EngineHealth : null;
                case "mode": return (float)(int)currentDriveMode;
                default:
                    throw new ArgumentException("wait_until 'field' must be one of: speed, lateral_error, "
                        + "skew, stuck_layer, body_health, engine_health, mode");
            }
        }

        private static bool BridgeCompare(float actual, string comparison, float want)
        {
            switch (comparison)
            {
                case ">=": return actual >= want;
                case "<=": return actual <= want;
                case ">": return actual > want;
                case "<": return actual < want;
                case "==": return Math.Abs(actual - want) < 0.001f;
                case "abs>=": return Math.Abs(actual) >= want;
                case "abs<=": return Math.Abs(actual) <= want;
                default:
                    throw new ArgumentException("'compare' must be one of: >=, <=, >, <, ==, abs>=, abs<=");
            }
        }

        // ================= shared =================

        /// <summary>
        /// Every world write the harness makes goes in the drive log. A teleport or a
        /// respawn the log cannot see is the trainer problem all over again: 08-19's
        /// worst incident was a trainer spawn-fall that cost a full review pass before
        /// anyone could say it was not a controller failure.
        /// </summary>
        private void BridgeNoteInLog(string kind, string detail)
        {
            if (driveLogger == null || !driveLogger.IsRunning) return;
            driveLogger.Write("[F" + driveLogFrameCount + "] EVENT " + kind + ": " + detail);
        }
    }
}
