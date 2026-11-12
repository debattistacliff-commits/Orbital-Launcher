using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DesktopOrbit.ShellIntegration;

/// <summary>
/// Checks every part of the launcher on this PC and writes a report to %LOCALAPPDATA%\DesktopOrbit\selftest.txt.
/// Run with: OrbitalLauncher.exe --selftest. Restart and shut down are never run.
/// It plays a few seconds of radio and a short spoken phrase, and puts every setting it touches back.
/// </summary>
public sealed class SelfTest(AppCatalog catalog, RadioService radio, RingSettings settings, DesktopOrbit.Ring.RingWindow ring, FocusTimer timer, string hotkeyLabel)
{
    public static readonly string ReportPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "selftest.txt");

    private readonly StringBuilder _report = new();
    private int _passed;
    private int _failed;

    public async Task<string> RunAsync()
    {
        _report.AppendLine($"Orbital Launcher self-test · {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        _report.AppendLine();

        await Step("Apps", TestApps);
        await Step("Search", TestSearch);
        await Step("File search", TestFiles);
        await Step("Radio directory", TestRadioDirectory);
        await Step("Radio playback", TestRadioPlayback);
        await Step("Watch faces and radio button", TestWatch);
        await Step("Work timer", TestTimer);
        await Step("Controls, Wi-Fi, Bluetooth and Windows keys", TestControls);
        await Step("Browser profiles and Volume watch", TestBrowsersAndVolume);
        await Step("Open apps: switch and close", TestOpenApps);
        await Step("Pinning by drag and drop", TestPinning);
        await Step("World times and weather", TestWorld);
        await Step("Themes and flags", TestThemes);
        await Step("Starting settings and Customise", TestStartingSettings);
        await Step("Speaker and mic volume", TestVolume);
        await Step("Brightness", TestBrightness);
        await Step("Read aloud", TestReadAloud);
        await Step("Screenshot", TestScreenshot);
        await Step("Snip and record", TestSnip);
        await Step("Desktop right-click and hotkey", TestIntegration);

        _report.AppendLine();
        _report.AppendLine($"{_passed} passed, {_failed} failed. Restart and shut down were not run (they need you).");
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        await File.WriteAllTextAsync(ReportPath, _report.ToString());
        return $"Self-test: {_passed} passed, {_failed} failed";
    }

    private async Task Step(string name, Func<Task> test)
    {
        _report.AppendLine($"== {name}");
        await SaveProgress($"(running: {name})");
        var watch = Stopwatch.StartNew();
        try
        {
            var run = test();
            if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(120))) != run)
            {
                Fail("did not finish within 2 minutes");
            }
            else
            {
                await run;
            }
        }
        catch (Exception ex)
        {
            Fail($"crashed: {ex.GetType().Name}: {ex.Message}");
        }

        _report.AppendLine($"  ({watch.Elapsed.TotalSeconds:0.0} s)");
        _report.AppendLine();
    }

    private async Task SaveProgress(string note)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        await File.WriteAllTextAsync(ReportPath + ".partial", _report + note);
    }

    private void Pass(string message) { _passed++; _report.AppendLine("  PASS  " + message); }
    private void Fail(string message) { _failed++; _report.AppendLine("  FAIL  " + message); }
    private void Info(string message) => _report.AppendLine("  info  " + message);
    private void Check(bool ok, string message) { if (ok) Pass(message); else Fail(message); }

    private static Task Wait(int milliseconds) => Task.Delay(milliseconds);

    // ---------------------------------------------------------------- tests

    private async Task TestApps()
    {
        for (var i = 0; i < 40 && !catalog.IsLoaded; i++)
        {
            await Wait(250);
        }

        Check(catalog.Groups.Count > 50, $"{catalog.Groups.Count} apps read from Start, Store, web apps and desktop");
        if (Directory.Exists(AppCatalog.OldLibraryShortcuts))
        {
            var fromLibrary = catalog.Groups.Count(g => g.Variants.Any(v => v.Id.StartsWith(AppCatalog.OldLibraryShortcuts, StringComparison.OrdinalIgnoreCase)));
            Check(fromLibrary > 0, $"{fromLibrary} shortcuts from the old Desktop Orbit Library are in the app list (the rest are already Start menu apps)");
        }

        var social = catalog.Groups.Where(g => g.IsSocial).ToList();
        Check(social.Count > 0, $"social apps found: {string.Join(", ", social.Select(g => $"{g.Name} ({g.Primary.ShortSourceLabel switch { "" => "app", var s => s }})"))}");

        var toCheck = social.Concat((settings.FavoriteApps ?? []).Select(catalog.Find).OfType<AppGroup>()).DistinctBy(g => g.Key).ToList();
        var missing = new List<string>();
        foreach (var group in toCheck)
        {
            if (await ShellIcons.GetAsync(group.Primary.ShellPath) is null)
            {
                missing.Add(group.Name);
            }
        }

        // An icon resolving through the shell also proves the launch target exists.
        Check(missing.Count == 0, missing.Count == 0
            ? $"all {toCheck.Count} social and pinned apps resolve and have icons"
            : $"{missing.Count} of {toCheck.Count} apps have no icon / target: {string.Join(", ", missing)}");

        var facebook = catalog.Groups.FirstOrDefault(g => g.Key == "facebook");
        if (facebook is not null)
        {
            Info($"Facebook opens as: {facebook.Primary.SourceLabel}; other versions: {string.Join(", ", facebook.Variants.Skip(1).Select(v => v.SourceLabel))}");
        }
    }

    private Task TestSearch()
    {
        foreach (var (query, expected) in new[] { ("face", "Facebook"), ("tik", "TikTok"), ("whats", "WhatsApp"), ("screensh", "Screenshot"), ("down", "Downloads"), ("bright", "Brightness") })
        {
            var titles = ring.FindTitles(query);
            Check(titles.Any(t => t.Contains(expected, StringComparison.OrdinalIgnoreCase)),
                $"\"{query}\" → {(titles.Count == 0 ? "nothing" : string.Join(", ", titles.Take(4)))}");
        }

        return Task.CompletedTask;
    }

    private async Task TestFiles()
    {
        var watch = Stopwatch.StartNew();
        var hits = await FileSearch.QueryIndexAsync("desktop", 6, CancellationToken.None);
        Check(hits.Count > 0, $"Windows index answered in {watch.ElapsedMilliseconds} ms with {hits.Count} matches for \"desktop\"");

        // Plant a uniquely named file on a drive the index doesn't usually cover, then find it by scanning all drives.
        var exeFolder = Path.GetDirectoryName(Environment.ProcessPath) ?? Path.GetTempPath();
        var markerFolder = Path.Combine(Path.GetPathRoot(exeFolder) ?? exeFolder, "OrbitalLauncherSelfTest");
        var target = $"orbital-selftest-{Guid.NewGuid():N}.txt";
        Directory.CreateDirectory(markerFolder);
        var markerPath = Path.Combine(markerFolder, target);
        await File.WriteAllTextAsync(markerPath, "Created by the Orbital Launcher self-test; deleted straight after.");
        var found = new List<FileHit>();
        watch.Restart();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await FileSearch.ScanDrivesAsync(Path.GetFileNameWithoutExtension(target), 5, hit =>
            {
                lock (found)
                {
                    found.Add(hit);
                }

                timeout.Cancel(); // found it, stop the scan
            }, timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            File.Delete(markerPath);
            Directory.Delete(markerFolder);
        }

        var drives = string.Join(" ", DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name.TrimEnd('\\')));
        Check(found.Any(h => h.Name.Equals(target, StringComparison.OrdinalIgnoreCase)),
            $"all-drives scan ({drives}) found a test file on {Path.GetPathRoot(markerPath)} in {watch.Elapsed.TotalSeconds:0.0} s (test file deleted)");
    }

    private async Task TestRadioDirectory()
    {
        foreach (var country in settings.RadioCountries.Append(string.Empty))
        {
            var top = await radio.GetTopAsync(country);
            Check(top.Count >= 5, $"{RadioService.CountryName(country)}: {top.Count} playable stations, e.g. {string.Join(", ", top.Take(4).Select(s => s.DisplayName))}");
        }

        var search = await radio.SearchAsync("rai", CancellationToken.None);
        Check(search.Count > 0, $"search \"rai\": {string.Join(", ", search.Take(4).Select(s => s.DisplayName))}");
    }

    private async Task TestRadioPlayback()
    {
        var windowsVolumeBefore = AudioControl.Speakers.Level;
        var radioVolumeBefore = radio.Volume;
        var favoritesBefore = settings.RadioFavorites.ToList();
        var recentBefore = settings.RadioRecent.ToList();
        radio.Volume = 30;
        try
        {
            foreach (var country in settings.RadioCountries)
            {
                var station = (await radio.GetTopAsync(country)).FirstOrDefault();
                if (station is null)
                {
                    Fail($"{RadioService.CountryName(country)}: no station to play");
                    continue;
                }

                var watch = Stopwatch.StartNew();
                radio.Play(station);
                while (radio.State == RadioState.Connecting && watch.ElapsedMilliseconds < 12000)
                {
                    await Wait(100);
                }

                var connectMs = watch.ElapsedMilliseconds;
                if (radio.State != RadioState.Playing)
                {
                    Fail($"{station.DisplayName} ({country}) did not start within 12 s");
                    continue;
                }

                // Listen for 3 seconds: the station's own sound level, and what reaches the speakers.
                float level = 0, speakerPeak = 0;
                for (var i = 0; i < 15; i++)
                {
                    await Wait(200);
                    level = Math.Max(level, radio.Spectrum.Level);
                    speakerPeak = Math.Max(speakerPeak, AudioControl.Speakers.Peak ?? 0);
                }

                Check(level > 0.05 && speakerPeak > 0.005,
                    $"{station.DisplayName} ({RadioService.CountryName(country)}): playing after {connectMs} ms · music level {level:0.00} · speaker peak {speakerPeak:0.000}");
            }

            // Radio volume must change only the radio, never the Windows volume.
            radio.Volume = 10;
            await Wait(300);
            Check(AudioControl.Speakers.Level == windowsVolumeBefore,
                $"radio volume changed without touching Windows volume (Windows stays at {windowsVolumeBefore}%)");
        }
        finally
        {
            radio.Stop();
            radio.Volume = radioVolumeBefore;
            settings.RadioFavorites = favoritesBefore;
            settings.RadioRecent = recentBefore;
            settings.Save();
        }

        Check(radio.State == RadioState.Stopped, "radio stops");
    }

    public static readonly string WatchPreviewPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "watch-preview.png");

    public static readonly string SystemPreviewPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "system-preview.png");

    public static readonly string SocialPreviewPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "social-preview.png");

    /// <summary>
    /// Open apps: your open windows are only read (never switched or closed). Closing is tested on a console window the
    /// test opens itself, minimised and without taking focus.
    /// </summary>
    private async Task TestOpenApps()
    {
        var snapshot = await Task.Run(WindowTracker.Snapshot);
        Info($"{snapshot.Count} open windows; Chrome/Edge profile buttons seen: {string.Join(", ", snapshot.Where(w => w.ProfileButton is not null).Select(w => $"{w.ProcessName}:{w.ProfileButton}").Distinct())}");
        var open = await ring.DescribeOpenAppsForTest();
        var expected = snapshot.Any(w => w.ProfileButton is not null || w.IsBrowserAppWindow);
        Check(!expected || open.Count > 0, open.Count > 0 ? $"recognised as open: {string.Join(" · ", open)}" : "no social apps or Chrome accounts open right now");

        // A small window of the app's own, off-screen and without taking focus, stands in for "an open app".
        const string title = "Orbital Launcher self-test window";
        var closed = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            using var form = new QuietTestForm { Text = title };
            form.FormClosed += (_, _) => closed.TrySetResult();
            System.Windows.Forms.Application.Run(form);
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        WindowTracker.IncludeOwnWindowsForTest = true;
        thread.Start();
        try
        {
            var watch = Stopwatch.StartNew();
            var found = 0;
            var left = 1;
            while (watch.ElapsedMilliseconds < 6000)
            {
                await Wait(300);
                if ((await Task.Run(WindowTracker.Snapshot)).Any(w => w.Title == title))
                {
                    (found, left) = await DesktopOrbit.Ring.RingWindow.CloseProgramForTest(Environment.ProcessPath!, title);
                    break;
                }
            }

            var formClosed = await Task.WhenAny(closed.Task, Task.Delay(3000)) == closed.Task;
            Check(found == 1, found == 1 ? "an open window was recognised by its program" : "the test window wasn't recognised");
            Check(left == 0 && formClosed, "× closed it the normal way (its own close, as if you clicked its X)");

            // Click once opens, click again closes: a second test window, treated as the app you were using.
            var closedAgain = new TaskCompletionSource();
            var second = new Thread(() =>
            {
                using var form = new QuietTestForm { Text = title };
                form.FormClosed += (_, _) => closedAgain.TrySetResult();
                System.Windows.Forms.Application.Run(form);
            })
            { IsBackground = true };
            second.SetApartmentState(ApartmentState.STA);
            second.Start();
            var handled = false;
            var toggleWatch = Stopwatch.StartNew();
            while (!handled && toggleWatch.ElapsedMilliseconds < 6000)
            {
                await Wait(300);
                handled = await ring.SecondClickClosesForTest(Environment.ProcessPath!, title);
            }

            var toggledClosed = handled && await Task.WhenAny(closedAgain.Task, Task.Delay(3000)) == closedAgain.Task;
            Check(toggledClosed, "clicking the bubble of the app you were using closes it (click to open, click again to close)");
        }
        finally
        {
            WindowTracker.IncludeOwnWindowsForTest = false;
        }
    }

    /// <summary>A tiny window that never takes focus and sits off-screen, so the test doesn't disturb you.</summary>
    private sealed class QuietTestForm : System.Windows.Forms.Form
    {
        public QuietTestForm()
        {
            StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            Location = new System.Drawing.Point(-4000, -4000);
            Size = new System.Drawing.Size(120, 60);
            ShowInTaskbar = true;
        }

        protected override bool ShowWithoutActivation => true;
    }

    public static readonly string SkiesPreviewPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "weather-preview.png");

    public static readonly string ThemesPreviewPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "themes-preview.png");

    public static readonly string FlagsPreviewPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "flags-preview.png");

    /// <summary>Themes (each drawn on a fresh ring), sharing a theme file, and the flag backgrounds.</summary>
    private Task TestThemes()
    {
        var all = Theme.All();
        Check(all.Count >= 10, $"{all.Count} themes: {string.Join(", ", all.Select(t => t.Name))}");

        // Save a theme as a file, load it back, then remove it.
        var shared = Theme.BuiltIn.First(t => t.Name == "Neon").Copy("Self-test theme");
        var file = Theme.Save(shared);
        try
        {
            var imported = Theme.Import(file);
            Check(File.Exists(file) && Theme.All().Any(t => t.Name == "Self-test theme") && imported.Accent == shared.Accent,
                "a theme can be saved as a file, shared and loaded again");
        }
        finally
        {
            File.Delete(file);
        }

        var original = Theme.Current;
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>();
        try
        {
            foreach (var theme in Theme.BuiltIn)
            {
                Theme.Use(theme);
                var preview = new DesktopOrbit.Ring.RingWindow(catalog, settings, radio, timer, _ => { });
                frames.Add(preview.RenderFrameForTest("system", 520, 600));
                preview.Close();
            }
        }
        finally
        {
            Theme.Use(original);
        }

        DesktopOrbit.Ring.RingWindow.SaveStrip(frames, ThemesPreviewPath, columns: 5);
        Check(frames.Count == Theme.BuiltIn.Length, $"drew the ring in all {frames.Count} themes to {ThemesPreviewPath}");

        var colour = settings.ClockColour;
        ring.RenderFlagsForTest(FlagsPreviewPath);
        settings.ClockColour = colour;
        Check(new FileInfo(FlagsPreviewPath).Length > 20_000, $"drew the watch with every flag to {FlagsPreviewPath}");

        // Any country's flag (or your own picture) can go on the watch.
        var countries = DesktopOrbit.Ring.RingWindow.Countries;
        var broken = countries.Where(c =>
        {
            try
            {
                var picture = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Assets/flags/{c.Code}.png"));
                return picture.PixelWidth < 100;
            }
            catch (Exception)
            {
                return true;
            }
        }).Select(c => c.Name).ToList();
        Check(countries.Count >= 250 && broken.Count == 0,
            $"{countries.Count} country flags to choose from in Customise, all load{(broken.Count > 0 ? $" (broken: {string.Join(", ", broken)})" : string.Empty)}");

        var photo = Path.Combine(Path.GetTempPath(), "orbital-myflag-test.png");
        System.Windows.Media.Imaging.BitmapEncoder encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        var strip = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 200, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.MediumPurple, null, new System.Windows.Rect(0, 0, 300, 200));
            dc.DrawEllipse(System.Windows.Media.Brushes.Gold, null, new System.Windows.Point(150, 100), 60, 60);
        }

        strip.Render(visual);
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(strip));
        using (var photoFile = File.Create(photo))
        {
            encoder.Save(photoFile);
        }

        ring.RenderMyFlagsForTest(MyFlagsPreviewPath, ["flag:br", "flag:us", "flag:in", "flag:jp", "flag:za", "flag:ca", photo]);
        File.Delete(photo);
        Check(new FileInfo(MyFlagsPreviewPath).Length > 20_000, $"drew the watch with your own flag (Brazil, USA, India, Japan, South Africa, Canada, a picture) to {MyFlagsPreviewPath}");
        return Task.CompletedTask;
    }

    public static readonly string MyFlagsPreviewPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "my-flags-preview.png");

    public static readonly string CustomisePreviewPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "customise-preview.png");

    /// <summary>What a brand-new person would start with (worked out here, nothing saved), and that your own settings are untouched.</summary>
    private async Task TestStartingSettings()
    {
        var fresh = new RingSettings();
        var place = fresh.Places.FirstOrDefault();
        Check(place is not null && !string.IsNullOrEmpty(place.TimeZone),
            $"a new person starts with their own city from this PC's time zone: {place?.Name} ({place?.TimeZone})");
        if (place is not null)
        {
            var resolved = await new WorldService().ResolveAsync(place);
            Check(resolved && place.Latitude != 0, $"its map position is found on first start: {place.Name}, {place.Country} ({place.Latitude:0.00}, {place.Longitude:0.00})");
        }

        Check(fresh.RadioCountries.Count == 1, $"a new person's radio starts with their own country: {string.Join(", ", fresh.RadioCountries.Select(RadioService.CountryName))} + World");
        Check(fresh.FavoriteApps is null && fresh.CustomPins.Count == 0 && fresh.ThemeName == "Glass", "no one else's apps, pins or theme are pre-set");

        Check(settings.Places.Count > 0 && settings.RadioCountries.Count > 0,
            $"your own setup is kept: places {string.Join(", ", settings.Places.Select(p => p.Name))} · radio {string.Join(" ", settings.RadioCountries)} · theme {settings.ThemeName}");

        var window = new DesktopOrbit.Customise.CustomiseWindow(settings, () => { });
        window.RenderPagesForTest(CustomisePreviewPath);
        window.Close();
        Check(new FileInfo(CustomisePreviewPath).Length > 20_000, $"drew the Customise window's pages to {CustomisePreviewPath}");

        TestYourFlag();
    }

    public static readonly string YourFlagPreviewPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopOrbit", "your-flag-test.png");

    /// <summary>
    /// "Your flag" used the way a new person would, on their own fresh settings (yours aren't touched):
    /// pick a country, type one, use a picture, check it's kept after a restart, then back to none.
    /// </summary>
    private void TestYourFlag()
    {
        var person = new RingSettings();
        var window = new DesktopOrbit.Customise.CustomiseWindow(person, () => { });
        var pictureCopy = Path.Combine(Path.GetDirectoryName(RingSettings.SettingsPath)!, "my-flag.png");
        var hadPicture = File.Exists(pictureCopy);
        var photo = Path.Combine(Path.GetTempPath(), "orbital-yourflag-test.png");
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>();
        try
        {
            var before = DesktopOrbit.Ring.RingWindow.WatchBackgroundNames.Count;

            // 1. Pick a country from the list.
            var (saved, shown, background) = window.YourFlagForTest(pick: "Brazil");
            Check(saved == "flag:br" && shown == "Brazil" && background == "Brazil" && person.ClockColour == DesktopOrbit.Ring.RingWindow.MyFlagIndex,
                $"pick Brazil in Your flag → saved \"{saved}\", watch background set to \"{background}\" (number {person.ClockColour})");
            Check(DesktopOrbit.Ring.RingWindow.WatchBackgroundNames.Count == before + 1 && DesktopOrbit.Ring.RingWindow.WatchBackgroundNames[^1] == "Brazil",
                "Brazil is added at the end of the watch backgrounds, after the built-in flags");
            frames.Add(ring.RenderWatchForTest("flag:br"));

            // 2. Kept after a restart: save to a file and read it back, like starting the app again.
            var file = Path.Combine(Path.GetTempPath(), "orbital-yourflag-settings.json");
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(person));
            var reloaded = RingSettings.TryLoadFrom(file);
            File.Delete(file);
            Check(reloaded?.MyFlag == "flag:br" && reloaded.ClockColour == person.ClockColour, "the choice is still there after a restart");

            // 3. Type a country instead of scrolling.
            (saved, shown, background) = window.YourFlagForTest(typed: "Malta");
            Check(saved == "flag:mt" && background == "Malta", $"typing \"Malta\" finds it → saved \"{saved}\", watch shows \"{background}\"");
            frames.Add(ring.RenderWatchForTest("flag:mt"));
            (saved, _, background) = window.YourFlagForTest(typed: "Japan");
            Check(saved == "flag:jp" && background == "Japan", $"typing \"Japan\" → \"{background}\"");
            frames.Add(ring.RenderWatchForTest("flag:jp"));

            // 4. Their own picture (a wide one, to check it's cut square from the middle).
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(400, 240, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.Teal, null, new System.Windows.Rect(0, 0, 400, 240));
                dc.DrawEllipse(System.Windows.Media.Brushes.Orange, null, new System.Windows.Point(200, 120), 80, 80);
            }

            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = File.Create(photo))
            {
                encoder.Save(stream);
            }

            (saved, shown, background) = window.YourFlagForTest(picture: photo);
            Check(saved is not null && File.Exists(saved) && shown == "My picture" && background == "My picture",
                $"Use my own picture → a copy is kept ({Path.GetFileName(saved)}), watch shows \"{background}\"");
            frames.Add(ring.RenderWatchForTest(saved!));

            // 5. Back to none: the extra background goes away again.
            (saved, _, _) = window.YourFlagForTest(pick: "None");
            Check(saved is null && DesktopOrbit.Ring.RingWindow.WatchBackgroundNames.Count == before && person.ClockColour < before,
                "choosing None removes it from the watch backgrounds");

            DesktopOrbit.Ring.RingWindow.SaveStrip(frames, YourFlagPreviewPath, columns: frames.Count);
            Info($"pictures of each step: {YourFlagPreviewPath}");
        }
        finally
        {
            window.Close();
            DesktopOrbit.Ring.RingWindow.MyFlag = settings.MyFlag; // your own flag back on your watch
            File.Delete(photo);
            if (!hadPicture)
            {
                File.Delete(pictureCopy);
            }
        }
    }

    /// <summary>World times and weather: your places, a city added and removed again, and every weather scene.</summary>
    private async Task TestWorld()
    {
        var places = settings.Places.ToList();
        Check(places.Count > 0, $"your places: {string.Join(", ", places.Select(p => $"{p.Name} {WorldService.TimeIn(p):HH:mm}"))}");

        var malta = places.FirstOrDefault(p => p.TimeZone == "Europe/Malta");
        var uk = places.FirstOrDefault(p => p.TimeZone == "Europe/London");
        if (malta is not null && uk is not null)
        {
            var gap = (WorldService.TimeIn(malta) - WorldService.TimeIn(uk)).TotalHours;
            Check(Math.Abs(gap - 1) < 0.02, $"Malta is {gap:0.#} hour ahead of the UK");
        }

        foreach (var place in places.Take(3))
        {
            var weather = await ring.WeatherForTest(place);
            Check(weather is not null, weather is null ? $"{place.Name}: no weather (offline?)"
                : $"{place.Name}: {weather.Summary}, {weather.Humidity}% humid, wind {Math.Round(weather.Wind)} km/h · {weather.Forecast}");
        }

        var found = await ring.FindPlacesForTest("Rome");
        var rome = found.FirstOrDefault(p => p.TimeZone == "Europe/Rome");
        Check(rome is not null, rome is null ? "couldn't find Rome" : $"\"w rome\" finds {rome.Name}, {rome.Country} ({rome.TimeZone})");
        if (rome is not null)
        {
            try
            {
                var message = ring.AddPlace(rome);
                Check(settings.Places.Any(p => p.Key == rome.Key), $"added a city: \"{message}\"");
            }
            finally
            {
                settings.Places = places; // put your places back
                settings.Save();
            }
        }

        var scenes = new[] { (0, false, true), (3, false, true), (61, false, true), (71, false, true), (95, false, true), (45, false, true), (3, true, true), (0, false, false) };
        var built = scenes.Select(s => ring.ShowSkyForTest(s.Item1, s.Item2, s.Item3)).ToList();
        Check(built.All(b => !b.EndsWith(": 0 shapes")), $"weather scenes: {string.Join(" · ", built)}");

        ring.RenderSkiesForTest(SkiesPreviewPath);
        Check(new FileInfo(SkiesPreviewPath).Length > 20_000, $"drew every weather scene to {SkiesPreviewPath}");
    }

    /// <summary>Pinning your own things: a test shortcut and folder are pinned, shown, then unpinned and deleted.</summary>
    private Task TestPinning()
    {
        // Which section a drop lands in, around the ring (sectors: Apps top, Social upper right, Files lower right…).
        var spots = new (double Angle, string Expected)[] { (-90, "apps"), (-30, "web"), (30, "files"), (90, "apps"), (150, "apps"), (-150, "apps") };
        var wrong = spots.Where(s => ring.SectionAtAngleForTest(s.Angle) != s.Expected).Select(s => $"{s.Angle}° → {ring.SectionAtAngleForTest(s.Angle)}").ToList();
        Check(wrong.Count == 0, wrong.Count == 0 ? "dropping on Apps, Social or Files pins there; anywhere else pins to Apps" : $"wrong section: {string.Join(", ", wrong)}");

        var folder = Path.Combine(Path.GetTempPath(), "Orbital pin test");
        Directory.CreateDirectory(folder);
        var shortcut = Path.Combine(folder, "Orbital Test Pin.lnk");
        var favoritesBefore = settings.FavoriteApps?.ToList();
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(shortcut);
            link.TargetPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            link.Save();

            var message = ring.AddPins([shortcut], "apps");
            var pin = settings.CustomPins.FirstOrDefault(p => p.Path == shortcut);
            Check(pin is not null && ring.TitlesInForTest("apps").Contains("Orbital Test Pin"), $"a dropped shortcut is pinned to Apps (\"{message}\")");
            Check(pin?.Target.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) == true, "its program is known, so it gets the green dot and closes like other apps");

            var again = ring.AddPins([shortcut], "apps");
            Check(settings.CustomPins.Count(p => p.Path == shortcut) == 1, $"dropping it twice doesn't pin it twice (\"{again}\")");

            ring.AddPins([folder], "files");
            Check(ring.TitlesInForTest("files").FirstOrDefault() == "Orbital pin test", "a dropped folder is pinned first in Files");

        }
        finally
        {
            ring.UnpinForTest([shortcut, folder]);
            settings.FavoriteApps = favoritesBefore;
            settings.Save();
            try
            {
                File.Delete(shortcut);
                Directory.Delete(folder);
            }
            catch (IOException)
            {
            }
        }

        Check(!settings.CustomPins.Any(p => p.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)), "unpinned and test files deleted");
        return Task.CompletedTask;
    }

    /// <summary>Browser profiles (no browser windows are opened) and the Volume watch.</summary>
    private async Task TestBrowsersAndVolume()
    {
        var profiles = BrowserProfiles.All();
        var chrome = profiles.Where(p => p.Browser == "Chrome").ToList();
        var edge = profiles.Where(p => p.Browser == "Edge").ToList();
        Check(chrome.Count > 0 && edge.Count > 0,
            $"Chrome profiles: {string.Join(", ", chrome.Select(p => p.DisplayName))} · Edge profiles: {string.Join(", ", edge.Select(p => p.DisplayName))}");

        var pictures = await Task.WhenAll(profiles.Select(BrowserProfiles.PictureAsync));
        Check(pictures.Count(p => p is not null) >= 2, $"{pictures.Count(p => p is not null)} of {profiles.Count} profile pictures loaded");
        Check(profiles.All(p => File.Exists(p.Executable)), $"browsers found: {string.Join(", ", profiles.Select(p => p.Executable).Distinct().Select(Path.GetFileName))}");

        var social = ring.SocialTitlesForTest();
        var pinned = settings.PinnedBrowserProfiles.Select(BrowserProfiles.Find).OfType<BrowserProfile>().Select(p => p.DisplayName).ToList();
        Check(pinned.Count == 2 && social.Take(2).SequenceEqual(pinned),
            $"Social starts with your two Chrome accounts: {string.Join(", ", social.Take(4))}…");

        // Volume watch: its buttons take clicks, and + then − puts the volume back.
        ring.ShowVolumeWatchForTest(true);
        try
        {
            var spots = new (string Where, double X, double Y, string Expected)[]
            {
                ("speaker", 0, -33, "volume mute"), ("−", -21, 40, "volume down"), ("+", 21, 40, "volume up")
            };
            var misses = spots.Select(s => (s, Got: ring.HitTestWatch(s.X, s.Y))).Where(r => r.Got != r.s.Expected).Select(r => $"{r.s.Where} → {r.Got}").ToList();
            Check(misses.Count == 0, misses.Count == 0 ? "Volume watch: speaker, − and + buttons take clicks" : $"Volume watch buttons blocked: {string.Join(", ", misses)}");

            var (before, up, down) = ring.PressVolumeButtonsForTest();
            Check(up != before && down == before, $"Volume watch: {before}% → {up}% → {down}%");
        }
        finally
        {
            ring.ShowVolumeWatchForTest(false);
        }

        ring.RenderViewsPreview(SocialPreviewPath, ("web", null), ("web", "browsers"), ("volume", null));
        Check(new FileInfo(SocialPreviewPath).Length > 20_000, $"drew Social, Browsers and the Volume watch to {SocialPreviewPath}");
    }

    /// <summary>Wi-Fi and Bluetooth are only read here, never switched: switching would cut your connection or headphones.</summary>
    private async Task TestControls()
    {
        await Connectivity.RefreshAsync();
        var wifi = Connectivity.IsOn(Windows.Devices.Radios.RadioKind.WiFi);
        var bluetooth = Connectivity.IsOn(Windows.Devices.Radios.RadioKind.Bluetooth);
        Check(Connectivity.WifiStatus != "…" && Connectivity.BluetoothStatus != "…",
            $"Wi-Fi: {(wifi is null ? "no Wi-Fi adapter" : wifi.Value ? "on" : "off")} · Bluetooth: {(bluetooth is null ? "no Bluetooth adapter" : bluetooth.Value ? "on" : "off")} (read only, nothing switched)");
        Info($"network: {Connectivity.NetworkName() ?? "unknown"}{(Connectivity.IsWired() ? " (cable)" : string.Empty)}");

        var access = await Windows.Devices.Radios.Radio.RequestAccessAsync();
        Check(access == Windows.Devices.Radios.RadioAccessStatus.Allowed, $"Windows lets the app switch Wi-Fi and Bluetooth ({access})");

        foreach (var (query, expected) in new[] { ("wifi", "Wi-Fi"), ("blue", "Bluetooth"), ("show desk", "Show desktop"), ("task view", "Task view"), ("emoji", "Emoji"), ("explorer", "File Explorer"), ("mic", "Mic") })
        {
            var titles = ring.FindTitles(query);
            Check(titles.Any(t => t.Equals(expected, StringComparison.OrdinalIgnoreCase)), $"\"{query}\" → {(titles.Count == 0 ? "nothing" : string.Join(", ", titles.Take(3)))}");
        }

        ring.RenderSystemPreview(SystemPreviewPath);
        Check(new FileInfo(SystemPreviewPath).Length > 20_000, $"drew System › Controls and System › Shortcuts to {SystemPreviewPath}");
    }

    private async Task TestTimer()
    {
        // Typing: "t 2h", "t 45m", "t 1h30", "t 1:15".
        var cases = new (string Text, TimeSpan? Expected)[]
        {
            ("2h", TimeSpan.FromHours(2)), ("45m", TimeSpan.FromMinutes(45)), ("1h30", TimeSpan.FromMinutes(90)),
            ("1:15", TimeSpan.FromMinutes(75)), ("2 hours", TimeSpan.FromHours(2)), ("90 min", TimeSpan.FromMinutes(90)), ("banana", null)
        };
        var wrong = cases.Where(c => DesktopOrbit.Ring.RingWindow.ParseDuration(c.Text) != c.Expected).Select(c => c.Text).ToList();
        Check(wrong.Count == 0, wrong.Count == 0 ? "understands t 2h, t 45m, t 1h30, t 1:15, t 2 hours, t 90 min" : $"misread: {string.Join(", ", wrong)}");

        var minutesBefore = settings.TimerMinutes;
        var radioWasPlaying = radio.State != RadioState.Stopped;
        timer.QuietForTest = true;
        var finished = false;
        void OnFinished() => finished = true;
        timer.Finished += OnFinished;
        try
        {
            // Pause keeps the time left; start carries on.
            timer.SetDuration(TimeSpan.FromMinutes(120));
            timer.Start();
            await Wait(1200);
            timer.Pause();
            var paused = timer.Remaining;
            await Wait(1200);
            Check(timer.IsPaused && timer.Remaining == paused && paused < TimeSpan.FromMinutes(120),
                $"2-hour timer counts down and pauses at {FocusTimer.Format(paused)}");
            timer.Cancel();

            // A 3-second timer must ring with the chime, and the button must stop it.
            timer.StartForTest(TimeSpan.FromSeconds(3));
            var watch = Stopwatch.StartNew();
            while (!finished && watch.ElapsedMilliseconds < 8000)
            {
                await Wait(100);
            }

            float peak = 0;
            for (var i = 0; i < 12; i++)
            {
                await Wait(150);
                peak = Math.Max(peak, AudioControl.Speakers.Peak ?? 0);
            }

            Check(finished && timer.IsRinging, $"timer finished after {watch.Elapsed.TotalSeconds:0.0} s and is ringing");
            Check(radioWasPlaying || peak > 0.005, $"the chime is heard on the speakers (peak {peak:0.000})");
            var message = timer.Press();
            Check(!timer.IsRinging && !timer.IsRunning, $"pressing the button stops the ringing (\"{message}\")");
        }
        finally
        {
            timer.Finished -= OnFinished;
            timer.QuietForTest = false;
            timer.Cancel();
            settings.TimerMinutes = minutesBefore;
            settings.Save();
        }
    }

    private async Task TestWatch()
    {
        var recentBefore = settings.RadioRecent.ToList();
        var faceBefore = settings.ClockFace;
        var radioVolumeBefore = radio.Volume;
        radio.Volume = 30;
        try
        {
            if (!ring.HasStationToResume)
            {
                await radio.GetTopAsync("MT");
            }

            // Play button: starts the last station and the watch switches to the radio face.
            var face = ring.PressRadioButton();
            var watch = Stopwatch.StartNew();
            while (radio.State != RadioState.Playing && watch.ElapsedMilliseconds < 12000)
            {
                await Wait(100);
            }

            Check(radio.State == RadioState.Playing && face == "Radio",
                $"play button started {radio.Current?.DisplayName} in {watch.ElapsedMilliseconds} ms and the watch shows the {face} face");

            await Wait(1500); // let the music lights fill up
            ring.RenderWatchPreview(WatchPreviewPath, includeLights: true);
            Check(new FileInfo(WatchPreviewPath).Length > 20_000, $"drew all watch faces and colours to {WatchPreviewPath}");

            // Stop button.
            ring.PressRadioButton();
            await Wait(300);
            Check(radio.State == RadioState.Stopped, "stop button stops the radio");

            // Clicks must land on the watch (not on something covering it), and each click flips to the next watch.
            var spots = new (string Where, double X, double Y, string Expected)[]
            {
                ("centre", 0, 0, "watch"), ("upper middle", 0, -40, "watch"), ("left side", -45, -10, "watch"),
                ("right side", 45, 10, "watch"), ("‹ arrow", -(75 + 7), 0, "previous arrow"), ("› arrow", 75 + 7, 0, "next arrow")
            };
            var misses = new List<string>();
            foreach (var spot in spots)
            {
                var got = ring.HitTestWatch(spot.X, spot.Y);
                if (got != spot.Expected)
                {
                    misses.Add($"{spot.Where} → {got}");
                }
            }

            Check(misses.Count == 0, misses.Count == 0
                ? "clicks reach the watch at the centre, top and both sides, and the ‹ › arrows"
                : $"clicks blocked: {string.Join(", ", misses)}");

            var flips = new List<string>();
            for (var i = 0; i < 10; i++)
            {
                ring.ClickWatchForTest();
                await Wait(400); // let the flip animation finish
                flips.Add(ring.CurrentWatch);
            }

            Check(flips.Distinct().Count() == 10, $"10 clicks flip through every watch: {string.Join(" → ", flips)}");
        }
        finally
        {
            if (radio.State != RadioState.Stopped)
            {
                radio.Stop();
            }

            radio.Volume = radioVolumeBefore;
            settings.RadioRecent = recentBefore;
            settings.ClockFace = faceBefore;
            settings.Save();
        }
    }

    private async Task TestVolume()
    {
        foreach (var (name, audio) in new[] { ("Speakers", AudioControl.Speakers), ("Microphone", AudioControl.Microphone) })
        {
            var before = audio.Level;
            var muted = audio.IsMuted;
            if (before is null)
            {
                Fail($"{name}: no device found");
                continue;
            }

            var step = before <= 90 ? 4 : -4;
            audio.Change(step);
            await Wait(150);
            var after = audio.Level;
            audio.Change(-step);
            await Wait(150);
            var restored = audio.Level;
            if (audio.IsMuted != muted)
            {
                audio.ToggleMute(); // raising volume unmutes; put mute back as it was
            }

            audio.ToggleMute();
            await Wait(100);
            var toggled = audio.IsMuted;
            audio.ToggleMute();
            await Wait(100);

            Check(after == before + step && restored == before && toggled != muted && audio.IsMuted == muted,
                $"{name}: {before}% → {after}% → {restored}%, mute switches and is back to {(muted ? "muted" : "on")}");
        }
    }

    private async Task TestBrightness()
    {
        BrightnessControl.Warm();
        for (var i = 0; i < 40 && BrightnessControl.Status == "…"; i++)
        {
            await Wait(200);
        }

        if (BrightnessControl.Status == "n/a")
        {
            Fail("the monitor doesn't answer brightness requests (DDC/CI is off in the monitor's own menu, or it isn't supported)");
            return;
        }

        var before = await Task.Run(BrightnessControl.ReadNow);
        var step = before is <= 80 ? 10 : -10;
        BrightnessControl.Change(step);
        await Wait(1500);
        var after = await Task.Run(BrightnessControl.ReadNow);
        BrightnessControl.Change(-step);
        await Wait(1500);
        var restored = await Task.Run(BrightnessControl.ReadNow);
        Check(before is not null && after is not null && Math.Abs(after.Value - (before.Value + step)) <= 2 && restored is not null && Math.Abs(restored.Value - before.Value) <= 2,
            $"monitor brightness {before}% → {after}% → {restored}%");
    }

    private async Task TestReadAloud()
    {
        var message = ReadAloud.Toggle("Orbital Launcher test.");
        var spoke = false;
        float peak = 0;
        for (var i = 0; i < 15; i++)
        {
            await Wait(150);
            spoke |= ReadAloud.IsSpeaking;
            peak = Math.Max(peak, AudioControl.Speakers.Peak ?? 0);
        }

        if (ReadAloud.IsSpeaking)
        {
            ReadAloud.Toggle(null);
        }

        Check(spoke && peak > 0.005, $"Windows voice spoke a test phrase ({message}) · speaker peak {peak:0.000}");
        Info($"voices installed: {string.Join(", ", ReadAloud.Voices())}");
    }

    private Task TestScreenshot()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OrbitalLauncherTest");
        var path = SystemActions.CaptureDesktop(folder, copyToClipboard: false);
        var size = new FileInfo(path).Length;
        Check(size > 50_000, $"whole-desktop screenshot saved ({size / 1024:N0} KB, test copy deleted)");
        File.Delete(path);
        return Task.CompletedTask;
    }

    private Task TestSnip()
    {
        using var clip = Registry.ClassesRoot.OpenSubKey("ms-screenclip");
        Check(clip is not null, "area snip (ms-screenclip) is available");
        var snipping = catalog.Find("snipping tool");
        Check(snipping is not null, "Snipping Tool is installed (screen recording uses it)");
        return Task.CompletedTask;
    }

    private Task TestIntegration()
    {
        using var command = Registry.CurrentUser.OpenSubKey(@"Software\Classes\DesktopBackground\Shell\OrbitalLauncher\shell\01ring\command");
        var value = command?.GetValue(string.Empty) as string ?? string.Empty;
        Check(value.Contains(Environment.ProcessPath ?? "?", StringComparison.OrdinalIgnoreCase), $"desktop right-click entry points at this program ({value})");
        Check(!hotkeyLabel.StartsWith("no hotkey"), $"hotkey registered: {hotkeyLabel}");
        return Task.CompletedTask;
    }
}

