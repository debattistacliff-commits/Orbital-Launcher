using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DesktopOrbit.ShellIntegration;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace DesktopOrbit.Ring;

/// <summary>
/// Open apps: a green dot on bubbles whose app (or browser profile) is open, click switches to it instead of opening
/// another copy, and a small × closes it.
/// </summary>
public sealed partial class RingWindow
{
    private IReadOnlyList<AppWindow> _openWindows = [];
    private bool _readingWindows;
    private readonly List<(RingItem Item, FrameworkElement Dot, FrameworkElement Close)> _openMarks = [];

    /// <summary>Re-reads the open windows in the background, then updates the dots.</summary>
    private void RefreshOpenWindows()
    {
        if (_readingWindows)
        {
            return;
        }

        _readingWindows = true;
        Task.Run(WindowTracker.Snapshot).ContinueWith(task => Dispatcher.BeginInvoke(() =>
        {
            _readingWindows = false;
            if (task.IsCompletedSuccessfully)
            {
                _openWindows = task.Result;
                UpdateOpenMarks();
            }
        }), TaskScheduler.Default);
    }

    private List<AppWindow> WindowsOf(RingItem item) => item.OpenWindows?.Invoke(_openWindows) ?? [];

    /// <summary>Adds the (hidden) green dot and × to a bubble that can be open.</summary>
    private void AddOpenMarks(RingItem item, Grid bubble)
    {
        if (item.OpenWindows is null)
        {
            return;
        }

        var dot = new Ellipse
        {
            Width = 11,
            Height = 11,
            Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x7B)),
            Stroke = new SolidColorBrush(Theme.BaseA(0xCC)),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(9, 9, 0, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        var close = new Grid
        {
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Cursor = Cursors.Hand,
            Tag = "close " + item.Title,
            Visibility = Visibility.Collapsed
        };
        close.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Theme.BaseA(0xF0)),
            Stroke = new SolidColorBrush(Theme.InkA(0x90)),
            StrokeThickness = 1,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.5, Color = Theme.ShadowColor }
        });
        close.Children.Add(new Path
        {
            Data = Geometry.Parse("M0,0 L8,8 M8,0 L0,8"),
            Stroke = Theme.InkBrush,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        });
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseOpenApp(item);
        };

        bubble.Children.Add(dot);
        bubble.Children.Add(close);
        _openMarks.Add((item, dot, close));
        UpdateOpenMark(item, dot, close);
    }

    private void UpdateOpenMarks()
    {
        foreach (var (item, dot, close) in _openMarks)
        {
            UpdateOpenMark(item, dot, close);
        }

        UpdateHub();
    }

    private void UpdateOpenMark(RingItem item, FrameworkElement dot, FrameworkElement close)
    {
        var count = WindowsOf(item).Count;
        dot.Visibility = close.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        close.ToolTip = count > 1 ? $"Close {item.Title} ({count} windows)" : $"Close {item.Title}";
    }

    /// <summary>Extra hint words for an open app.</summary>
    private string OpenHint(RingItem item)
    {
        var windows = WindowsOf(item);
        if (windows.Count == 0)
        {
            return string.Empty;
        }

        var inFront = windows.Any(w => w.Handle == _lastAppInFront);
        return inFront
            ? $"Open and in front · click again to close it · "
            : $"Open{(windows.Count > 1 ? $" ({windows.Count} windows)" : string.Empty)} · click to bring it to the front, again to close · ";
    }

    /// <summary>
    /// Click on an app that's already open works like a switch:
    /// if it's the window you were using (in front before the ring), it closes; otherwise it comes to the front.
    /// Returns true when the click was handled here (so nothing new is opened).
    /// </summary>
    private bool SwitchToOpenApp(RingItem item)
    {
        var windows = WindowsOf(item).Where(w => WindowTracker.Exists(w.Handle)).ToList();
        if (windows.Count == 0)
        {
            return false;
        }

        if (windows.FirstOrDefault(w => w.Handle == _lastAppInFront) is { } inFront)
        {
            // Close just the window you were using, so things go back to how they were (other windows of the app stay).
            CloseFully(inFront, item.Title);
            Flash($"Closed {item.Title}");
            _lastAppInFront = IntPtr.Zero;
            Delay(900, RefreshOpenWindows);
            return true;
        }

        // Not in front: bring it there (clicking again then closes it).
        var target = windows[0];
        _lastAppInFront = target.Handle;
        CloseAfterLaunch();
        WindowTracker.Activate(target);
        return true;
    }

    /// <summary>The app window you were using before you came to the ring (not the ring itself).</summary>
    private IntPtr _lastAppInFront;

    /// <summary>Called when the ring opens and every second while it's open but not in use.</summary>
    private void NoteWindowInFront()
    {
        var front = NativeMethods.GetForegroundWindow();
        if (front != IntPtr.Zero && front != new System.Windows.Interop.WindowInteropHelper(this).Handle)
        {
            _lastAppInFront = front;
        }
    }

    private void CloseOpenApp(RingItem item)
    {
        var windows = WindowsOf(item);
        if (windows.Count == 0)
        {
            return;
        }

        foreach (var window in windows)
        {
            CloseFully(window, item.Title);
        }

        Flash(windows.Count > 1 ? $"Closing {item.Title} ({windows.Count} windows)…" : $"Closing {item.Title}…");
        Delay(900, RefreshOpenWindows);
        Delay(2500, RefreshOpenWindows);
    }

    /// <summary>Closes a window the normal way; if its app then only hides in the tray (Telegram), ends it.</summary>
    private void CloseFully(AppWindow window, string title)
    {
        WindowTracker.Close(window);
        _ = WindowTracker.QuitIfHiddenInTrayAsync(window).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    Flash($"{title} closed completely (it was hiding in the tray)");
                    RefreshOpenWindows();
                });
            }
        }, TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- self-test

    /// <summary>Self-test: open windows found for the pinned Chrome accounts and the social apps (read only).</summary>
    public async Task<List<string>> DescribeOpenAppsForTest()
    {
        _openWindows = await Task.Run(WindowTracker.Snapshot);
        var lines = new List<string>();
        var social = _socialView;
        _socialView = null;
        // Social bubbles plus every browser profile (some live under Browsers rather than in Social itself).
        var all = SocialItems().Concat(BrowserProfiles.All().Select(BrowserItem)).Where(i => i.OpenWindows is not null).DistinctBy(i => i.CountKey ?? i.Title);
        foreach (var item in all)
        {
            var count = WindowsOf(item).Count;
            if (count > 0)
            {
                lines.Add($"{item.Title}: {count} open");
            }
        }

        _socialView = social;
        return lines;
    }

    /// <summary>
    /// Self-test: the window was the one you were using, so clicking its bubble again must close it.
    /// Returns false if the window wasn't found.
    /// </summary>
    public async Task<bool> SecondClickClosesForTest(string exePath, string title)
    {
        _openWindows = await Task.Run(WindowTracker.Snapshot);
        var app = new AppEntry { Name = "Self-test", Id = exePath, Source = AppSource.Desktop };
        var item = new RingItem
        {
            Title = "Self-test",
            Activate = () => { },
            OpenWindows = windows => WindowTracker.For(app, windows).Where(w => w.Title == title).ToList()
        };

        var window = WindowsOf(item).FirstOrDefault();
        if (window is null)
        {
            return false;
        }

        _lastAppInFront = window.Handle; // as if you'd been using it before opening the ring
        return SwitchToOpenApp(item);
    }

    /// <summary>
    /// Real-app test (--testapps): for each Social bubble and your Edge account, opens it like a click, waits for its new
    /// window, then clicks the bubble again and checks that window closed. Windows that were already open are left alone.
    /// </summary>
    public async Task<string> TestRealAppsAsync(params string[] only)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine($"Real-app test · {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        report.AppendLine("Each app is opened from its bubble, then its bubble is clicked again; only the new window is closed.");
        report.AppendLine();
        var passed = 0;
        var failed = 0;

        var social = _socialView;
        _socialView = null;
        var items = SocialItems().Where(i => i.OpenWindows is not null).ToList();
        _socialView = social;
        items.AddRange(BrowserProfiles.All().Where(p => p.Browser == "Edge" && !string.IsNullOrEmpty(p.PicturePath)).Take(1).Select(BrowserItem));

        // Named apps only (e.g. --testapps notepad "my app"): look in Apps (both pages) and Social.
        if (only.Length > 0)
        {
            var page = _appsPage;
            _appsPage = 0;
            var apps = FavoriteItems();
            _appsPage = 1;
            apps.AddRange(FavoriteItems());
            _appsPage = page;
            items = apps.Concat(items)
                .Where(i => i.OpenWindows is not null && only.Any(n => i.Title.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(i => i.Title)
                .ToList();
        }

        foreach (var item in items)
        {
            var before = (await Task.Run(WindowTracker.Snapshot)).Select(w => w.Handle).ToHashSet();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                item.Activate(); // the same as clicking the bubble when the app isn't open
            }
            catch (Exception ex)
            {
                report.AppendLine($"  FAIL  {item.Title}: couldn't open ({ex.Message})");
                failed++;
                continue;
            }

            // Wait for the new window (apps such as Discord take a few seconds).
            AppWindow? opened = null;
            while (watch.ElapsedMilliseconds < 20000 && opened is null)
            {
                await Task.Delay(500);
                _openWindows = await Task.Run(WindowTracker.Snapshot);
                opened = WindowsOf(item).FirstOrDefault(w => !before.Contains(w.Handle));
            }

            if (opened is null)
            {
                var already = WindowsOf(item).Count;
                report.AppendLine(already > 0
                    ? $"  info  {item.Title}: was already open, so it came to the front instead of a new window (left as it was)"
                    : $"  FAIL  {item.Title}: no window appeared within 20 s");
                if (already == 0)
                {
                    failed++;
                }

                continue;
            }

            var openedAfter = watch.Elapsed.TotalSeconds;
            await Task.Delay(1500); // let it finish drawing, like a person would

            // It's the window you're using now; clicking its bubble again must close it.
            WindowTracker.Activate(opened);
            await Task.Delay(400);
            NoteWindowInFront();
            var inFront = _lastAppInFront == opened.Handle;
            SwitchToOpenApp(item);

            var closeWatch = System.Diagnostics.Stopwatch.StartNew();
            while (WindowTracker.IsShown(opened.Handle) && closeWatch.ElapsedMilliseconds < 8000)
            {
                await Task.Delay(250);
            }

            var gone = !WindowTracker.IsShown(opened.Handle);

            // Apps of their own (not browsers) must really quit, not hide in the tray by the clock.
            var quitNote = string.Empty;
            var stillRunning = false;
            if (opened.ProcessName is not ("chrome" or "msedge"))
            {
                var exitWatch = System.Diagnostics.Stopwatch.StartNew();
                while (exitWatch.ElapsedMilliseconds < 6000 && IsRunning(opened.ProcessId))
                {
                    await Task.Delay(250);
                }

                stillRunning = IsRunning(opened.ProcessId);
                quitNote = stillRunning ? " but it's still running in the tray" : ", and the program quit completely";
            }

            if (gone && inFront && !stillRunning)
            {
                passed++;
                report.AppendLine($"  PASS  {item.Title}: opened in {openedAfter:0.0} s (\"{Short(opened.Title)}\"), second click closed it in {closeWatch.Elapsed.TotalSeconds:0.0} s{quitNote}");
            }
            else if (gone && inFront)
            {
                failed++;
                report.AppendLine($"  FAIL  {item.Title}: window closed{quitNote}");
            }
            else
            {
                failed++;
                report.AppendLine($"  FAIL  {item.Title}: opened in {openedAfter:0.0} s, {(inFront ? string.Empty : "wasn't recognised as the window in front, ")}{(gone ? "closed" : "still open after the second click")}");
                if (!gone)
                {
                    WindowTracker.Close(opened); // tidy up
                }
            }

            await Task.Delay(800);
        }

        report.AppendLine();
        report.AppendLine($"{passed} passed, {failed} failed.");
        var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "realapps.txt");
        await System.IO.File.WriteAllTextAsync(path, report.ToString());
        return $"Real-app test: {passed} passed, {failed} failed";

        static string Short(string text) => text.Length > 40 ? text[..40] + "…" : text;

        static bool IsRunning(int pid)
        {
            try
            {
                return !System.Diagnostics.Process.GetProcessById(pid).HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>Self-test: finds and closes the windows of a program by its path.</summary>
    public static async Task<(int Found, int Left)> CloseProgramForTest(string exePath, string expectedTitle)
    {
        var app = new AppEntry { Name = "Self-test", Id = exePath, Source = AppSource.Desktop };
        var windows = WindowTracker.For(app, await Task.Run(WindowTracker.Snapshot)).Where(w => w.Title == expectedTitle).ToList();
        foreach (var window in windows)
        {
            WindowTracker.Close(window);
        }

        await Task.Delay(1500);
        var left = WindowTracker.For(app, await Task.Run(WindowTracker.Snapshot)).Count(w => w.Title == expectedTitle);
        return (windows.Count, left);
    }
}
