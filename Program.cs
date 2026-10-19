using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace TrueStretchSwitcher;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var app = new SwitcherContext();
        Application.Run(app);
    }
}

internal sealed class SwitcherContext : ApplicationContext
{
    private const int HotkeyId = 0x5151;
    private const uint ModNoRepeat = 0x4000;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const int PollMs = 500;

    private readonly HiddenWindow _window;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly string _settingsDir;
    private readonly string _settingsPath;
    private readonly string _logPath;
    private Settings _settings;

    private long _lastLogLength;
    private DateTime _lastLogWriteUtc = DateTime.MinValue;
    private bool _inMatch;
    private bool _awaitingMenuAfterEnd;
    private bool _awaitingNetworkRecovery;
    private bool _valorantProcessWasRunning;
    private bool _exiting;

    public SwitcherContext()
    {
        _settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TrueStretchSwitcher");
        Directory.CreateDirectory(_settingsDir);
        _settingsPath = Path.Combine(_settingsDir, "settings.json");
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VALORANT", "Saved", "Logs", "ShooterGame.log");
        _settings = LoadSettings();

        _window = new HiddenWindow(HandleHotkey);
        if (!RegisterConfiguredHotkey())
        {
            MessageBox.Show($"The saved hotkey ({FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyKey)}) could not be registered. F6 will be used for this session.",
                "sWitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _settings.HotkeyModifiers = 0;
            _settings.HotkeyKey = (int)Keys.F6;
            RegisterConfiguredHotkey();
        }

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "sWitch",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => ToggleResolution();

        // Start at the end of the current log, so old match events do not trigger on app launch.
        try
        {
            if (File.Exists(_logPath))
            {
                var info = new FileInfo(_logPath);
                _lastLogLength = info.Length;
                _lastLogWriteUtc = info.LastWriteTimeUtc;
            }
        }
        catch { }

        _valorantProcessWasRunning = IsValorantGameRunning();

        _pollTimer = new System.Windows.Forms.Timer { Interval = PollMs };
        _pollTimer.Tick += (_, _) =>
        {
            PollLog();
            PollValorantProcess();
        };
        _pollTimer.Start();

        SetStartupRegistration(_settings.StartWithWindows);
        _tray.ShowBalloonTip(2500, "sWitch",
            $"Running. {FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyKey)} toggles resolution; auto-switch watches Valorant match events.",
            ToolTipIcon.Info);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add($"Toggle resolution ({FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyKey)})", null, (_, _) => ToggleResolution());
        menu.Items.Add($"Switch to native resolution ({_settings.NativeWidth}×{_settings.NativeHeight})", null, (_, _) => SetResolution(false));
        menu.Items.Add($"Switch to stretched resolution ({_settings.StretchedWidth}×{_settings.StretchedHeight})", null, (_, _) => SetResolution(true));
        menu.Items.Add("Resolution settings…", null, (_, _) => OpenResolutionSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Valorant log", null, (_, _) => OpenValorantLog());
        menu.Items.Add("Open Valorant log folder", null, (_, _) => OpenValorantLogFolder());
        menu.Items.Add("Copy Valorant log path", null, (_, _) => CopyValorantLogPath());
        menu.Items.Add(new ToolStripSeparator());

        var auto = new ToolStripMenuItem("Auto-switch on match start/end")
        {
            CheckOnClick = true,
            Checked = _settings.AutoSwitch
        };
        auto.CheckedChanged += (_, _) =>
        {
            _settings.AutoSwitch = auto.Checked;
            SaveSettings();
        };
        menu.Items.Add(auto);

        var startup = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = _settings.StartWithWindows
        };
        startup.CheckedChanged += (_, _) =>
        {
            _settings.StartWithWindows = startup.Checked;
            SetStartupRegistration(startup.Checked);
            SaveSettings();
        };
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        return menu;
    }

    private void OpenValorantLog()
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                MessageBox.Show($"Valorant log not found at:\\n{_logPath}", "sWitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = _logPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open Valorant log.\\n{ex.Message}", "sWitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenValorantLogFolder()
    {
        try
        {
            string? folder = Path.GetDirectoryName(_logPath);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                MessageBox.Show($"Valorant log folder not found:\\n{folder}", "sWitch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open Valorant log folder.\\n{ex.Message}", "sWitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CopyValorantLogPath()
    {
        try
        {
            Clipboard.SetText(_logPath);
            _tray.ShowBalloonTip(1800, "sWitch", "Valorant log path copied to clipboard.",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not copy log path.\\n{ex.Message}", "sWitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenResolutionSettings()
    {
        using var dialog = new ResolutionSettingsForm(_settings.NativeWidth, _settings.NativeHeight,
            _settings.StretchedWidth, _settings.StretchedHeight, _settings.HotkeyModifiers, _settings.HotkeyKey);
        if (dialog.ShowDialog() != DialogResult.OK) return;

        _settings.NativeWidth = dialog.NativeMode.Width;
        _settings.NativeHeight = dialog.NativeMode.Height;
        _settings.StretchedWidth = dialog.StretchedMode.Width;
        _settings.StretchedHeight = dialog.StretchedMode.Height;
        uint oldModifiers = _settings.HotkeyModifiers;
        int oldKey = _settings.HotkeyKey;
        _settings.HotkeyModifiers = dialog.HotkeyModifiers;
        _settings.HotkeyKey = dialog.HotkeyKey;
        if (oldModifiers != _settings.HotkeyModifiers || oldKey != _settings.HotkeyKey)
        {
            Native.UnregisterHotKey(_window.Handle, HotkeyId);
            if (!RegisterConfiguredHotkey())
            {
                _settings.HotkeyModifiers = oldModifiers;
                _settings.HotkeyKey = oldKey;
                RegisterConfiguredHotkey();
                MessageBox.Show("That hotkey is already in use or Windows rejected it. The previous shortcut has been restored.",
                    "Hotkey unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        SaveSettings();
        RebuildMenu();
        _tray.ShowBalloonTip(2500, "sWitch settings saved",
            $"Native: {_settings.NativeWidth}×{_settings.NativeHeight}\nStretched: {_settings.StretchedWidth}×{_settings.StretchedHeight}\nHotkey: {FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyKey)}",
            ToolTipIcon.Info);
    }

    private bool RegisterConfiguredHotkey() => Native.RegisterHotKey(
        _window.Handle, HotkeyId, _settings.HotkeyModifiers | ModNoRepeat, (uint)_settings.HotkeyKey);

    private static string FormatHotkey(uint modifiers, int key)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(((Keys)key).ToString());
        return string.Join("+", parts);
    }

    private void RebuildMenu()
    {
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildMenu();
        old?.Dispose();
    }

    private void HandleHotkey()
    {
        if (!_exiting) ToggleResolution();
    }

    private void ToggleResolution()
    {
        var current = DisplayModes.GetCurrent();
        if (current.Width == _settings.StretchedWidth && current.Height == _settings.StretchedHeight)
            SetResolution(false);
        else
            SetResolution(true);
    }

    private void SetResolution(bool stretched)
    {
        int width = stretched ? _settings.StretchedWidth : _settings.NativeWidth;
        int height = stretched ? _settings.StretchedHeight : _settings.NativeHeight;

        if (!DisplayModes.IsModeAvailable(width, height))
        {
            _tray.ShowBalloonTip(4000, "Resolution unavailable",
                $"{width}×{height} is not currently enumerated by Windows. No change was made.",
                ToolTipIcon.Warning);
            return;
        }

        int result = DisplayModes.Change(width, height);
        if (result == 0)
        {
            _tray.Text = stretched
                ? $"sWitch — {_settings.StretchedWidth}×{_settings.StretchedHeight}"
                : $"sWitch — {_settings.NativeWidth}×{_settings.NativeHeight}";
            _tray.ShowBalloonTip(1000, "sWitch",
                stretched
                    ? $"Switched to {_settings.StretchedWidth}×{_settings.StretchedHeight}."
                    : $"Switched to {_settings.NativeWidth}×{_settings.NativeHeight}.",
                ToolTipIcon.Info);
        }
        else
        {
            _tray.ShowBalloonTip(4000, "Resolution change failed",
                $"Windows rejected {width}×{height} (display API code {result}).",
                ToolTipIcon.Error);
        }
    }

    private void PollLog()
    {
        if (_exiting || !_settings.AutoSwitch) return;

        try
        {
            if (!File.Exists(_logPath)) return;
            var info = new FileInfo(_logPath);

            // If the log was truncated/replaced, resume at the beginning of the new file.
            if (info.Length < _lastLogLength || info.LastWriteTimeUtc < _lastLogWriteUtc)
                _lastLogLength = 0;

            if (info.Length <= _lastLogLength)
            {
                _lastLogWriteUtc = info.LastWriteTimeUtc;
                return;
            }

            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_lastLogLength, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string appended = reader.ReadToEnd();
            _lastLogLength = stream.Length;
            _lastLogWriteUtc = info.LastWriteTimeUtc;

            foreach (string line in appended.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                ProcessLogLine(line);

        }
        catch
        {
            // Log can be briefly locked or rotated by the game. Retry on the next timer tick.
        }
    }

    private static bool IsValorantGameRunning()
    {
        try
        {
            return Process.GetProcessesByName("VALORANT-Win64-Shipping").Length > 0;
        }
        catch
        {
            // Do not treat a process-enumeration failure as the game closing.
            return true;
        }
    }

    private void PollValorantProcess()
    {
        bool isRunning = IsValorantGameRunning();

        if (isRunning)
        {
            _valorantProcessWasRunning = true;
            return;
        }

        // Restore native resolution only after the game process has actually been
        // observed running and then disappears (crash, Alt+F4, or normal exit).
        if (!_valorantProcessWasRunning)
            return;

        _valorantProcessWasRunning = false;
        _inMatch = false;
        _awaitingMenuAfterEnd = false;
        _awaitingNetworkRecovery = false;

        if (_settings.AutoSwitch)
            SetResolution(false);
    }

    private void ProcessLogLine(string line)
    {
        if (!_settings.AutoSwitch) return;

        // Match Ended marks the start of the return flow, but do not switch yet.
        // Restore native resolution on the explicit InGame -> TransitionToMainMenu transition.
        // This avoids switching too early while the end-of-match screen is still visible.
        if (line.Contains("LogShooterGameState: Match Ended:", StringComparison.OrdinalIgnoreCase))
        {
            _awaitingMenuAfterEnd = true;
            _awaitingNetworkRecovery = false;
            return;
        }

        // The same transition is the trigger for restoring native resolution after a match.
        if (line.Contains("Transitioning from InGame to TransitionToMainMenu", StringComparison.OrdinalIgnoreCase))
        {
            _awaitingMenuAfterEnd = true;
            _awaitingNetworkRecovery = false;
            if (_inMatch)
            {
                _inMatch = false;
                SetResolution(false);
            }
            return;
        }

        if (line.Contains("Broadcasting state changed to MainMenu", StringComparison.OrdinalIgnoreCase))
        {
            _awaitingMenuAfterEnd = false;
            return;
        }

        // Early warning: Unreal/Valorant reports that queued movement has reached
        // its limit before the authoritative connection timeout (12.5 seconds in the
        // supplied log). While already in a match, fail safe to native resolution.
        // The explicit timeout/network-failure lines are also treated as triggers.
        bool networkDegradation =
            line.Contains("CreateSavedMove: Hit limit of 96 saved moves", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Connection TIMED OUT", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Network Failure: GameNetDriver[ConnectionTimeout]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("BroadcastNetworkFailure: FailureType = ConnectionTimeout", StringComparison.OrdinalIgnoreCase);

        if (_inMatch && !_awaitingMenuAfterEnd && !_awaitingNetworkRecovery && networkDegradation)
        {
            _awaitingNetworkRecovery = true;
            SetResolution(false);
            return;
        }

        bool mapAndCameraReady =
            line.Contains("LogMapLoadModel: Display: UpdateMapReady:", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("PlayerCharacterLoaded: TRUE", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("MatchLoadInCompleted: TRUE", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("PlayerCameraReady: TRUE", StringComparison.OrdinalIgnoreCase);

        if (_awaitingNetworkRecovery && mapAndCameraReady && _inMatch && !_awaitingMenuAfterEnd)
        {
            _awaitingNetworkRecovery = false;
            SetResolution(true);
            return;
        }

        if (mapAndCameraReady && !_inMatch && !_awaitingMenuAfterEnd)
        {
            _inMatch = true;
            SetResolution(true);
        }
    }

    private Settings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
                if (loaded != null) return loaded;
            }
        }
        catch { }
        return new Settings();
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(_settingsPath,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void SetStartupRegistration(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(runKey, writable: true);
            if (enabled)
                key.SetValue("TrueStretchSwitcher", $"\"{Application.ExecutablePath}\"");
            else
                key.DeleteValue("TrueStretchSwitcher", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(3000, "Startup setting",
                $"Could not update Windows startup: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void ExitApp()
    {
        _exiting = true;
        _pollTimer.Stop();
        Native.UnregisterHotKey(_window.Handle, HotkeyId);
        _tray.Visible = false;
        _tray.Dispose();
        _pollTimer.Dispose();
        _window.Dispose();
        ExitThread();
    }
}

internal sealed class HiddenWindow : NativeWindow, IDisposable
{
    private readonly Action _onHotkey;
    public HiddenWindow(Action onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams { Caption = "TrueStretchSwitcher.HiddenWindow" });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312) _onHotkey();
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}

internal sealed class Settings
{
    public int NativeWidth { get; set; } = 2560;
    public int NativeHeight { get; set; } = 1440;
    public int StretchedWidth { get; set; } = 1080;
    public int StretchedHeight { get; set; } = 1080;
    public bool AutoSwitch { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public uint HotkeyModifiers { get; set; } = 0;
    public int HotkeyKey { get; set; } = (int)Keys.F6;
}

internal static class AppIcon
{
    public static Icon Create()
    {
        using Stream? stream = typeof(AppIcon).Assembly.GetManifestResourceStream("TrueStretchSwitcher.sWitch.ico");
        if (stream is null) return SystemIcons.Application;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}

internal static class DisplayModes
{
    public static (int Width, int Height) GetCurrent()
    {
        var mode = NewDevMode();
        if (Native.EnumDisplaySettings(null, -1, ref mode))
            return ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight);
        return (0, 0);
    }

    public static (int Width, int Height, int Hz) GetCurrentDetailed()
    {
        var mode = NewDevMode();
        if (Native.EnumDisplaySettings(null, -1, ref mode))
            return ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency);
        return (0, 0, 0);
    }

    public static List<(int Width, int Height)> GetAvailableModes()
    {
        var modes = new HashSet<(int Width, int Height)>();
        for (int i = 0; i < 2000; i++)
        {
            var mode = NewDevMode();
            if (!Native.EnumDisplaySettings(null, i, ref mode)) break;
            if (mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
                modes.Add(((int)mode.dmPelsWidth, (int)mode.dmPelsHeight));
        }

        // NVIDIA stores custom timings in NVAPI before Windows necessarily exposes
        // them through EnumDisplaySettings. Merge those driver-registered sizes into
        // the resolution selectors so newly-created modes (e.g. 1440x1024@320Hz)
        // can be selected without restarting the app.
        foreach (var mode in NvApiCustomDisplay.GetRegisteredModeSizes())
            if (mode.Width > 0 && mode.Height > 0)
                modes.Add(mode);

        return modes.OrderBy(m => m.Width * (long)m.Height)
            .ThenBy(m => m.Width).ThenBy(m => m.Height).ToList();
    }

    public static int GetHighestRefreshFor(int width, int height)
    {
        int best = 0;
        for (int i = 0; i < 2000; i++)
        {
            var mode = NewDevMode();
            if (!Native.EnumDisplaySettings(null, i, ref mode)) break;
            if (mode.dmPelsWidth == width && mode.dmPelsHeight == height && mode.dmDisplayFrequency > best)
                best = (int)mode.dmDisplayFrequency;
        }
        return best;
    }

    public static bool IsModeAvailable(int width, int height) =>
        GetAvailableModes().Contains((width, height));

    public static int Change(int width, int height)
    {
        var current = NewDevMode();
        if (!Native.EnumDisplaySettings(null, -1, ref current)) return -1;
        uint currentHz = current.dmDisplayFrequency;

        // Prefer the target resolution at the current refresh rate. If the driver does
        // not enumerate that exact combination, leave frequency unspecified so Windows
        // can choose a supported rate rather than forcing an invalid mode.
        Native.DEVMODE? exact = null;
        for (int i = 0; i < 2000; i++)
        {
            var candidate = NewDevMode();
            if (!Native.EnumDisplaySettings(null, i, ref candidate)) break;
            if (candidate.dmPelsWidth == width && candidate.dmPelsHeight == height &&
                candidate.dmDisplayFrequency == currentHz)
            {
                exact = candidate;
                break;
            }
        }

        var mode = exact ?? current;
        mode.dmPelsWidth = (uint)width;
        mode.dmPelsHeight = (uint)height;
        mode.dmFields = 0x00080000 | 0x00100000;
        if (exact.HasValue)
        {
            mode.dmFields |= 0x00400000; // DM_DISPLAYFREQUENCY
        }
        else
        {
            // If this mode is registered with NVIDIA but not yet listed by Windows,
            // request its registered refresh rate rather than silently using the
            // current mode's refresh value.
            int driverHz = NvApiCustomDisplay.GetRegisteredRefreshFor(width, height);
            if (driverHz > 1)
            {
                mode.dmDisplayFrequency = (uint)driverHz;
                mode.dmFields |= 0x00400000; // DM_DISPLAYFREQUENCY
            }
        }
        return Native.ChangeDisplaySettings(ref mode, 0);
    }

    private static Native.DEVMODE NewDevMode() => new()
    {
        dmDeviceName = new string('\0', 32),
        dmFormName = new string('\0', 32),
        dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>()
    };
}

internal sealed class ResolutionSettingsForm : Form
{
    private readonly ComboBox _nativeCombo = new();
    private readonly ComboBox _stretchedCombo = new();
    public (int Width, int Height) NativeMode => ((ResolutionChoice)_nativeCombo.SelectedItem!).Mode;
    public (int Width, int Height) StretchedMode => ((ResolutionChoice)_stretchedCombo.SelectedItem!).Mode;
    public uint HotkeyModifiers { get; private set; }
    public int HotkeyKey { get; private set; }
    private bool _capturingHotkey;
    private readonly Button _hotkeyButton = new();

    public ResolutionSettingsForm(int nativeWidth, int nativeHeight, int stretchedWidth, int stretchedHeight, uint hotkeyModifiers, int hotkeyKey)
    {
        HotkeyModifiers = hotkeyModifiers;
        HotkeyKey = hotkeyKey;
        Text = "sWitch — Resolution settings";
        KeyPreview = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(470, 370);
        Font = new Font("Segoe UI", 9F);

        var nativeLabel = new Label { Text = "Native / lobby resolution", AutoSize = true, Left = 18, Top = 20 };
        var stretchedLabel = new Label { Text = "Stretched / gameplay resolution", AutoSize = true, Left = 18, Top = 68 };
        ConfigureCombo(_nativeCombo, 18, 42);
        ConfigureCombo(_stretchedCombo, 18, 90);

        var hotkeyLabel = new Label { Text = "Toggle shortcut", AutoSize = true, Left = 18, Top = 133 };
        _hotkeyButton.SetBounds(150, 128, 302, 28);
        _hotkeyButton.FlatStyle = FlatStyle.Flat;
        _hotkeyButton.Text = FormatHotkeyText(HotkeyModifiers, HotkeyKey);
        _hotkeyButton.Click += (_, _) =>
        {
            _capturingHotkey = true;
            _hotkeyButton.Text = "Press a key combination…";
            _hotkeyButton.Focus();
        };
        KeyDown += (_, e) =>
        {
            if (!_capturingHotkey) return;
            if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
            uint mods = 0;
            if ((e.Modifiers & Keys.Control) != 0) mods |= 0x0002;
            if ((e.Modifiers & Keys.Alt) != 0) mods |= 0x0001;
            if ((e.Modifiers & Keys.Shift) != 0) mods |= 0x0004;
            HotkeyModifiers = mods;
            HotkeyKey = (int)e.KeyCode;
            _capturingHotkey = false;
            _hotkeyButton.Text = FormatHotkeyText(HotkeyModifiers, HotkeyKey);
            e.SuppressKeyPress = true;
            e.Handled = true;
        };

        var note = new Label
        {
            Text = "Modes are read from the NVIDIA/Windows display driver. Custom modes use the current refresh rate when asking NVIDIA to create them.",
            Left = 18, Top = 170, Width = 434, Height = 34
        };
        var customTitle = new Label { Text = "Create custom resolution", AutoSize = true, Left = 18, Top = 210 };
        var widthLabel = new Label { Text = "Width", AutoSize = true, Left = 18, Top = 236 };
        var heightLabel = new Label { Text = "Height", AutoSize = true, Left = 148, Top = 236 };
        var widthBox = new NumericUpDown { Left = 18, Top = 256, Width = 110, Minimum = 320, Maximum = 16384, Value = 1080, Increment = 8 };
        var heightBox = new NumericUpDown { Left = 148, Top = 256, Width = 110, Minimum = 200, Maximum = 16384, Value = 1080, Increment = 8 };
        var create = new Button { Text = "Test & create", Left = 274, Top = 253, Width = 178 };
        create.Click += (_, _) =>
        {
            Cursor = Cursors.WaitCursor;
            create.Enabled = false;
            try
            {
                if (NvApiCustomDisplay.TryCreate((int)widthBox.Value, (int)heightBox.Value, out var result))
                {
                    MessageBox.Show(this, result + "\n\nReopen the resolution lists if the new mode does not appear immediately.",
                        "Custom resolution", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    var refreshed = DisplayModes.GetAvailableModes();
                    AddModes(_nativeCombo, refreshed, (nativeWidth, nativeHeight));
                    AddModes(_stretchedCombo, refreshed, (stretchedWidth, stretchedHeight));
                }
                else
                    MessageBox.Show(this, result, "Could not create custom resolution", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "NVIDIA NVAPI custom-mode creation failed.\n\n" + ex.Message +
                    "\n\nMake sure the NVIDIA driver is installed and try running the app normally (not as administrator first).",
                    "Custom resolution", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = Cursors.Default; create.Enabled = true; }
        };
        var delete = new Button { Text = "Delete custom mode", Left = 18, Top = 292, Width = 150 };
        delete.Click += (_, _) =>
        {
            int w = (int)widthBox.Value, h = (int)heightBox.Value;
            if (MessageBox.Show(this, $"Delete the NVIDIA custom mode {w} × {h} at the current refresh rate?",
                    "Delete custom resolution", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Cursor = Cursors.WaitCursor;
            delete.Enabled = false;
            try
            {
                bool deleted = NvApiCustomDisplay.TryDelete(w, h, out var result);
                MessageBox.Show(this, result, deleted ? "Custom resolution deleted" : "Custom resolution not deleted",
                    MessageBoxButtons.OK, deleted ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                var refreshed = DisplayModes.GetAvailableModes();
                AddModes(_nativeCombo, refreshed, (nativeWidth, nativeHeight));
                AddModes(_stretchedCombo, refreshed, (stretchedWidth, stretchedHeight));
            }
            finally { Cursor = Cursors.Default; delete.Enabled = true; }
        };
        var scan = new Button { Text = "Scan driver modes", Left = 176, Top = 292, Width = 150 };
        scan.Click += (_, _) =>
        {
            NvApiCustomDisplay.TryListModes(out var report);
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueStretchSwitcher", "nvapi-custom-modes.txt");
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, report); } catch { }
            MessageBox.Show(this, report + "\nSaved to: " + path, "NVIDIA driver custom modes", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        var ok = new Button { Text = "Save selections", DialogResult = DialogResult.OK, Left = 274, Top = 332, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 362, Top = 332, Width = 90 };
        Controls.AddRange(new Control[] { nativeLabel, stretchedLabel, _nativeCombo, _stretchedCombo, hotkeyLabel, _hotkeyButton, note,
            customTitle, widthLabel, heightLabel, widthBox, heightBox, create, delete, scan, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        var modes = DisplayModes.GetAvailableModes();
        AddModes(_nativeCombo, modes, (nativeWidth, nativeHeight));
        AddModes(_stretchedCombo, modes, (stretchedWidth, stretchedHeight));
        if (_nativeCombo.SelectedIndex < 0 && _nativeCombo.Items.Count > 0) _nativeCombo.SelectedIndex = _nativeCombo.Items.Count - 1;
        if (_stretchedCombo.SelectedIndex < 0 && _stretchedCombo.Items.Count > 0) _stretchedCombo.SelectedIndex = 0;
        ok.Enabled = _nativeCombo.Items.Count > 0 && _stretchedCombo.Items.Count > 0;
    }

    private static string FormatHotkeyText(uint modifiers, int key)
    {
        var parts = new List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        parts.Add(((Keys)key).ToString());
        return string.Join("+", parts);
    }

    private static void ConfigureCombo(ComboBox combo, int left, int top)
    {
        combo.Left = left;
        combo.Top = top;
        combo.Width = 394;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
    }

    private static void AddModes(ComboBox combo, List<(int Width, int Height)> modes, (int Width, int Height) selected)
    {
        combo.Items.Clear();
        foreach (var mode in modes)
            combo.Items.Add(new ResolutionChoice(mode, mode.Width == selected.Width && mode.Height == selected.Height));
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (((ResolutionChoice)combo.Items[i]!).Mode == selected)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        // Preserve the current setting visibly if the driver no longer enumerates it.
        var missing = new ResolutionChoice(selected, false, unavailable: true);
        combo.Items.Add(missing);
        combo.SelectedItem = missing;
    }

    private sealed class ResolutionChoice
    {
        public (int Width, int Height) Mode { get; }
        private readonly bool _current;
        private readonly bool _unavailable;
        public ResolutionChoice((int Width, int Height) mode, bool current, bool unavailable = false)
        {
            Mode = mode;
            _current = current;
            _unavailable = unavailable;
        }
        public override string ToString() => $"{Mode.Width} × {Mode.Height}" + (_unavailable ? " (not currently available)" : "");
    }
}

internal static class NvApiCustomDisplay
{
    private const uint InitializeId = 0x0150E828;
    private const uint GetPrimaryDisplayIdId = 0x1E9D8A31;
    private const uint GetTimingId = 0x175167E9;
    private const uint TryCustomDisplayId = 0x1F7DB630;
    private const uint SaveCustomDisplayId = 0x49882876;
    private const uint RevertCustomDisplayTrialId = 0xCBBD40F0;
    private const uint EnumCustomDisplayId = 0xA2072D59;   // nvapi_interface.h: NvAPI_DISP_EnumCustomDisplay
    private const uint DeleteCustomDisplayId = 0x552E5B9B; // nvapi_interface.h: NvAPI_DISP_DeleteCustomDisplay
    private const int NvApiEndEnumeration = -7;            // NVAPI_END_ENUMERATION
    private static IntPtr _library;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterfaceDelegate(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPrimaryDisplayIdDelegate(out uint displayId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetTimingDelegate(uint displayId, ref NvTimingInput input, ref NvTiming timing);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TryCustomDisplayDelegate(ref uint displayIds, uint count, ref NvCustomDisplay customDisplay);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SaveCustomDisplayDelegate(ref uint displayIds, uint count, uint outputOnly, uint monitorOnly);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RevertCustomDisplayTrialDelegate(ref uint displayIds, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumCustomDisplayDelegate(uint displayId, uint index, ref NvCustomDisplay customDisplay);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteCustomDisplayDelegate(ref uint displayIds, uint count, ref NvCustomDisplay customDisplay);

    [StructLayout(LayoutKind.Sequential)] private struct NvViewport { public float X, Y, W, H; }
    // NV_TIMING_FLAG is three 32-bit allocation units in NVIDIA's C header:
    // isInterlaced/reserved0, the anonymous union, and scaling.
    // Representing it as one uint makes NV_TIMING_INPUT too small and causes
    // NVAPI_INCOMPATIBLE_STRUCT_VERSION (-9).
    [StructLayout(LayoutKind.Sequential)] private struct NvTimingFlag
    {
        public uint InterlacedAndReserved;
        public uint FormatId;
        public uint Scaling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NvTimingInput
    {
        public uint Version, Width, Height;
        public float RefreshRate;
        public NvTimingFlag Flag;
        public int Type;
    }
    [StructLayout(LayoutKind.Sequential)] private unsafe struct NvTimingExt
    {
        public uint Flag;
        public ushort RefreshRate;
        private ushort _padding0;
        public uint RefreshRateX1000;
        public uint Aspect;
        public ushort Repetition;
        private ushort _padding1;
        public uint Status;
        public fixed byte Name[40];
    }
    [StructLayout(LayoutKind.Sequential)] private struct NvTiming
    {
        public ushort HVisible, HBorder, HFrontPorch, HSyncWidth, HTotal;
        public byte HSyncPol;
        public ushort VVisible, VBorder, VFrontPorch, VSyncWidth, VTotal;
        public byte VSyncPol;
        public ushort Interlaced;
        public uint PixelClock;
        public NvTimingExt Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NvCustomDisplay
    {
        public uint Version, Width, Height, Depth;
        public int ColorFormat;
        public NvViewport SourcePartition;
        public float XRatio, YRatio;
        public NvTiming Timing;
        public uint HardwareModeSetOnly;
    }

    public static bool TryCreate(int width, int height, out string message)
    {
        if (width < 320 || height < 200 || width > 16384 || height > 16384)
        { message = "The requested dimensions are outside the allowed range."; return false; }
        IntPtr library = IntPtr.Zero;
        uint displayId = 0;
        bool trialStarted = false;
        try
        {
            // NVAPI is installed with the NVIDIA display driver.
            if (_library == IntPtr.Zero) _library = NativeLibrary.Load("nvapi64.dll");
            library = _library;
            AssertLayouts();
            var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(NativeLibrary.GetExport(library, "nvapi_QueryInterface"));
            var initialize = Get<InitializeDelegate>(query, InitializeId);
            var getPrimary = Get<GetPrimaryDisplayIdDelegate>(query, GetPrimaryDisplayIdId);
            var getTiming = Get<GetTimingDelegate>(query, GetTimingId);
            var tryCustom = Get<TryCustomDisplayDelegate>(query, TryCustomDisplayId);
            var saveCustom = Get<SaveCustomDisplayDelegate>(query, SaveCustomDisplayId);
            var revert = Get<RevertCustomDisplayTrialDelegate>(query, RevertCustomDisplayTrialId);
            var enumerate = Get<EnumCustomDisplayDelegate>(query, EnumCustomDisplayId);

            Check(initialize(), "NvAPI_Initialize");
            Check(getPrimary(out displayId), "NvAPI_DISP_GetGDIPrimaryDisplayId");
            // nvapi.h: RevertCustomDisplayTrial "must be called only after a custom display
            // configuration is tested ... otherwise no action is taken", so this clears any
            // stale trial from an earlier attempt and is a no-op when there is none.
            _ = revert(ref displayId, 1);

            var current = DisplayModes.GetCurrentDetailed();
            float hz = current.Hz > 1 ? current.Hz : DisplayModes.GetHighestRefreshFor(current.Width, current.Height);
            if (hz <= 1) hz = 60f;
            // Try the monitor's current refresh rate first, then fall back only
            // if NVIDIA rejects that timing for the requested dimensions.
            // The user does not need to enter a refresh rate manually.
            var refreshCandidates = new List<float> { hz };
            foreach (var candidate in new float[] { 300, 280, 270, 260, 240, 200, 180, 165, 160, 144, 120, 100, 75, 60 })
                if (candidate < hz - 0.1f && !refreshCandidates.Any(x => Math.Abs(x - candidate) < 0.1f))
                    refreshCandidates.Add(candidate);

            var acceptedHz = 0f;
            NvCustomDisplay acceptedCustom = default;
            var attempts = new List<string>();
            foreach (var candidateHz in refreshCandidates)
            {
                try
                {
                    // Try the documented formula modes. NVIDIA's CustomTiming sample
                    // uses CVT_RB (6), 32-bpp A8R8G8B8, and scaling=1.
                    // AUTO/default is retained as a fallback for driver compatibility.
                    var variants = new[]
                    {
                        (TimingType: 6, Depth: 32u, ColorFormat: 21, Scaling: 1u, Name: "CVT-RB/32-bit"),
                        (TimingType: 1, Depth: 32u, ColorFormat: 21, Scaling: 1u, Name: "AUTO/32-bit"),
                        (TimingType: 6, Depth: 0u, ColorFormat: 0, Scaling: 1u, Name: "CVT-RB/default-format"),
                        (TimingType: 1, Depth: 0u, ColorFormat: 0, Scaling: 0u, Name: "AUTO/default")
                    };

                    bool acceptedThisHz = false;
                    foreach (var variant in variants)
                    {
                        var input = new NvTimingInput
                        {
                            Version = MakeVersion<NvTimingInput>(1), Width = (uint)width, Height = (uint)height,
                            RefreshRate = candidateHz,
                            Flag = new NvTimingFlag { InterlacedAndReserved = 0, FormatId = 0, Scaling = variant.Scaling },
                            Type = variant.TimingType
                        };
                        var timing = new NvTiming();
                        int getStatus = getTiming(displayId, ref input, ref timing);
                        if (getStatus != 0)
                        {
                            attempts.Add($"{candidateHz:0.##} Hz {variant.Name}: GetTiming status {getStatus}");
                            continue;
                        }

                        var custom = new NvCustomDisplay
                        {
                            Version = MakeVersion<NvCustomDisplay>(1), Width = (uint)width, Height = (uint)height,
                            Depth = variant.Depth, ColorFormat = variant.ColorFormat,
                            SourcePartition = new NvViewport { X = 0, Y = 0, W = 1, H = 1 },
                            XRatio = 1, YRatio = 1, Timing = timing, HardwareModeSetOnly = 0
                        };
                        int tryStatus = tryCustom(ref displayId, 1, ref custom);
                        if (tryStatus == 0)
                        {
                            acceptedHz = candidateHz;
                            acceptedCustom = custom;
                            trialStarted = true;
                            acceptedThisHz = true;
                            break;
                        }
                        attempts.Add($"{candidateHz:0.##} Hz {variant.Name}: TryCustomDisplay status {tryStatus} [{TimingText(timing)}]");
                    }
                    if (acceptedThisHz) break;
                }
                catch (Exception exAttempt)
                {
                    attempts.Add($"{candidateHz:0.##} Hz: {exAttempt.Message}");
                }
            }

            if (!trialStarted)
            {
                string detail = string.Join("\n", attempts.Take(8));
                throw new InvalidOperationException(
                    $"NVIDIA rejected {width} × {height} at the current refresh rate ({hz:0.##} Hz) and the lower fallback rates tried.\n\n" +
                    $"Last attempts:\n{detail}\n\n" +
                    "This can mean the requested mode is unsupported by the monitor/driver. Try a more common size such as 1080 × 1080.");
            }

            try
            {
                // NVIDIA's CustomTiming sample waits 5 seconds after TryCustomDisplay
                // before saving, allowing the trial mode to settle on the hardware.
                // Our previous build called Save immediately, which may explain why
                // Try succeeded but Save returned NVAPI_ERROR (-1).
                Thread.Sleep(5000);
                Check(saveCustom(ref displayId, 1, 1, 1), "NvAPI_DISP_SaveCustomDisplay");
                // Documented order (NVIDIA CustomTiming sample): Try -> wait -> Save -> Revert.
                int revertStatus = revert(ref displayId, 1);
                trialStarted = false;

                // Save returning OK is not proof of persistence ("will not do anything" if no
                // trial is active). Confirm against the driver's own custom-mode list.
                var stored = EnumerateCustom(enumerate, displayId, out _);
                bool registered = stored.Any(c => c.Width == width && c.Height == height && Math.Abs(HzOf(c) - acceptedHz) < 1.0);
                bool windowsLists = DisplayModes.IsModeAvailable(width, height);

                message = $"NVIDIA accepted {width} × {height} at {acceptedHz:0.##} Hz." +
                          (acceptedHz < hz - 0.1f ? $"\n\nThe current {hz:0.##} Hz timing was rejected, so {acceptedHz:0.##} Hz was used instead." : "") +
                          $"\n\nTiming used: {TimingText(acceptedCustom.Timing)}" +
                          $"\n\nDriver custom-mode list (EnumCustomDisplay): {(registered ? "contains this mode" : "DOES NOT contain this mode - persistence NOT confirmed")}" +
                          $"\nWindows mode list (EnumDisplaySettings): {(windowsLists ? "lists this size" : "does not list this size (yet)")}" +
                          (revertStatus != 0 ? $"\n\nWarning: RevertCustomDisplayTrial returned {revertStatus}; the display may still be in the trial mode." : "");
                return true;
            }
            finally
            {
                // Safety net: only reached with an active trial if Save threw.
                if (trialStarted)
                {
                    _ = revert(ref displayId, 1);
                    trialStarted = false;
                }
            }
        }
        catch (Exception ex)
        {
            if (trialStarted)
            {
                try
                {
                    if (library != IntPtr.Zero)
                    {
                        var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(NativeLibrary.GetExport(library, "nvapi_QueryInterface"));
                        var revert = Get<RevertCustomDisplayTrialDelegate>(query, RevertCustomDisplayTrialId);
                        _ = revert(ref displayId, 1);
                    }
                }
                catch { }
            }
            message = ex.Message;
            return false;
        }
        // Keep the driver library loaded for the lifetime of the process. NVAPI is
        // initialized and its delegates may rely on the module remaining resident.
    }

    // ---- Custom-mode inspection / deletion (NvAPI_DISP_EnumCustomDisplay / DeleteCustomDisplay) ----

    // Return the resolution sizes saved in NVIDIA's own custom-display store.
    // This is needed because EnumDisplaySettings may omit a valid NVAPI mode.
    public static List<(int Width, int Height)> GetRegisteredModeSizes()
    {
        try
        {
            AssertLayouts();
            Check(Load<InitializeDelegate>(InitializeId)(), "NvAPI_Initialize");
            Check(Load<GetPrimaryDisplayIdDelegate>(GetPrimaryDisplayIdId)(out uint displayId), "NvAPI_DISP_GetGDIPrimaryDisplayId");
            var enumerate = Load<EnumCustomDisplayDelegate>(EnumCustomDisplayId);
            return EnumerateCustom(enumerate, displayId, out _)
                .Where(c => c.Width > 0 && c.Height > 0)
                .Select(c => ((int)c.Width, (int)c.Height))
                .Distinct()
                .ToList();
        }
        catch
        {
            // Keep settings usable even if NVAPI is unavailable on this system.
            return new List<(int Width, int Height)>();
        }
    }

    public static int GetRegisteredRefreshFor(int width, int height)
    {
        try
        {
            AssertLayouts();
            Check(Load<InitializeDelegate>(InitializeId)(), "NvAPI_Initialize");
            Check(Load<GetPrimaryDisplayIdDelegate>(GetPrimaryDisplayIdId)(out uint displayId), "NvAPI_DISP_GetGDIPrimaryDisplayId");
            var enumerate = Load<EnumCustomDisplayDelegate>(EnumCustomDisplayId);
            var matches = EnumerateCustom(enumerate, displayId, out _)
                .Where(c => c.Width == width && c.Height == height)
                .Select(c => (int)Math.Round(HzOf(c)))
                .Where(hz => hz > 1);
            return matches.DefaultIfEmpty(0).Max();
        }
        catch
        {
            return 0;
        }
    }

    // Reports what the NVIDIA driver itself has registered for the primary display.
    // This is the driver's custom-mode store - a different store from the Windows mode list.
    public static bool TryListModes(out string report)
    {
        try
        {
            AssertLayouts();
            Check(Load<InitializeDelegate>(InitializeId)(), "NvAPI_Initialize");
            Check(Load<GetPrimaryDisplayIdDelegate>(GetPrimaryDisplayIdId)(out uint displayId), "NvAPI_DISP_GetGDIPrimaryDisplayId");
            var enumerate = Load<EnumCustomDisplayDelegate>(EnumCustomDisplayId);
            var modes = EnumerateCustom(enumerate, displayId, out int endStatus);
            var windows = DisplayModes.GetAvailableModes();
            var current = DisplayModes.GetCurrentDetailed();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("NVAPI custom-mode report");
            sb.AppendLine($"GDI primary display ID: 0x{displayId:X8}");
            sb.AppendLine($"Current Windows mode: {current.Width} x {current.Height} @ {current.Hz} Hz");
            sb.AppendLine($"Driver custom modes: {modes.Count} (enumeration ended with status {endStatus}; {NvApiEndEnumeration} = normal end)");
            for (int i = 0; i < modes.Count; i++)
            {
                var c = modes[i];
                bool inWindows = windows.Contains(((int)c.Width, (int)c.Height));
                sb.AppendLine($"#{i}: {c.Width} x {c.Height} @ {HzOf(c):0.###} Hz  depth {c.Depth} fmt {c.ColorFormat}  " +
                              $"name \"{NameOf(c)}\"  Windows lists size: {(inWindows ? "yes" : "NO")}");
                sb.AppendLine($"     {TimingText(c.Timing)}");
            }
            report = sb.ToString();
            return true;
        }
        catch (Exception ex) { report = "NVAPI scan failed: " + ex.Message; return false; }
    }

    // Deletes the custom mode(s) matching width x height at the current refresh rate, then
    // re-enumerates to prove the driver no longer holds them.
    public static bool TryDelete(int width, int height, out string message)
    {
        try
        {
            AssertLayouts();
            var current = DisplayModes.GetCurrentDetailed();
            if (current.Width == width && current.Height == height)
            {
                message = $"{width} × {height} is the active Windows mode. Switch to a different resolution first, then delete it.";
                return false;
            }
            Check(Load<InitializeDelegate>(InitializeId)(), "NvAPI_Initialize");
            Check(Load<GetPrimaryDisplayIdDelegate>(GetPrimaryDisplayIdId)(out uint displayId), "NvAPI_DISP_GetGDIPrimaryDisplayId");
            var enumerate = Load<EnumCustomDisplayDelegate>(EnumCustomDisplayId);
            var delete = Load<DeleteCustomDisplayDelegate>(DeleteCustomDisplayId);

            bool Matches(NvCustomDisplay c) => c.Width == width && c.Height == height && Math.Abs(HzOf(c) - current.Hz) < 1.0;

            var list = EnumerateCustom(enumerate, displayId, out _);
            if (!list.Any(Matches))
            {
                var held = list.Count == 0 ? "none" : string.Join(", ", list.Select(c => $"{c.Width}×{c.Height}@{HzOf(c):0.##}"));
                message = $"The NVIDIA driver has no custom mode {width} × {height} at {current.Hz} Hz for the primary display. Nothing was deleted.\nDriver custom modes: {held}.";
                return false;
            }

            int removed = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                int index = list.FindIndex(Matches);
                if (index < 0) break;
                var entry = list[index];   // pass back exactly what EnumCustomDisplay returned
                int status = delete(ref displayId, 1, ref entry);
                if (status != 0)
                {
                    message = $"NvAPI_DISP_DeleteCustomDisplay returned status {status} (0x{status:X8}) for enumerated entry #{index}.";
                    return false;
                }
                int before = list.Count;
                list = EnumerateCustom(enumerate, displayId, out _);
                if (list.Count >= before)
                {
                    message = $"NvAPI_DISP_DeleteCustomDisplay returned OK but the entry still enumerates ({list.Count} driver modes). It was NOT removed.";
                    return false;
                }
                removed++;
            }

            bool gone = !list.Any(Matches);
            message = gone
                ? $"Removed {removed} driver entr{(removed == 1 ? "y" : "ies")} for {width} × {height} @ {current.Hz} Hz; re-enumeration confirms none remain." +
                  (DisplayModes.IsModeAvailable(width, height) ? "\n\nWindows still lists this size; it may clear after a display refresh or restart." : "")
                : $"Removed {removed} entr{(removed == 1 ? "y" : "ies")}, but {width} × {height} still enumerates in the driver.";
            return gone;
        }
        catch (Exception ex) { message = "NVAPI delete failed: " + ex.Message; return false; }
    }

    private static List<NvCustomDisplay> EnumerateCustom(EnumCustomDisplayDelegate enumerate, uint displayId, out int endStatus)
    {
        var modes = new List<NvCustomDisplay>();
        endStatus = 0;
        for (uint index = 0; index < 1024; index++)
        {
            var mode = new NvCustomDisplay { Version = MakeVersion<NvCustomDisplay>(1) };
            endStatus = enumerate(displayId, index, ref mode);
            if (endStatus != 0) break;   // NVAPI_END_ENUMERATION (-7) or an error
            modes.Add(mode);
        }
        return modes;
    }

    private static double HzOf(NvCustomDisplay c)
    {
        var e = c.Timing.Extra;
        if (e.RefreshRateX1000 > 0) return e.RefreshRateX1000 / 1000.0;
        if (c.Timing.HTotal > 0 && c.Timing.VTotal > 0 && c.Timing.PixelClock > 0)
            return c.Timing.PixelClock * 10000.0 / ((double)c.Timing.HTotal * c.Timing.VTotal);
        return e.RefreshRate;
    }

    private static unsafe string NameOf(NvCustomDisplay c)
    {
        var e = c.Timing.Extra;
        return (Marshal.PtrToStringAnsi((IntPtr)e.Name, 40) ?? "").Split('\0')[0];
    }

    // pclk is in 10 kHz units; polarity 1 = negative (nvapi.h).
    private static string TimingText(NvTiming t) =>
        $"H vis/fp/sw/total {t.HVisible}/{t.HFrontPorch}/{t.HSyncWidth}/{t.HTotal}, V {t.VVisible}/{t.VFrontPorch}/{t.VSyncWidth}/{t.VTotal}, " +
        $"pclk {t.PixelClock / 100.0:0.##} MHz, hsync {(t.HSyncPol == 1 ? "-" : "+")} vsync {(t.VSyncPol == 1 ? "-" : "+")}, interlaced {t.Interlaced}, rrx1k {t.Extra.RefreshRateX1000}";

    // The README claims runtime size assertions; this makes it true. The driver derives the
    // expected size from the version DWORD, so a wrong managed size is rejected with -9.
    private static void AssertLayouts()
    {
        static void Size<T>(int expected) where T : struct
        {
            int actual = Marshal.SizeOf<T>();
            if (actual != expected)
                throw new InvalidOperationException($"{typeof(T).Name} marshals to {actual} bytes but nvapi.h requires {expected}.");
        }
        Size<NvTimingFlag>(12);
        Size<NvTimingInput>(32);
        Size<NvTimingExt>(64);
        Size<NvTiming>(96);
        Size<NvCustomDisplay>(144);
        if ((int)Marshal.OffsetOf<NvCustomDisplay>(nameof(NvCustomDisplay.Timing)) != 44)
            throw new InvalidOperationException("NvCustomDisplay.Timing is not at offset 44.");
    }

    private static T Load<T>(uint id) where T : Delegate
    {
        if (_library == IntPtr.Zero) _library = NativeLibrary.Load("nvapi64.dll");
        var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(NativeLibrary.GetExport(_library, "nvapi_QueryInterface"));
        return Get<T>(query, id);
    }

    private static T Get<T>(QueryInterfaceDelegate query, uint id) where T : Delegate
    {
        var pointer = query(id);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException($"NVIDIA NVAPI function 0x{id:X8} is unavailable.");
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }
    private static uint MakeVersion<T>(uint version) where T : struct => (uint)Marshal.SizeOf<T>() | (version << 16);
    private static void Check(int status, string function)
    {
        if (status != 0) throw new InvalidOperationException($"{function} returned NVAPI status {status} (0x{status:X8}). See NVIDIA NVAPI status codes; this may indicate an API/structure issue or an unsupported timing.");
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ChangeDisplaySettings(ref DEVMODE devMode, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
