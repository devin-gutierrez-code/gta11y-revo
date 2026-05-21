using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using DavyKager; // Tolk wrapper

namespace GTA11Y.MenuHelper
{
    /// <summary>
    /// GTA11Y Menu Helper - Standalone application that monitors GTA V's pause menu
    /// and phone UI, reading them aloud via screen reader.
    ///
    /// This runs as a separate process to avoid the ScriptHookVDotNet pause issue.
    /// Requires Tolk.dll to be in the GTA V main folder.
    /// </summary>
    public class MenuHelperForm : Form
    {
        // Win32 imports
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        // Virtual key codes
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;
        private const int VK_LEFT = 0x25;
        private const int VK_RIGHT = 0x27;
        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_BACK = 0x08;
        private const int VK_TAB = 0x09;
        private const int VK_F12 = 0x7B;

        // State
        private IntPtr keyboardHook = IntPtr.Zero;
        private LowLevelKeyboardProc keyboardProcDelegate;
        private OcrEngine ocrEngine;
        private bool ocrInProgress = false;
        private string lastOcrText = "";
        private DateTime lastOcrTime = DateTime.MinValue;
        private bool gtaWasFocused = false;
        private NotifyIcon trayIcon;
        private StreamWriter logWriter;

        // GTA V window detection
        private const string GTA_WINDOW_TITLE = "Grand Theft Auto V";
        private const string GTA_WINDOW_CLASS = "grcWindow";

        // ============================================
        // SHARED MEMORY FOR GAME STATE
        // Reads state written by main GTA11Y mod
        // ============================================
        private const string SHARED_MEMORY_NAME = "GTA11Y_GameState";
        private const int SHARED_MEMORY_SIZE = 256;
        private MemoryMappedFile sharedMemory = null;
        private MemoryMappedViewAccessor sharedMemoryAccessor = null;

        // Game state from shared memory
        private bool lastPauseMenuActive = false;
        private bool lastPhoneVisible = false;
        private int lastMenuState = -1;
        private int lastMenuSelection = -1;
        private int lastStateTimestamp = 0;

        public MenuHelperForm()
        {
            // Initialize as a hidden form with system tray icon
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Size = new Size(1, 1);
            this.Opacity = 0;

            InitializeLogging();
            Log("=== GTA11Y Menu Helper Started ===");

            // Initialize Tolk
            try
            {
                Tolk.Load();
                if (Tolk.IsLoaded())
                {
                    Log("Tolk loaded successfully");
                    Speak("Menu helper ready. Press F12 in GTA 5 to read screen.");
                }
                else
                {
                    Log("ERROR: Tolk failed to load");
                }
            }
            catch (Exception ex)
            {
                Log("ERROR loading Tolk: " + ex.Message);
            }

            // Initialize OCR
            InitializeOcr();

            // Setup system tray icon
            SetupTrayIcon();

            // Install keyboard hook
            InstallKeyboardHook();

            // Start a timer to monitor game state from shared memory
            var stateTimer = new Timer();
            stateTimer.Interval = 100; // Check 10 times per second
            stateTimer.Tick += CheckGameState;
            stateTimer.Start();
        }

        /// <summary>
        /// Tries to open the shared memory created by the main GTA11Y mod.
        /// </summary>
        private bool TryOpenSharedMemory()
        {
            if (sharedMemoryAccessor != null) return true;

            try
            {
                sharedMemory = MemoryMappedFile.OpenExisting(SHARED_MEMORY_NAME);
                sharedMemoryAccessor = sharedMemory.CreateViewAccessor(0, SHARED_MEMORY_SIZE, MemoryMappedFileAccess.Read);
                Log("Connected to shared memory: " + SHARED_MEMORY_NAME);
                return true;
            }
            catch (FileNotFoundException)
            {
                // Shared memory not created yet - GTA11Y mod not running
                return false;
            }
            catch (Exception ex)
            {
                Log($"Shared memory error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads game state from shared memory.
        /// </summary>
        private (bool pauseMenuActive, int menuState, bool phoneVisible, int menuSelection, int timestamp) ReadGameState()
        {
            if (sharedMemoryAccessor == null)
            {
                return (false, 0, false, 0, 0);
            }

            try
            {
                byte pauseMenu = sharedMemoryAccessor.ReadByte(0);
                byte menuState = sharedMemoryAccessor.ReadByte(1);
                byte phone = sharedMemoryAccessor.ReadByte(2);
                byte selection = sharedMemoryAccessor.ReadByte(3);
                int timestamp = sharedMemoryAccessor.ReadInt32(4);

                return (pauseMenu == 1, menuState, phone == 1, selection, timestamp);
            }
            catch
            {
                return (false, 0, false, 0, 0);
            }
        }

        /// <summary>
        /// Timer callback to check game state from shared memory.
        /// </summary>
        private void CheckGameState(object sender, EventArgs e)
        {
            // Try to connect to shared memory if not connected
            if (!TryOpenSharedMemory()) return;

            var (pauseMenuActive, menuState, phoneVisible, menuSelection, timestamp) = ReadGameState();

            // Check if state is stale (game might have closed)
            int now = Environment.TickCount;
            if (Math.Abs(now - timestamp) > 2000)
            {
                // State is more than 2 seconds old - game might not be running
                // Reset our state tracking
                if (lastPauseMenuActive || lastPhoneVisible)
                {
                    lastPauseMenuActive = false;
                    lastPhoneVisible = false;
                    lastMenuState = -1;
                    lastMenuSelection = -1;
                }
                return;
            }

            // Pause menu state changes
            if (pauseMenuActive && !lastPauseMenuActive)
            {
                // Menu just opened - reset navigation tracking
                Log($"Pause menu opened (state={menuState})");
                Speak(GetMenuTabName(menuState));
                lastPauseMenuActive = true;
                lastMenuState = menuState;
                lastMenuSelection = menuSelection;
                lastOcrText = "";
                currentMenuItemIndex = 0;  // Reset navigation
                totalMenuItems = 0;

                // Trigger OCR after brief delay
                Task.Run(async () =>
                {
                    await Task.Delay(300);
                    await TriggerOcrAsync();
                });
            }
            else if (!pauseMenuActive && lastPauseMenuActive)
            {
                // Menu just closed
                Log("Pause menu closed");
                Speak("Menu closed");
                lastPauseMenuActive = false;
                lastMenuState = -1;
                lastMenuSelection = -1;
                lastOcrText = "";
                currentMenuItemIndex = 0;
                totalMenuItems = 0;
            }
            else if (pauseMenuActive)
            {
                // Menu still open - check for tab or selection changes
                if (menuState != lastMenuState)
                {
                    Log($"Menu tab changed: {lastMenuState} -> {menuState}");
                    Speak(GetMenuTabName(menuState));
                    lastMenuState = menuState;
                    lastOcrText = "";
                    currentMenuItemIndex = 0;  // Reset navigation on tab change
                    totalMenuItems = 0;

                    Task.Run(async () =>
                    {
                        await Task.Delay(200);
                        await TriggerOcrAsync();
                    });
                }
                else if (menuSelection != lastMenuSelection)
                {
                    Log($"Menu selection changed: {lastMenuSelection} -> {menuSelection}");
                    lastMenuSelection = menuSelection;

                    // Trigger OCR for new selection
                    Task.Run(async () =>
                    {
                        await Task.Delay(150);
                        await TriggerOcrAsync();
                    });
                }
            }

            // Phone state changes
            if (phoneVisible && !lastPhoneVisible)
            {
                Log("Phone opened");
                Speak("Phone");
                lastPhoneVisible = true;
                lastOcrText = "";
                currentMenuItemIndex = 0;  // Reset navigation
                totalMenuItems = 0;

                Task.Run(async () =>
                {
                    await Task.Delay(400);
                    await TriggerPhoneOcrAsync();
                });
            }
            else if (!phoneVisible && lastPhoneVisible)
            {
                Log("Phone closed");
                Speak("Phone closed");
                lastPhoneVisible = false;
                lastOcrText = "";
                currentMenuItemIndex = 0;
                totalMenuItems = 0;
            }

            lastStateTimestamp = timestamp;
        }

        /// <summary>
        /// Gets the name of a pause menu tab by state index.
        /// </summary>
        private string GetMenuTabName(int state)
        {
            switch (state)
            {
                case 0: return "Map";
                case 1: return "Brief";
                case 2: return "Stats";
                case 3: return "Settings";
                case 4: return "Game";
                case 5: return "Gallery";
                case 6: return "Info";
                case 7: return "Store";
                case 8: return "Social Club";
                case 9: return "Friends";
                case 10: return "Crews";
                default: return $"Menu {state}";
            }
        }

        /// <summary>
        /// Triggers OCR specifically for phone UI region.
        /// </summary>
        private async Task TriggerPhoneOcrAsync()
        {
            if (ocrInProgress) return;

            ocrInProgress = true;
            lastOcrTime = DateTime.Now;

            try
            {
                IntPtr hwnd = FindGtaWindow();
                if (hwnd == IntPtr.Zero) return;

                using (Bitmap screenshot = CapturePhoneRegion(hwnd))
                {
                    if (screenshot == null) return;

                    string text = await PerformOcr(screenshot);
                    if (!string.IsNullOrWhiteSpace(text) && text != lastOcrText)
                    {
                        Log($"Phone OCR: {text}");
                        Speak(text);
                        lastOcrText = text;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Phone OCR error: {ex.Message}");
            }
            finally
            {
                ocrInProgress = false;
            }
        }

        /// <summary>
        /// Captures the phone region of the screen.
        /// </summary>
        private Bitmap CapturePhoneRegion(IntPtr hwnd)
        {
            try
            {
                RECT rect;
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf(typeof(RECT))) != 0)
                {
                    GetWindowRect(hwnd, out rect);
                }

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width < 100 || height < 100) return null;

                Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    IntPtr hdc = g.GetHdc();
                    bool success = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                    g.ReleaseHdc(hdc);

                    if (!success)
                    {
                        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
                    }
                }

                // Phone region: center-right area
                int regionLeft = (int)(width * 0.35);
                int regionTop = (int)(height * 0.15);
                int regionWidth = (int)(width * 0.40);
                int regionHeight = (int)(height * 0.70);

                Bitmap region = new Bitmap(regionWidth, regionHeight, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(region))
                {
                    g.DrawImage(bitmap,
                        new Rectangle(0, 0, regionWidth, regionHeight),
                        new Rectangle(regionLeft, regionTop, regionWidth, regionHeight),
                        GraphicsUnit.Pixel);
                }
                bitmap.Dispose();

                return region;
            }
            catch
            {
                return null;
            }
        }

        private void InitializeLogging()
        {
            try
            {
                string logPath = Path.Combine(
                    Path.GetDirectoryName(Application.ExecutablePath),
                    "MenuHelper_log.txt");
                logWriter = new StreamWriter(logPath, append: true);
                logWriter.AutoFlush = true;
            }
            catch { }
        }

        private void Log(string message)
        {
            try
            {
                string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
                logWriter?.WriteLine(line);
                System.Diagnostics.Debug.WriteLine(line);
            }
            catch { }
        }

        private void Speak(string text, bool interrupt = true)
        {
            if (!string.IsNullOrEmpty(text))
            {
                Log($"Speaking: {text}");
                try
                {
                    Tolk.Speak(text, interrupt);
                }
                catch (Exception ex)
                {
                    Log($"Speak error: {ex.Message}");
                }
            }
        }

        private void InitializeOcr()
        {
            try
            {
                var language = new Windows.Globalization.Language("en-US");
                if (OcrEngine.IsLanguageSupported(language))
                {
                    ocrEngine = OcrEngine.TryCreateFromLanguage(language);
                    Log("OCR engine initialized for English");
                }
                else
                {
                    ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                    Log("OCR engine initialized from user profile");
                }
            }
            catch (Exception ex)
            {
                Log($"OCR init error: {ex.Message}");
            }
        }

        private void SetupTrayIcon()
        {
            trayIcon = new NotifyIcon();
            trayIcon.Icon = SystemIcons.Application;
            trayIcon.Text = "GTA11Y Menu Helper";
            trayIcon.Visible = true;

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Read Screen Now (F12)", null, (s, e) => TriggerOcr());
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());
            trayIcon.ContextMenuStrip = contextMenu;

            trayIcon.DoubleClick += (s, e) => TriggerOcr();
        }

        private void InstallKeyboardHook()
        {
            keyboardProcDelegate = KeyboardHookCallback;

            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            using (var module = process.MainModule)
            {
                keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProcDelegate,
                    GetModuleHandle(module.ModuleName), 0);
            }

            if (keyboardHook != IntPtr.Zero)
            {
                Log("Keyboard hook installed");
            }
            else
            {
                Log($"Failed to install keyboard hook: {Marshal.GetLastWin32Error()}");
            }
        }

        private void UninstallKeyboardHook()
        {
            if (keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(keyboardHook);
                keyboardHook = IntPtr.Zero;
                Log("Keyboard hook uninstalled");
            }
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);

                // Only respond if GTA V is the foreground window
                if (IsGtaFocused())
                {
                    // F12 = Manual OCR trigger (always works)
                    if (vkCode == VK_F12)
                    {
                        Log("F12 pressed - triggering manual OCR");
                        Task.Run(() => TriggerOcr());
                    }
                    // Track menu navigation when pause menu or phone is active
                    else if (lastPauseMenuActive || lastPhoneVisible)
                    {
                        bool navigationChanged = false;
                        string navDirection = "";

                        if (vkCode == VK_UP)
                        {
                            currentMenuItemIndex--;
                            if (currentMenuItemIndex < 0) currentMenuItemIndex = Math.Max(0, totalMenuItems - 1);
                            navigationChanged = true;
                            navDirection = "up";
                        }
                        else if (vkCode == VK_DOWN)
                        {
                            currentMenuItemIndex++;
                            if (totalMenuItems > 0 && currentMenuItemIndex >= totalMenuItems) currentMenuItemIndex = 0;
                            navigationChanged = true;
                            navDirection = "down";
                        }
                        else if (vkCode == VK_RETURN)
                        {
                            // Selection made - trigger OCR to announce current item
                            Log("Enter pressed - announcing selection");
                            Task.Run(() => TriggerOcr());
                        }
                        else if (vkCode == VK_ESCAPE || vkCode == VK_BACK)
                        {
                            // Going back - reset navigation index
                            currentMenuItemIndex = 0;
                            Log("Back/Escape - resetting menu index");
                        }
                        else if (vkCode == VK_LEFT || vkCode == VK_RIGHT)
                        {
                            // Tab change - reset navigation and trigger OCR
                            currentMenuItemIndex = 0;
                            navigationChanged = true;
                            navDirection = vkCode == VK_LEFT ? "left" : "right";
                        }

                        if (navigationChanged)
                        {
                            Log($"Navigation: {navDirection}, item index now {currentMenuItemIndex}");
                            // Trigger OCR after brief delay to let UI update
                            Task.Run(async () =>
                            {
                                await Task.Delay(100);
                                await TriggerOcrAsync();
                            });
                        }
                    }
                }
            }

            return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
        }

        private bool IsGtaFocused()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            var title = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, title, 256);

            return title.ToString().Contains("Grand Theft Auto");
        }

        private IntPtr FindGtaWindow()
        {
            // Try to find GTA V window
            IntPtr hwnd = FindWindow(GTA_WINDOW_CLASS, GTA_WINDOW_TITLE);
            if (hwnd == IntPtr.Zero)
            {
                // Fall back to foreground if it's GTA
                hwnd = GetForegroundWindow();
                var title = new System.Text.StringBuilder(256);
                GetWindowText(hwnd, title, 256);
                if (!title.ToString().Contains("Grand Theft Auto"))
                {
                    return IntPtr.Zero;
                }
            }
            return hwnd;
        }

        private void TriggerOcr()
        {
            Task.Run(() => TriggerOcrAsync());
        }

        private async Task TriggerOcrAsync()
        {
            if (ocrInProgress)
            {
                return;
            }

            ocrInProgress = true;
            lastOcrTime = DateTime.Now;

            try
            {
                IntPtr hwnd = FindGtaWindow();
                if (hwnd == IntPtr.Zero)
                {
                    Log("GTA window not found");
                    Speak("Game not found");
                    return;
                }

                // Capture the screen
                using (Bitmap screenshot = CaptureWindow(hwnd))
                {
                    if (screenshot == null)
                    {
                        Log("Screenshot failed");
                        Speak("Capture failed");
                        return;
                    }

                    // Perform OCR
                    string text = await PerformOcr(screenshot);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Only speak if text changed
                        if (text != lastOcrText)
                        {
                            Log($"OCR result: {text}");
                            Speak(text);
                            lastOcrText = text;
                        }
                        else
                        {
                            Log("OCR result unchanged");
                        }
                    }
                    else
                    {
                        Log("No text detected");
                        // Don't speak "no text" on navigation - too noisy
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"OCR error: {ex.Message}");
            }
            finally
            {
                ocrInProgress = false;
            }
        }

        private Bitmap CaptureWindow(IntPtr hwnd)
        {
            try
            {
                // Get window bounds
                RECT rect;
                int result = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                    out rect, Marshal.SizeOf(typeof(RECT)));

                if (result != 0)
                {
                    GetWindowRect(hwnd, out rect);
                }

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                if (width < 100 || height < 100)
                {
                    Log($"Window too small: {width}x{height}");
                    return null;
                }

                // Capture using PrintWindow with DirectX content flag
                Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    IntPtr hdc = g.GetHdc();
                    bool success = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                    g.ReleaseHdc(hdc);

                    if (!success)
                    {
                        Log("PrintWindow failed, trying CopyFromScreen");
                        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
                    }
                }

                // Extract center region where menu text typically appears
                // Pause menu is usually in center-left area
                int regionLeft = (int)(width * 0.05);
                int regionTop = (int)(height * 0.10);
                int regionWidth = (int)(width * 0.50);
                int regionHeight = (int)(height * 0.80);

                Bitmap region = new Bitmap(regionWidth, regionHeight, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(region))
                {
                    g.DrawImage(bitmap,
                        new Rectangle(0, 0, regionWidth, regionHeight),
                        new Rectangle(regionLeft, regionTop, regionWidth, regionHeight),
                        GraphicsUnit.Pixel);
                }
                bitmap.Dispose();

                return region;
            }
            catch (Exception ex)
            {
                Log($"Capture error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Represents an OCR line with its bounding box and brightness analysis.
        /// </summary>
        private class OcrLineInfo
        {
            public string Text { get; set; }
            public int Y { get; set; }  // Vertical position
            public int Height { get; set; }
            public int X { get; set; }
            public int Width { get; set; }
            public float AverageBrightness { get; set; }
            public bool IsHighlighted { get; set; }
        }

        /// <summary>
        /// Performs OCR and attempts to identify the highlighted/selected menu item
        /// by analyzing brightness differences between lines.
        /// </summary>
        private async Task<string> PerformOcr(Bitmap bitmap)
        {
            if (ocrEngine == null)
            {
                Log("OCR engine not available");
                return null;
            }

            try
            {
                // Keep the bitmap reference for brightness analysis
                Bitmap bitmapCopy = (Bitmap)bitmap.Clone();

                // Convert to SoftwareBitmap
                using (var stream = new System.IO.MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Bmp);
                    stream.Position = 0;

                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                        stream.AsRandomAccessStream());
                    var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                        Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

                    // Perform OCR
                    var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);

                    if (ocrResult != null && ocrResult.Lines.Count > 0)
                    {
                        // Collect all valid lines with their bounding boxes
                        var lineInfos = new System.Collections.Generic.List<OcrLineInfo>();

                        foreach (var line in ocrResult.Lines)
                        {
                            string text = line.Text.Trim();
                            if (text.Length >= 2 && !IsNoise(text))
                            {
                                // Get bounding box from the line's words
                                int minX = int.MaxValue, minY = int.MaxValue;
                                int maxX = 0, maxY = 0;

                                foreach (var word in line.Words)
                                {
                                    var rect = word.BoundingRect;
                                    if (rect.X < minX) minX = (int)rect.X;
                                    if (rect.Y < minY) minY = (int)rect.Y;
                                    if (rect.X + rect.Width > maxX) maxX = (int)(rect.X + rect.Width);
                                    if (rect.Y + rect.Height > maxY) maxY = (int)(rect.Y + rect.Height);
                                }

                                var info = new OcrLineInfo
                                {
                                    Text = text,
                                    X = minX,
                                    Y = minY,
                                    Width = maxX - minX,
                                    Height = maxY - minY
                                };

                                // Calculate average brightness of this line's region
                                info.AverageBrightness = CalculateRegionBrightness(bitmapCopy, info.X, info.Y, info.Width, info.Height);
                                lineInfos.Add(info);
                            }
                        }

                        bitmapCopy.Dispose();

                        if (lineInfos.Count > 0)
                        {
                            // Try to find the highlighted item based on brightness
                            string result = FindHighlightedItem(lineInfos);
                            if (!string.IsNullOrEmpty(result))
                            {
                                return result;
                            }

                            // Fallback: return all lines if we can't determine highlight
                            int maxLines = Math.Min(5, lineInfos.Count);
                            return string.Join(", ", lineInfos.GetRange(0, maxLines).Select(l => l.Text));
                        }
                    }
                    else
                    {
                        bitmapCopy.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"OCR processing error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Calculates the average brightness of a region in the bitmap.
        /// </summary>
        private float CalculateRegionBrightness(Bitmap bitmap, int x, int y, int width, int height)
        {
            try
            {
                // Clamp to bitmap bounds
                x = Math.Max(0, Math.Min(x, bitmap.Width - 1));
                y = Math.Max(0, Math.Min(y, bitmap.Height - 1));
                width = Math.Min(width, bitmap.Width - x);
                height = Math.Min(height, bitmap.Height - y);

                if (width <= 0 || height <= 0) return 0;

                // Sample pixels to calculate average brightness
                long totalBrightness = 0;
                int sampleCount = 0;
                int stepX = Math.Max(1, width / 20);  // Sample up to 20 points across
                int stepY = Math.Max(1, height / 5);  // Sample up to 5 points vertically

                for (int py = y; py < y + height && py < bitmap.Height; py += stepY)
                {
                    for (int px = x; px < x + width && px < bitmap.Width; px += stepX)
                    {
                        Color c = bitmap.GetPixel(px, py);
                        // Use perceived brightness formula
                        int brightness = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                        totalBrightness += brightness;
                        sampleCount++;
                    }
                }

                return sampleCount > 0 ? totalBrightness / (float)sampleCount : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Finds the highlighted menu item by analyzing brightness differences.
        /// GTA V menu items typically have distinct brightness when selected.
        /// </summary>
        private string FindHighlightedItem(System.Collections.Generic.List<OcrLineInfo> lines)
        {
            if (lines.Count < 2)
            {
                // Only one line, return it
                return lines.Count == 1 ? lines[0].Text : null;
            }

            // Calculate statistics
            float avgBrightness = lines.Average(l => l.AverageBrightness);
            float maxBrightness = lines.Max(l => l.AverageBrightness);
            float minBrightness = lines.Min(l => l.AverageBrightness);
            float brightnessRange = maxBrightness - minBrightness;

            Log($"Brightness analysis: avg={avgBrightness:F1}, min={minBrightness:F1}, max={maxBrightness:F1}, range={brightnessRange:F1}");

            // If there's significant brightness variation, the highlighted item is likely different
            if (brightnessRange > 20) // Threshold for meaningful difference
            {
                // Find items that stand out from the average
                var candidates = new System.Collections.Generic.List<OcrLineInfo>();

                foreach (var line in lines)
                {
                    float deviation = Math.Abs(line.AverageBrightness - avgBrightness);
                    // Item is highlighted if it's significantly brighter or darker than average
                    if (deviation > brightnessRange * 0.3f)
                    {
                        line.IsHighlighted = true;
                        candidates.Add(line);
                        Log($"  Candidate: '{line.Text}' brightness={line.AverageBrightness:F1}, deviation={deviation:F1}");
                    }
                }

                // If we found exactly one standout item, that's our selection
                if (candidates.Count == 1)
                {
                    Log($"Found highlighted item: {candidates[0].Text}");
                    return candidates[0].Text;
                }

                // If multiple candidates, pick the brightest one (GTA typically highlights with lighter color)
                if (candidates.Count > 1)
                {
                    var brightest = candidates.OrderByDescending(c => c.AverageBrightness).First();
                    Log($"Multiple candidates, picking brightest: {brightest.Text}");
                    return brightest.Text;
                }
            }

            // If no clear highlight found, use navigation tracking
            return FindItemByNavigation(lines);
        }

        // Track navigation state
        private int currentMenuItemIndex = 0;
        private int totalMenuItems = 0;

        /// <summary>
        /// Uses keyboard navigation tracking to determine current selection.
        /// </summary>
        private string FindItemByNavigation(System.Collections.Generic.List<OcrLineInfo> lines)
        {
            totalMenuItems = lines.Count;

            // Clamp index to valid range
            if (currentMenuItemIndex < 0) currentMenuItemIndex = 0;
            if (currentMenuItemIndex >= lines.Count) currentMenuItemIndex = lines.Count - 1;

            // Sort lines by vertical position (top to bottom)
            var sortedLines = lines.OrderBy(l => l.Y).ToList();

            if (currentMenuItemIndex < sortedLines.Count)
            {
                Log($"Navigation tracking: item {currentMenuItemIndex + 1}/{totalMenuItems}: {sortedLines[currentMenuItemIndex].Text}");
                return sortedLines[currentMenuItemIndex].Text;
            }

            // Fallback to first line
            return sortedLines.Count > 0 ? sortedLines[0].Text : null;
        }

        private bool IsNoise(string text)
        {
            // Filter out common OCR noise
            if (text.Length < 2) return true;
            if (text.All(c => !char.IsLetterOrDigit(c))) return true;

            // GTA-specific noise patterns
            string[] noisePatterns = { "|||", "...", "___", "---" };
            foreach (var pattern in noisePatterns)
            {
                if (text.Contains(pattern)) return true;
            }

            return false;
        }

        private void ExitApplication()
        {
            Log("=== GTA11Y Menu Helper Exiting ===");
            UninstallKeyboardHook();

            try
            {
                Tolk.Unload();
            }
            catch { }

            trayIcon?.Dispose();
            logWriter?.Close();

            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Minimize to tray instead of closing
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                return;
            }

            ExitApplication();
            base.OnFormClosing(e);
        }

        protected override void SetVisibleCore(bool value)
        {
            // Start hidden
            base.SetVisibleCore(false);
        }
    }
}
