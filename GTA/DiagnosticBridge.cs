using GTA;
using GTA.Native;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GrandTheftAccessibility
{
    /// <summary>
    /// Loopback-only diagnostic transport. Socket threads only parse and queue requests;
    /// GTA/SHVDN objects are touched exclusively by Pump(), called from GTA11Y.onTick.
    /// </summary>
    internal sealed class DiagnosticBridge : IDisposable
    {
        internal sealed class Request
        {
            public long Id;
            public string Command;
            public JObject Args;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public JObject Response;
            public volatile bool Cancelled;
        }

        private const int MaxClients = 4;
        private const int MaxRequestBytes = 65536;
        private readonly ConcurrentQueue<Request> requests = new ConcurrentQueue<Request>();
        private readonly Func<string, JObject, object> handler;
        private readonly int port;
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private int clients;
        private long ticks;
        private long lastTickWall;

        public DiagnosticBridge(int port, Func<string, JObject, object> handler)
        {
            this.port = port;
            this.handler = handler;
        }

        public bool IsRunning { get { return running; } }
        public int Port { get { return port; } }
        public long Ticks { get { return Interlocked.Read(ref ticks); } }
        public long MillisecondsSinceTick
        {
            get
            {
                long stamp = Interlocked.Read(ref lastTickWall);
                return stamp == 0 ? -1 : Math.Max(0, (DateTime.UtcNow.Ticks - stamp) / TimeSpan.TicksPerMillisecond);
            }
        }

        public void Start()
        {
            if (running) return;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(MaxClients);
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "GTA11YDiagnosticAccept" };
            acceptThread.Start();
        }

        public void Pump()
        {
            Interlocked.Increment(ref ticks);
            Interlocked.Exchange(ref lastTickWall, DateTime.UtcNow.Ticks);
            Request request;
            int serviced = 0;
            while (serviced++ < 32 && requests.TryDequeue(out request))
            {
                if (request.Cancelled) continue;
                try
                {
                    object result = handler(request.Command, request.Args ?? new JObject());
                    request.Response = Ok(request.Id, result);
                }
                catch (Exception ex)
                {
                    request.Response = Error(request.Id, ex.Message);
                }
                finally { request.Done.Set(); }
            }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    if (Interlocked.Increment(ref clients) > MaxClients)
                    {
                        Interlocked.Decrement(ref clients);
                        client.Close();
                        continue;
                    }
                    var thread = new Thread(() => ClientLoop(client)) { IsBackground = true, Name = "GTA11YDiagnosticClient" };
                    thread.Start();
                }
                catch (SocketException) { if (!running) return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        private void ClientLoop(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 12000;
                client.SendTimeout = 12000;
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    while (running)
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;
                        if (Encoding.UTF8.GetByteCount(line) > MaxRequestBytes)
                        {
                            writer.WriteLine(Error(0, "request too large").ToString(Formatting.None));
                            break;
                        }
                        JObject envelope;
                        long id = 0;
                        try
                        {
                            envelope = JObject.Parse(line);
                            id = envelope.Value<long?>("id") ?? 0;
                        }
                        catch (Exception ex)
                        {
                            writer.WriteLine(Error(id, "malformed JSON: " + ex.Message).ToString(Formatting.None));
                            continue;
                        }
                        string command = envelope.Value<string>("cmd");
                        if (string.IsNullOrWhiteSpace(command))
                        {
                            writer.WriteLine(Error(id, "request has no cmd").ToString(Formatting.None));
                            continue;
                        }
                        if (command == "ping")
                        {
                            writer.WriteLine(Ok(id, new
                            {
                                bridge = "gta11y",
                                protocol = 1,
                                pid = Process.GetCurrentProcess().Id,
                                port,
                                ticks = Ticks,
                                ms_since_tick = MillisecondsSinceTick
                            }).ToString(Formatting.None));
                            continue;
                        }
                        var request = new Request
                        {
                            Id = id,
                            Command = command,
                            Args = envelope["args"] as JObject ?? new JObject()
                        };
                        requests.Enqueue(request);
                        if (!request.Done.Wait(5000))
                        {
                            request.Cancelled = true;
                            writer.WriteLine(Error(id, "timed out waiting for the GTA V script tick; the game may be paused, loading, or hung").ToString(Formatting.None));
                            continue;
                        }
                        writer.WriteLine(request.Response.ToString(Formatting.None));
                    }
                }
            }
            catch { }
            finally { Interlocked.Decrement(ref clients); }
        }

        private static JObject Ok(long id, object result)
        {
            return new JObject { ["id"] = id, ["ok"] = true, ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result) };
        }

        private static JObject Error(long id, string error)
        {
            return new JObject { ["id"] = id, ["ok"] = false, ["error"] = error ?? "command failed" };
        }

        public void Dispose()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            try { if (acceptThread != null && acceptThread.IsAlive) acceptThread.Join(1000); } catch { }
            Request request;
            while (requests.TryDequeue(out request))
            {
                request.Response = Error(request.Id, "bridge shutting down");
                request.Done.Set();
            }
        }
    }

    internal sealed class BridgeSapiSpeaker : IDisposable
    {
        private readonly BlockingCollection<string> pending = new BlockingCollection<string>(16);
        private readonly Thread thread;
        private volatile string status = "starting";

        public BridgeSapiSpeaker()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "GTA11YBridgeSAPI" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public string Status { get { return status; } }
        public void Speak(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required");
            if (text.Length > 1000) throw new ArgumentException("text is limited to 1000 characters");
            if (!pending.TryAdd(text)) throw new InvalidOperationException("SAPI queue is full");
        }

        private void Run()
        {
            object voice = null;
            try
            {
                Type type = Type.GetTypeFromProgID("SAPI.SpVoice", true);
                voice = Activator.CreateInstance(type);
                status = "ready";
                foreach (string text in pending.GetConsumingEnumerable())
                {
                    status = "speaking";
                    type.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, new object[] { text, 0 });
                    status = "ready";
                }
            }
            catch (Exception ex) { status = "failed:" + ex.GetType().Name; }
            finally { if (voice != null && Marshal.IsComObject(voice)) Marshal.FinalReleaseComObject(voice); }
        }

        public void Dispose()
        {
            pending.CompleteAdding();
            try { thread.Join(1500); } catch { }
            pending.Dispose();
        }
    }

    partial class GTA11Y
    {
        // iter-71: bumped with the harness. mod.state is often the FIRST thing a
        // session reads, and a stale iteration string here would let a harness command
        // fail against an old build with no clue why.
        private const string BridgeIteration = "iter71-bridge-harness-authority-convergence";
        private DiagnosticBridge diagnosticBridge;
        private BridgeSapiSpeaker bridgeSapi;
        private readonly Dictionary<int, BridgeHeldControl> bridgeHeldControls = new Dictionary<int, BridgeHeldControl>();
        private int bridgeScreenshotSequence;

        private sealed class BridgeHeldControl
        {
            public float Value;
            public long UntilUtcTicks;
        }

        private void StartDiagnosticBridge()
        {
            try
            {
                // SHVDN's established asset convention is relative to the GTA V root
                // (every map/audio loader uses scripts/...). Assembly.Location can point
                // at SHVDN's load context instead of the deployed scripts directory.
                string path = Path.Combine("scripts", "gta11y-bridge.json");
                if (!File.Exists(path))
                {
                    string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    path = Path.Combine(dir ?? "", "gta11y-bridge.json");
                }
                if (!File.Exists(path)) return;
                JObject config = JObject.Parse(File.ReadAllText(path));
                if (!(config.Value<bool?>("enabled") ?? false)) return;
                int port = config.Value<int?>("port") ?? 27738;
                if (port < 1024 || port > 65535) throw new InvalidDataException("bridge port must be 1024-65535");
                // iter-71: reading a live session and steering it are different
                // privileges. A bridge left enabled for diagnosis must not be able to
                // teleport the player's car out from under them, so the harness has
                // its own flag and it defaults off even when the bridge is on.
                bridgeAllowWorldWrites = config.Value<bool?>("allow_world_writes") ?? false;
                bridgeSapi = new BridgeSapiSpeaker();
                diagnosticBridge = new DiagnosticBridge(port, HandleDiagnosticCommand);
                diagnosticBridge.Start();
            }
            catch (Exception ex)
            {
                diagnosticBridge = null;
                try { bridgeSapi?.Dispose(); } catch { }
                bridgeSapi = null;
                Speech.Critical("Developer bridge could not start. " + ex.Message);
            }
        }

        private void PumpDiagnosticBridge()
        {
            if (diagnosticBridge == null) return;
            diagnosticBridge.Pump();
        }

        private void ApplyDiagnosticInputHolds()
        {
            long now = DateTime.UtcNow.Ticks;
            if (bridgeHeldControls.Count == 0) return;
            foreach (var pair in bridgeHeldControls.ToArray())
            {
                if (pair.Value.UntilUtcTicks <= now) bridgeHeldControls.Remove(pair.Key);
                else WriteControlValue(pair.Key, pair.Value.Value);
            }
        }

        private void StopDiagnosticBridge()
        {
            bridgeHeldControls.Clear();
            try { diagnosticBridge?.Dispose(); } finally { diagnosticBridge = null; }
            try { bridgeSapi?.Dispose(); } finally { bridgeSapi = null; }
        }

        private object HandleDiagnosticCommand(string command, JObject args)
        {
            // iter-71: the harness half (settings, placement, scenarios) lives in
            // BridgeHarness.cs. It is asked first so this switch stays the transport's
            // read-only surface and the two halves cannot drift out of sync.
            object harnessResult;
            if (TryHandleHarnessCommand(command, args, out harnessResult)) return harnessResult;

            switch (command)
            {
                case "help": return BridgeHelp();
                case "state": return BridgeState();
                case "mod.state": return BridgeModState();
                case "player": return BridgePlayer();
                case "vehicle": return BridgeVehicle();
                case "drive.state": return BridgeDriveState();
                case "shapecasts": return BridgeShapecasts();
                case "scanner": return BridgeScanner(args);
                case "input.controls": return BridgeInputControls();
                case "input.hold": return BridgeInputHold(args);
                case "input.release": return BridgeInputRelease(args);
                case "input.state": return BridgeInputState();
                case "screenshot": return BridgeScreenshot(args);
                case "log.tail": return BridgeLogTail(args);
                case "log.write": return BridgeLogWrite(args);
                case "speech.sapi": return BridgeSapiSpeak(args);
                case "speech.status": return new { sapi = bridgeSapi == null ? "unavailable" : bridgeSapi.Status };
                default: throw new ArgumentException("unknown command '" + command + "'; try help");
            }
        }

        private object BridgeHelp()
        {
            var read = new[] { "ping", "help", "state", "mod.state", "player", "vehicle", "drive.state", "shapecasts", "scanner", "input.controls", "input.hold", "input.release", "input.state", "screenshot", "log.tail", "log.write", "speech.sapi", "speech.status" };
            return new
            {
                commands = read.Concat(HARNESS_COMMANDS).ToArray(),
                read_only = read,
                harness = HARNESS_COMMANDS,
                world_writes_allowed = bridgeAllowWorldWrites,
                safety = "Loopback only. GTA state commands run on the script tick. Input holds expire after at most 30 seconds. "
                    + "The harness commands change the game world and additionally require \"allow_world_writes\": true in scripts/gta11y-bridge.json. "
                    + "There is no arbitrary-native command by design."
            };
        }

        private object BridgeState()
        {
            return new { mod = BridgeModState(), player = BridgePlayer(), vehicle = BridgeVehicle(), drive = BridgeDriveState(), shapecasts = BridgeShapecasts() };
        }

        private object BridgeModState()
        {
            return new
            {
                iteration = BridgeIteration,
                bridge_port = diagnosticBridge?.Port ?? 0,
                drive_log_running = driveLogger != null && driveLogger.IsRunning,
                drive_log_path = driveLogger?.FilePath,
                drive_frame = driveLogFrameCount,
                snapshot_running = snapshotWriter != null && snapshotWriter.IsRunning,
                snapshot_status = snapshotWriter?.SelfTestStatus,
                game_loading = Game.IsLoading,
                paused = Game.IsPaused
            };
        }

        private object BridgePlayer()
        {
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists()) throw new InvalidOperationException("player ped is unavailable; the game may be loading");
            return new
            {
                handle = ped.Handle,
                model = ped.Model.Hash,
                position = Vec(ped.Position),
                heading = ped.Heading,
                velocity = Vec(ped.Velocity),
                health = ped.Health,
                armor = ped.Armor,
                wanted_level = Game.Player.WantedLevel,
                alive = ped.IsAlive,
                in_vehicle = ped.IsInVehicle(),
                street = World.GetStreetName(ped.Position),
                zone = Function.Call<string>(Hash.GET_NAME_OF_ZONE, ped.Position.X, ped.Position.Y, ped.Position.Z)
            };
        }

        private object BridgeVehicle()
        {
            Ped ped = Game.Player.Character;
            Vehicle vehicle = ped != null && ped.Exists() && ped.IsInVehicle() ? ped.CurrentVehicle : null;
            if (vehicle == null || !vehicle.Exists()) return null;
            return new
            {
                handle = vehicle.Handle,
                model = vehicle.Model.Hash,
                name = vehicle.LocalizedName,
                position = Vec(vehicle.Position),
                heading = vehicle.Heading,
                rotation = Vec(vehicle.Rotation),
                velocity = Vec(vehicle.Velocity),
                speed_mps = vehicle.Speed,
                forward_speed_mps = GTA.Math.Vector3.Dot(vehicle.Velocity, vehicle.ForwardVector),
                body_health = vehicle.BodyHealth,
                engine_health = vehicle.EngineHealth,
                petrol_tank_health = vehicle.PetrolTankHealth,
                dirt = vehicle.DirtLevel,
                on_fire = vehicle.IsOnFire,
                driveable = vehicle.IsDriveable,
                upside_down = vehicle.IsUpsideDown,
                on_all_wheels = vehicle.IsOnAllWheels
            };
        }

        private object BridgeDriveState()
        {
            return new
            {
                steering_assist_setting = getSetting("steeringAssist"),
                steering_active = steeringAssistActive,
                mode = currentDriveMode.ToString(),
                mode_reason = driveModeWhy,
                cached_steer = cachedSteerCorrection,
                cached_brake = cachedBrakeMagnitude,
                player_throttle = lastPlayerThrottleRaw,
                player_steer = Math.Max(-1f, Math.Min(1f,
                    Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, 59))),
                live_steer = lastLiveSteer,
                last_steer_written = lastSteerWritten,
                recovery_distance = recoveryTargetDistance,
                recovery_reason = recoveryDistWhy,
                autodriving = isAutodriving,
                controls = BridgeInputState()
            };
        }

        private object BridgeShapecasts()
        {
            return new
            {
                left = new { distance = BridgeDistance(navAssistDistLeft), type = navAssistTypeLeft },
                center = new { distance = BridgeDistance(navAssistDistCenter), type = navAssistTypeCenter },
                right = new { distance = BridgeDistance(navAssistDistRight), type = navAssistTypeRight },
                behind = new { distance = BridgeDistance(navAssistDistBehind), type = navAssistTypeBehind },
                note = "null distance means no usable cast result; refused and clear must be distinguished by the type field"
            };
        }

        private static float? BridgeDistance(float value) { return value >= 900f ? (float?)null : value; }

        private object BridgeScanner(JObject args)
        {
            string kind = (args.Value<string>("kind") ?? "vehicles").ToLowerInvariant();
            float radius = Math.Max(1f, Math.Min(500f, args.Value<float?>("radius") ?? 75f));
            int cap = Math.Max(1, Math.Min(50, args.Value<int?>("max_results") ?? 12));
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) throw new InvalidOperationException("player ped unavailable");
            var rows = new List<object>();
            IEnumerable<Entity> entities;
            if (kind == "vehicles") entities = World.GetNearbyVehicles(player.Position, radius).Cast<Entity>();
            else if (kind == "people" || kind == "peds") entities = World.GetNearbyPeds(player.Position, radius).Where(p => p.Handle != player.Handle).Cast<Entity>();
            else if (kind == "props" || kind == "objects") entities = World.GetNearbyProps(player.Position, radius).Cast<Entity>();
            else throw new ArgumentException("kind must be vehicles, people, or objects");
            foreach (Entity entity in entities.Where(e => e != null && e.Exists()).OrderBy(e => e.Position.DistanceTo(player.Position)).Take(cap))
            {
                float distance = entity.Position.DistanceTo(player.Position);
                rows.Add(new { handle = entity.Handle, model = entity.Model.Hash, type = entity.EntityType.ToString(), distance, position = Vec(entity.Position), relative_heading = RelativeHeading(player, entity.Position), visible = entity.IsVisible, on_screen = entity.IsOnScreen });
            }
            return new { kind, radius, count = rows.Count, results = rows };
        }

        private object BridgeInputControls()
        {
            return new Dictionary<string, int> { ["steer"] = 59, ["accelerate"] = 71, ["brake"] = 72, ["handbrake"] = 76, ["move_left_right"] = 30, ["move_up_down"] = 31, ["attack"] = 24, ["aim"] = 25, ["enter"] = 23, ["jump"] = 22 };
        }

        private int ControlNumber(string name)
        {
            var controls = (Dictionary<string, int>)BridgeInputControls();
            int value;
            if (!controls.TryGetValue((name ?? "").ToLowerInvariant(), out value)) throw new ArgumentException("unknown control; call input.controls");
            return value;
        }

        private object BridgeInputHold(JObject args)
        {
            int control = ControlNumber(args.Value<string>("control"));
            float value = Math.Max(-1f, Math.Min(1f, args.Value<float?>("value") ?? 1f));
            int ms = Math.Max(1, Math.Min(30000, args.Value<int?>("ms") ?? 250));
            bridgeHeldControls[control] = new BridgeHeldControl { Value = value, UntilUtcTicks = DateTime.UtcNow.AddMilliseconds(ms).Ticks };
            WriteControlValue(control, value);
            return new { control, value, ms, readback = BridgeInputState() };
        }

        private object BridgeInputRelease(JObject args)
        {
            string name = args.Value<string>("control");
            if (string.IsNullOrWhiteSpace(name)) bridgeHeldControls.Clear();
            else bridgeHeldControls.Remove(ControlNumber(name));
            return BridgeInputState();
        }

        private object BridgeInputState()
        {
            long now = DateTime.UtcNow.Ticks;
            return new
            {
                held = bridgeHeldControls.Select(p => new { control = p.Key, value = p.Value.Value, remaining_ms = Math.Max(0, (p.Value.UntilUtcTicks - now) / TimeSpan.TicksPerMillisecond) }).ToArray(),
                achieved = new
                {
                    valid = achValid,
                    throttle = achValid ? (float?)achThrottle : null,
                    brake = achValid ? (float?)achBrake : null,
                    steer_normalized = achValid ? (float?)achSteerNorm : null,
                    steer_degrees = achValid ? (float?)achSteerDeg : null
                }
            };
        }

        private object BridgeScreenshot(JObject args)
        {
            string requested = args.Value<string>("name") ?? "live";
            string safe = new string(requested.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
            if (safe.Length == 0) safe = "live";
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rockstar Games", "GTA V", "ModSettings", "bridge-screenshots");
            Directory.CreateDirectory(dir);
            string source;
            Rectangle rect = SnapshotWriter.ResolveCaptureRect(out source);
            string path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd-HHmmss-fff") + "-" + (++bridgeScreenshotSequence) + "-" + safe + ".jpg");
            using (var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
                bitmap.Save(path, ImageFormat.Jpeg);
            }
            string why;
            return new { path, source, width = rect.Width, height = rect.Height, degraded = SnapshotWriter.IsCaptureDegraded(out why), warning = why };
        }

        private object BridgeLogTail(JObject args)
        {
            if (driveLogger == null || !driveLogger.IsRunning || string.IsNullOrEmpty(driveLogger.FilePath)) throw new InvalidOperationException("drive-assist logging is not running");
            int count = Math.Max(1, Math.Min(400, args.Value<int?>("count") ?? 40));
            string[] lines;
            using (var stream = new FileStream(driveLogger.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream)) lines = ReadTail(reader, count);
            return new { path = driveLogger.FilePath, lines };
        }

        private object BridgeLogWrite(JObject args)
        {
            if (driveLogger == null || !driveLogger.IsRunning) throw new InvalidOperationException("drive-assist logging is not running");
            string text = args.Value<string>("text");
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required");
            if (text.Length > 1000) throw new ArgumentException("text is limited to 1000 characters");
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            string source = args.Value<string>("source") ?? "agent";
            source = new string(source.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
            if (source.Length == 0) source = "agent";
            // iter-71: an optional failure tag. Without one a bridge marker is a
            // comment; with one it goes through the same emitter as a player's F1
            // flag, so the scenario's own moments of interest get pre-roll frames and
            // a contact sheet instead of being invisible to every triage artifact.
            string tag = args.Value<string>("tag");
            if (!string.IsNullOrWhiteSpace(tag))
            {
                tag = new string(tag.ToUpperInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
                if (tag.Length == 0) tag = null;
            }

            string line = "[F" + driveLogFrameCount + "] EVENT bridge-marker: source=" + source
                + (tag != null ? " tag=" + tag : "")
                + " text=\"" + text.Replace("\"", "'") + "\"";
            driveLogger.Write(line);
            if (tag != null) SnapshotFailure(tag, "bridge source=" + source);
            return new { written = true, frame = driveLogFrameCount, tag, snapshot = tag != null, line };
        }

        private object BridgeSapiSpeak(JObject args)
        {
            if (bridgeSapi == null) throw new InvalidOperationException("SAPI bridge speaker is unavailable");
            string text = args.Value<string>("text");
            bridgeSapi.Speak(text);
            return new { queued = true, status = bridgeSapi.Status };
        }

        private static string[] ReadTail(StreamReader reader, int count)
        {
            var queue = new Queue<string>(count);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (queue.Count == count) queue.Dequeue();
                queue.Enqueue(line);
            }
            return queue.ToArray();
        }

        private static object Vec(GTA.Math.Vector3 value) { return new { x = value.X, y = value.Y, z = value.Z }; }
        private static float RelativeHeading(Entity origin, GTA.Math.Vector3 target)
        {
            GTA.Math.Vector3 delta = target - origin.Position;
            double bearing = Math.Atan2(delta.Y, delta.X) * 180.0 / Math.PI - 90.0;
            double relative = bearing - origin.Heading;
            while (relative > 180) relative -= 360;
            while (relative < -180) relative += 360;
            return (float)relative;
        }
    }
}
