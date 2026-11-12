using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace DesktopOrbit.ShellIntegration;

/// <summary>A window on the taskbar: which program, which Store package, and (for Chrome/Edge) which profile it's in.</summary>
public sealed record AppWindow(
    IntPtr Handle,
    int ProcessId,
    string ProcessName,
    string ExePath,
    string Title,
    string? PackageFamily,
    string? ProfileButton,
    bool IsBrowserAppWindow);

/// <summary>
/// Finds the open windows of apps and browser profiles, so the ring can show which are open,
/// bring them to the front, or close them (politely, like pressing their own close button).
/// </summary>
public static class WindowTracker
{
    private static readonly int OwnProcess = Environment.ProcessId;
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge" };
    private static readonly Dictionary<IntPtr, (DateTime At, string? Profile, bool IsApp)> BrowserInfoCache = [];

    /// <summary>Self-test only: let the app see its own test window.</summary>
    internal static bool IncludeOwnWindowsForTest { get; set; }

    /// <summary>All normal app windows right now. Reads browser profile buttons, so call it off the UI thread.</summary>
    public static List<AppWindow> Snapshot()
    {
        var windows = new List<AppWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsTaskbarWindow(handle))
            {
                return true;
            }

            var title = Text(handle);
            var cls = ClassName(handle);
            if (title.Length == 0 || cls is "Progman" or "WorkerW" or "Shell_TrayWnd")
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var pid);
            if (pid == OwnProcess && !IncludeOwnWindowsForTest)
            {
                return true;
            }

            // Store apps run inside ApplicationFrameHost; the real app is its CoreWindow child.
            var appPid = pid;
            if (ProcessName(pid).Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                EnumChildWindows(handle, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out var childPid);
                    if (childPid != pid && ClassName(child) == "Windows.UI.Core.CoreWindow")
                    {
                        appPid = childPid;
                        return false;
                    }

                    return true;
                }, IntPtr.Zero);
            }

            var name = ProcessName(appPid);
            string? profile = null;
            var isApp = false;
            if (Browsers.Contains(name))
            {
                (profile, isApp) = BrowserInfo(handle);
            }

            windows.Add(new AppWindow(handle, appPid, name, ExePath(appPid), title, PackageFamily(appPid), profile, isApp));
            return true;
        }, IntPtr.Zero);

        // Forget closed windows.
        lock (BrowserInfoCache)
        {
            foreach (var gone in BrowserInfoCache.Keys.Except(windows.Select(w => w.Handle)).ToList())
            {
                BrowserInfoCache.Remove(gone);
            }
        }

        return windows;
    }

    /// <summary>Brings a window to the front (restoring it if minimised).</summary>
    public static void Activate(AppWindow window)
    {
        if (IsIconic(window.Handle))
        {
            ShowWindow(window.Handle, 9 /* SW_RESTORE */);
        }

        if (SetForegroundWindow(window.Handle))
        {
            return;
        }

        // Windows only lets the program in front hand over the focus; borrow its input queue for a moment.
        var front = GetForegroundWindow();
        var frontThread = GetWindowThreadProcessId(front, out _);
        var ownThread = GetCurrentThreadId();
        if (frontThread != 0 && frontThread != ownThread && AttachThreadInput(ownThread, frontThread, true))
        {
            try
            {
                BringWindowToTop(window.Handle);
                SetForegroundWindow(window.Handle);
            }
            finally
            {
                AttachThreadInput(ownThread, frontThread, false);
            }
        }
    }

    // Never ended by the ring: browsers keep other windows and profiles, and these belong to Windows itself.
    private static readonly HashSet<string> NeverEnd = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "explorer", "ApplicationFrameHost", "dwm", "svchost", "csrss", "winlogon",
        "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "SystemSettings", "OrbitalLauncher"
    };

    /// <summary>
    /// Some apps (Telegram, Discord, Spotify…) don't quit when their window is closed; they hide in the tray by the clock.
    /// After the ring closes such an app, this ends it if it has no window left on screen. Returns true if it was ended.
    /// </summary>
    public static async Task<bool> QuitIfHiddenInTrayAsync(AppWindow window)
    {
        if (NeverEnd.Contains(window.ProcessName))
        {
            return false;
        }

        await Task.Delay(1500); // give it time to close properly first
        Process process;
        try
        {
            process = Process.GetProcessById(window.ProcessId);
            if (process.HasExited)
            {
                return false; // it quit by itself
            }
        }
        catch (ArgumentException)
        {
            return false; // already gone
        }

        if (HasVisibleWindow(window.ProcessId))
        {
            return false; // still has another window open: leave it
        }

        var ended = EndQuietly(process);

        // Opening such an app again while it hides may start extra hidden copies (some apps do): end those too,
        // but never a copy that has a window on screen.
        if (!string.IsNullOrEmpty(window.ExePath))
        {
            foreach (var other in Process.GetProcessesByName(window.ProcessName))
            {
                if (other.Id != window.ProcessId && !HasVisibleWindow(other.Id) && ExePath(other.Id).Equals(window.ExePath, StringComparison.OrdinalIgnoreCase)
                    && IsTopCopy(other))
                {
                    EndQuietly(other);
                }
            }
        }

        await Task.Delay(300);
        return ended;
    }

    /// <summary>Ends a process and its helper processes.</summary>
    private static bool EndQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A main copy of a program, not one of its own helpers (whose parent is the same program).</summary>
    private static bool IsTopCopy(Process process)
    {
        try
        {
            using var query = new System.Management.ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {process.Id}");
            foreach (var item in query.Get())
            {
                var parent = Convert.ToInt32(item["ParentProcessId"]);
                try
                {
                    return !Process.GetProcessById(parent).ProcessName.Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase);
                }
                catch (ArgumentException)
                {
                    return true; // parent gone: it's a top copy
                }
            }
        }
        catch (Exception)
        {
        }

        return false; // not sure: leave it alone
    }

    private static bool HasVisibleWindow(int pid)
    {
        var found = false;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (owner == pid && IsTaskbarWindow(handle) && Text(handle).Length > 0)
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>True while the window exists and is shown (Telegram and others "close" by hiding to the tray).</summary>
    public static bool IsShown(IntPtr handle) => IsWindow(handle) && IsWindowVisible(handle);

    /// <summary>Asks a window to close, like clicking its own close button (it may ask to save first).</summary>
    public static void Close(AppWindow window) => PostMessage(window.Handle, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

    public static bool Exists(IntPtr handle) => IsWindow(handle);

    // ---------------------------------------------------------------- matching

    /// <summary>Windows that belong to an app from the catalogue.</summary>
    public static List<AppWindow> For(AppEntry app, IReadOnlyList<AppWindow> windows)
    {
        var own = OwnWindows(app, windows);
        if (own.Count > 0 || app.Source is AppSource.EdgeWebApp or AppSource.ChromeWebApp)
        {
            return own;
        }

        // Some Store apps (Instagram) just open an Edge or Chrome app window: find that instead, by its title.
        var name = AppCatalog.KeyFor(app.Name);
        return windows.Where(w => w.IsBrowserAppWindow && TitleStartsWith(w.Title, name)).ToList();
    }

    /// <summary>"Instagram - Personal", "(3) Facebook", "Facebook": the app's name at the start, or after a count.</summary>
    private static bool TitleStartsWith(string title, string name)
    {
        var text = title.TrimStart();
        if (text.StartsWith('(') && text.IndexOf(')') is var close and > 0)
        {
            text = text[(close + 1)..].TrimStart(); // unread count, e.g. "(3) Facebook"
        }

        return text.StartsWith(name, StringComparison.OrdinalIgnoreCase);
    }

    private static List<AppWindow> OwnWindows(AppEntry app, IReadOnlyList<AppWindow> windows)
    {
        switch (app.Source)
        {
            case AppSource.Store:
                var family = app.Id[..app.Id.IndexOf('!')];
                return windows.Where(w => string.Equals(w.PackageFamily, family, StringComparison.OrdinalIgnoreCase)).ToList();

            case AppSource.EdgeWebApp:
            case AppSource.ChromeWebApp:
                // Only the web app's own window (no profile button, few controls), never a normal browser window with that site in a tab.
                var browser = app.Source == AppSource.EdgeWebApp ? "msedge" : "chrome";
                var name = AppCatalog.KeyFor(app.Name);
                return windows.Where(w => w.IsBrowserAppWindow && w.ProcessName.Equals(browser, StringComparison.OrdinalIgnoreCase)
                                          && TitleStartsWith(w.Title, name)).ToList();

            default:
                var exe = app.TargetExe ?? DesktopExePath(app.Id);
                if (exe is not null)
                {
                    return windows.Where(w => w.ExePath.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                var process = KnownProcess(app.Id) ?? AppCatalog.KeyFor(app.Name).Replace(" ", string.Empty);
                return windows.Where(w => w.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase)
                                          && !(Browsers.Contains(w.ProcessName) && w.IsBrowserAppWindow)
                                          && !(w.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && ClassName(w.Handle) != "CabinetWClass")).ToList();
        }
    }

    /// <summary>Windows of a Chrome or Edge profile, recognised by the name on the window's profile button.</summary>
    public static List<AppWindow> For(BrowserProfile profile, IReadOnlyList<AppWindow> windows)
    {
        var process = profile.Browser == "Chrome" ? "chrome" : "msedge";

        // Chrome's button shows the profile or first name ("jane", "Jane's"); Edge's shows "Personal Profile".
        var names = new[] { profile.Name, profile.Account, profile.Account.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty, profile.ShortcutName }
            .Where(n => n.Length > 0)
            .SelectMany(n => new[] { n, n + " Profile" })
            .ToList();
        return windows.Where(w => w.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase) &&
                                  (ClaimedBy(w.Handle) == profile.Key ||
                                   (w.ProfileButton is { } button && ClaimedBy(w.Handle) is null && names.Any(n => button.Equals(n, StringComparison.OrdinalIgnoreCase)))))
            .ToList();
    }

    // ---------------------------------------------------------------- windows the ring opened

    private static readonly Dictionary<IntPtr, string> Claims = [];

    private static string? ClaimedBy(IntPtr handle)
    {
        lock (Claims)
        {
            return Claims.GetValueOrDefault(handle);
        }
    }

    /// <summary>
    /// After the ring opens a browser profile, the first new window of that browser is that profile's, even before the
    /// browser has drawn its profile button. Watches for up to 15 seconds.
    /// </summary>
    public static void ClaimNextWindow(BrowserProfile profile, IReadOnlyCollection<IntPtr> before)
    {
        var process = profile.Browser == "Chrome" ? "chrome" : "msedge";
        Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 15000)
            {
                await Task.Delay(300);
                var opened = ListTopWindows().FirstOrDefault(w => !before.Contains(w.Handle) && w.Process.Equals(process, StringComparison.OrdinalIgnoreCase));
                if (opened.Handle != IntPtr.Zero)
                {
                    lock (Claims)
                    {
                        foreach (var gone in Claims.Keys.Where(h => !IsWindow(h)).ToList())
                        {
                            Claims.Remove(gone);
                        }

                        Claims[opened.Handle] = profile.Key;
                    }

                    return;
                }
            }
        });
    }

    /// <summary>Quick list of taskbar windows and their process names (no profile reading).</summary>
    public static List<(IntPtr Handle, string Process)> ListTopWindows()
    {
        var list = new List<(IntPtr, string)>();
        EnumWindows((handle, _) =>
        {
            if (IsTaskbarWindow(handle) && Text(handle).Length > 0)
            {
                GetWindowThreadProcessId(handle, out var pid);
                list.Add((handle, ProcessName(pid)));
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string? KnownProcess(string appId) => appId switch
    {
        "MSEdge" => "msedge",
        "Chrome" => "chrome",
        "Microsoft.Windows.Explorer" => "explorer",
        _ when appId.StartsWith("com.squirrel.", StringComparison.OrdinalIgnoreCase) => appId.Split('.').Last(),
        _ => null
    };

    /// <summary>Desktop apps in the Start menu are listed as "{known folder}\path\app.exe"; turn that into a real path.</summary>
    private static string? DesktopExePath(string id)
    {
        if (id.Length > 2 && id[1] == ':' && id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }

        if (!id.StartsWith('{') || !id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var close = id.IndexOf('}');
        if (close < 0 || !Guid.TryParse(id[1..close], out var folderId))
        {
            return null;
        }

        if (SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var pathPointer) != 0)
        {
            return null;
        }

        try
        {
            return Path.Combine(Marshal.PtrToStringUni(pathPointer) ?? string.Empty, id[(close + 2)..]);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    // ---------------------------------------------------------------- browser windows

    /// <summary>The profile button's name ("jane", "Jane's"…) and whether this is a web-app window (no toolbar).</summary>
    private static (string? Profile, bool IsApp) BrowserInfo(IntPtr handle)
    {
        lock (BrowserInfoCache)
        {
            if (BrowserInfoCache.TryGetValue(handle, out var cached) && DateTime.Now - cached.At < TimeSpan.FromSeconds(20))
            {
                return (cached.Profile, cached.IsApp);
            }
        }

        string? profile = null;
        var isApp = false;
        try
        {
            var element = AutomationElement.FromHandle(handle);
            var buttons = element.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                // The profile button sits in the toolbar, named after the profile; skip the page's own buttons.
                var name = button.Current.Name;
                var id = button.Current.AutomationId;
                var cls = button.Current.ClassName;
                if (cls.Contains("AvatarToolbarButton", StringComparison.OrdinalIgnoreCase) || id.Contains("avatar", StringComparison.OrdinalIgnoreCase)
                    || cls.Contains("ProfileMenuButton", StringComparison.OrdinalIgnoreCase))
                {
                    profile = name;
                    break;
                }
            }

            // Web-app windows (Facebook, TikTok…) have no profile button and just a few title-bar buttons.
            isApp = profile is null && buttons.Count < 10;
        }
        catch (Exception)
        {
        }

        // A window that's still loading may not show its profile button yet: only remember a result once it's certain,
        // otherwise look again next time.
        if (profile is not null || isApp)
        {
            lock (BrowserInfoCache)
            {
                BrowserInfoCache[handle] = (DateTime.Now, profile, isApp);
            }
        }

        return (profile, isApp);
    }

    // ---------------------------------------------------------------- Win32

    private static bool IsTaskbarWindow(IntPtr handle)
    {
        if (!IsWindowVisible(handle) || GetWindow(handle, 4 /* GW_OWNER */) != IntPtr.Zero)
        {
            return false;
        }

        var exStyle = GetWindowLong(handle, -20);
        if ((exStyle & 0x80) != 0 /* WS_EX_TOOLWINDOW */)
        {
            return false;
        }

        return DwmGetWindowAttribute(handle, 14 /* DWMWA_CLOAKED */, out var cloaked, 4) != 0 || cloaked == 0;
    }

    private static string Text(IntPtr handle)
    {
        var builder = new StringBuilder(512);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    public static string ClassName(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string ProcessName(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string ExePath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var builder = new StringBuilder(1024);
            var size = builder.Capacity;
            return QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString() : string.Empty;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? PackageFamily(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var length = 0;
            if (GetPackageFamilyName(handle, ref length, null) != 122 /* ERROR_INSUFFICIENT_BUFFER */)
            {
                return null; // not a packaged app
            }

            var builder = new StringBuilder(length);
            return GetPackageFamilyName(handle, ref length, builder) == 0 ? builder.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private delegate bool EnumProc(IntPtr handle, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(int attach, int attachTo, bool join);
    [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(IntPtr process, ref int length, StringBuilder? name);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
