using System.Windows;
using System.Windows.Interop;
using DesktopOrbit.Ring;
using DesktopOrbit.ShellIntegration;
using Forms = System.Windows.Forms;

namespace DesktopOrbit;

public partial class App : System.Windows.Application
{
    private const int HotkeyId = 0x2100;

    private Mutex? _instanceMutex;
    private RingWindow? _ring;
    private RadioService? _radio;
    private FocusTimer? _timer;
    private AppCatalog? _catalog;
    private Forms.ToolStripMenuItem? _stopRadio;
    private Forms.NotifyIcon? _tray;
    private RingSettings _settings = new();
    private IntPtr _hotkeyWindow;
    private string _hotkeyLabel = "Alt+Space";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _instanceMutex = new Mutex(true, @"Local\TitaniumOrbitalLauncher.v2", out var isFirst);
        if (!isFirst)
        {
            // Already running: pass our arguments (e.g. from the desktop right-click menu) to it.
            SingleInstance.TrySend(e.Args);
            Shutdown();
            return;
        }

        var firstRun = !System.IO.File.Exists(RingSettings.SettingsPath);
        _settings = RingSettings.Load();
        Theme.Use(Theme.Find(_settings.ThemeName)); // before anything is drawn
        SetUpDesktopIntegration();

        var catalog = _catalog = new AppCatalog();
        _ = catalog.LoadAsync();

        _radio = new RadioService(_settings);
        foreach (var country in _settings.RadioCountries.Append(string.Empty))
        {
            _ = _radio.GetTopAsync(country); // warm up, so the country lists open instantly
        }

        _timer = new FocusTimer(_settings);
        _ring = new RingWindow(catalog, _settings, _radio, _timer, Notify);
        _timer.Finished += () =>
        {
            if (_timer.QuietForTest)
            {
                return;
            }

            _ring.ShowTimeUp();
            Notify("Time's up! Your work timer has finished.");
        };
        _hotkeyWindow = new WindowInteropHelper(_ring).EnsureHandle();
        HwndSource.FromHwnd(_hotkeyWindow)?.AddHook(OnWindowMessage);
        RegisterHotkey();
        SetUpTray();

        SingleInstance.Listen(args => Dispatcher.BeginInvoke(() => HandleArguments(args)));
        HandleArguments(e.Args);

        // A new person: find where their starting city is, then show them Customise to make the ring theirs.
        _ = ResolvePlacesAsync();
        if (firstRun)
        {
            Notify("Welcome to Titanium Orbital! Press Alt+Space to open the ring. Customise it to make it yours.");
            OpenCustomise();
        }
    }

    private void HandleArguments(string[] args)
    {
        if (args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (args.Contains("--testapps", StringComparer.OrdinalIgnoreCase) && _ring is not null)
        {
            // Anything after --testapps is a list of app names to test (otherwise the Social apps and accounts).
            var names = args.SkipWhile(a => !a.Equals("--testapps", StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();
            _ = _ring.TestRealAppsAsync(names).ContinueWith(t => Dispatcher.BeginInvoke(() =>
                Notify(t.IsFaulted ? $"Real-app test crashed: {t.Exception?.GetBaseException().Message}" : t.Result)));
            return;
        }

        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase) && _ring is not null && _radio is not null && _catalog is not null)
        {
            var test = new SelfTest(_catalog, _radio, _settings, _ring, _timer!, _hotkeyLabel);
            _ = test.RunAsync().ContinueWith(t => Dispatcher.BeginInvoke(() =>
                Notify(t.IsFaulted ? $"Self-test crashed: {t.Exception?.GetBaseException().Message}" : $"{t.Result}. Report: {SelfTest.ReportPath}")));
            return;
        }

        var ringIndex = Array.FindIndex(args, a => a.Equals("--ring", StringComparison.OrdinalIgnoreCase));
        var section = ringIndex >= 0 && ringIndex + 1 < args.Length ? args[ringIndex + 1] : null;
        _ring?.ShowRing(section);
    }

    private void SetUpDesktopIntegration()
    {
        try
        {
            if (_settings.DesktopMenuEnabled is null)
            {
                _settings.DesktopMenuEnabled = true;
                _settings.Save();
            }

            // Re-register every start so the entry follows the app if it moves.
            if (_settings.DesktopMenuEnabled == true)
            {
                DesktopIntegration.RegisterDesktopMenu();
            }

            if (_settings.StartWithWindows)
            {
                DesktopIntegration.SetStartWithWindows(true);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            // Registry blocked by policy: the hotkey and tray still work.
        }
    }

    private void RegisterHotkey()
    {
        var preferCtrl = _settings.Hotkey == "Ctrl+Alt+Space";
        if (!preferCtrl && NativeMethods.RegisterHotKey(_hotkeyWindow, HotkeyId, NativeMethods.ModAlt | NativeMethods.ModNoRepeat, NativeMethods.VkSpace))
        {
            return;
        }

        // Chosen in Customise, or Alt+Space is taken (PowerToys Run uses it): use Ctrl+Alt+Space.
        _hotkeyLabel = "Ctrl+Alt+Space";
        if (!NativeMethods.RegisterHotKey(_hotkeyWindow, HotkeyId, NativeMethods.ModAlt | NativeMethods.ModControl | NativeMethods.ModNoRepeat, NativeMethods.VkSpace))
        {
            _hotkeyLabel = "no hotkey (both are in use)";
        }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotKey && wParam.ToInt32() == HotkeyId)
        {
            _ring?.Toggle();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void SetUpTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Text = $"Titanium Orbital – {_hotkeyLabel}",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                _ring?.ShowRing(null);
            }
        };

        var menu = _tray.ContextMenuStrip;
        menu.Items.Add($"Open ring ({_hotkeyLabel})", null, (_, _) => _ring?.ShowRing(null));
        _stopRadio = new Forms.ToolStripMenuItem("Stop radio", null, (_, _) => _radio?.Stop()) { Visible = false };
        menu.Items.Add(_stopRadio);
        if (_radio is not null)
        {
            _radio.StateChanged += _ =>
            {
                var playing = _radio.State != RadioState.Stopped && _radio.Current is not null;
                _stopRadio.Visible = playing;
                _stopRadio.Text = playing ? $"Stop radio ({_radio.Current!.DisplayName})" : "Stop radio";
            };
        }

        menu.Items.Add(new Forms.ToolStripSeparator());

        var pin = new Forms.ToolStripMenuItem("Pin ring in place") { Checked = _ring?.IsPinned == true, CheckOnClick = true };
        pin.CheckedChanged += (_, _) =>
        {
            if (_ring is not null && _ring.IsPinned != pin.Checked)
            {
                _ring.SetPinned(pin.Checked);
                if (pin.Checked)
                {
                    _ring.ShowRing(null);
                }
            }
        };
        if (_ring is not null)
        {
            _ring.PinnedChanged += pinned => pin.Checked = pinned;
        }

        menu.Items.Add(pin);

        var desktopMenu = new Forms.ToolStripMenuItem("Desktop right-click entry") { Checked = _settings.DesktopMenuEnabled == true, CheckOnClick = true };
        desktopMenu.CheckedChanged += (_, _) =>
        {
            _settings.DesktopMenuEnabled = desktopMenu.Checked;
            _settings.Save();
            if (desktopMenu.Checked)
            {
                DesktopIntegration.RegisterDesktopMenu();
            }
            else
            {
                DesktopIntegration.UnregisterDesktopMenu();
            }
        };
        menu.Items.Add(desktopMenu);

        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = _settings.StartWithWindows, CheckOnClick = true };
        startup.CheckedChanged += (_, _) =>
        {
            _settings.StartWithWindows = startup.Checked;
            _settings.Save();
            DesktopIntegration.SetStartWithWindows(startup.Checked);
        };
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Insert(1, new Forms.ToolStripMenuItem("Customise…", null, (_, _) => OpenCustomise()));
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _tray.BalloonTipClicked += (_, _) => SystemActions.OpenFolder(SystemActions.ScreenshotFolder);
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/titanium-orbital-shortcut-v1.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (System.IO.IOException)
        {
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void Notify(string message) => _tray?.ShowBalloonTip(3000, "Titanium Orbital", message, Forms.ToolTipIcon.None);

    /// <summary>Places that only have a name (a new person's starting city) get their map position, for the weather.</summary>
    private async Task ResolvePlacesAsync()
    {
        var world = new WorldService();
        var changed = false;
        foreach (var place in _settings.Places.ToList())
        {
            changed |= await world.ResolveAsync(place);
        }

        if (changed)
        {
            _settings.Save();
        }
    }

    private Customise.CustomiseWindow? _customise;

    /// <summary>Opens the Customise window (themes, watch, places, radio, accounts, start-up, save/load).</summary>
    public void OpenCustomise()
    {
        _ring?.HideRing();
        if (_customise is { IsVisible: true })
        {
            _customise.Activate();
            return;
        }

        _customise = new Customise.CustomiseWindow(_settings, RestartToApply);
        _customise.Show();
        _customise.Activate();
    }

    /// <summary>Saves and restarts the app so a new theme or hotkey takes effect everywhere, then reopens the ring.</summary>
    public void RestartToApply()
    {
        _settings.Save();
        _radio?.Stop();
        NativeMethods.UnregisterHotKey(_hotkeyWindow, HotkeyId);
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        // Let go of "only one copy" first, so the new copy starts as the main one.
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--ring") { UseShellExecute = false });
        Shutdown();
    }

    private void ExitApplication()
    {
        _radio?.Stop();
        NativeMethods.UnregisterHotKey(_hotkeyWindow, HotkeyId);
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
