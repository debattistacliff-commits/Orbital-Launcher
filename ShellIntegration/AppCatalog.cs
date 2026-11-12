using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace DesktopOrbit.ShellIntegration;

public enum AppSource
{
    Desktop,
    Store,
    EdgeWebApp,
    ChromeWebApp,
    Shortcut,
    Website
}

/// <summary>One way of starting an app: a Start menu entry, a Store app, a web app, or a desktop shortcut.</summary>
public sealed class AppEntry
{
    public required string Name { get; init; }

    /// <summary>The AppsFolder id (AppUserModelID or known-folder path), or a file path for desktop shortcuts.</summary>
    public required string Id { get; init; }

    public required AppSource Source { get; init; }

    /// <summary>The program this app really runs (from its shortcut), used to find its open windows. Null if unknown.</summary>
    public string? TargetExe { get; init; }

    public bool IsAppsFolderItem => Source != AppSource.Shortcut;

    public string ShellPath => IsAppsFolderItem ? $@"shell:AppsFolder\{Id}" : Id;

    public string SourceLabel => Source switch
    {
        AppSource.Store => "Store app",
        AppSource.EdgeWebApp => "Edge web app",
        AppSource.ChromeWebApp => "Chrome web app",
        AppSource.Shortcut => "Desktop shortcut",
        AppSource.Website => "Website",
        _ => "Desktop app"
    };

    public string ShortSourceLabel => Source switch
    {
        AppSource.Store => "Store",
        AppSource.EdgeWebApp => "Edge",
        AppSource.ChromeWebApp => "Chrome",
        _ => string.Empty
    };

    public void Launch()
    {
        if (IsAppsFolderItem)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", ShellPath) { UseShellExecute = true });
        }
        else
        {
            Process.Start(new ProcessStartInfo(Id) { UseShellExecute = true });
        }
    }
}

/// <summary>All the ways of opening one app, grouped by name (e.g. Facebook from the Store and from Edge).</summary>
public sealed class AppGroup
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required List<AppEntry> Variants { get; init; }

    public AppEntry Primary => Variants[0];

    public bool IsSocial => AppCatalog.SocialPattern.IsMatch(Name);
}

/// <summary>
/// Reads every app the Start menu knows about (normal programs, Store apps, Edge and Chrome web apps)
/// through the shell's AppsFolder, plus shortcuts lying on the desktop.
/// </summary>
public sealed class AppCatalog
{
    internal static readonly Regex SocialPattern = new(
        @"\b(facebook|messenger|instagram|tiktok|whatsapp|telegram|discord|youtube|twitter|linkedin|snapchat|reddit|pinterest|threads|twitch|signal|bluesky)\b|^x$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DuplicateSuffix = new(@"\s*\(\d+\)$", RegexOptions.Compiled);
    private static readonly Regex Noise = new(@"^(uninstall|readme|release notes|license|help|documentation)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // When an app exists in several forms, open this one by default.
    private static readonly AppSource[] Preference =
        [AppSource.EdgeWebApp, AppSource.Store, AppSource.Desktop, AppSource.ChromeWebApp, AppSource.Shortcut, AppSource.Website];

    private IReadOnlyList<AppGroup> _groups = [];

    public event EventHandler? Loaded;

    public IReadOnlyList<AppGroup> Groups => _groups;

    public bool IsLoaded { get; private set; }

    public static string KeyFor(string name) => DuplicateSuffix.Replace(name.Trim(), string.Empty).ToLowerInvariant();

    public AppGroup? Find(string key) => _groups.FirstOrDefault(group => group.Key == key);

    public Task LoadAsync()
    {
        var completion = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                _groups = Group(ReadAppsFolder().Concat(ReadDesktopShortcuts()));
                IsLoaded = true;
                completion.SetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                IsLoaded = true;
                completion.SetResult();
            }
        })
        { IsBackground = true, Name = "AppCatalog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.ContinueWith(_ => Loaded?.Invoke(this, EventArgs.Empty), TaskScheduler.Default);
    }

    private static IEnumerable<AppEntry> ReadAppsFolder()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            yield break;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        if (folder is null)
        {
            yield break;
        }

        foreach (dynamic item in folder.Items())
        {
            string name = item.Name ?? string.Empty;
            string id = item.Path ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || Noise.IsMatch(name))
            {
                continue;
            }

            // Start menu entries made from a shortcut know the program behind it (e.g. an app id like "com.example.app" runs "Example App.exe").
            string? target = null;
            try
            {
                target = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
            }
            catch (Exception)
            {
            }

            yield return new AppEntry { Name = name, Id = id, Source = Classify(id), TargetExe = ExeOrNull(target) };
        }
    }

    private static AppSource Classify(string id)
    {
        if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return AppSource.Website;
        }

        if (id.Contains("_crx_", StringComparison.OrdinalIgnoreCase))
        {
            return id.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase) ? AppSource.EdgeWebApp : AppSource.ChromeWebApp;
        }

        if (id.Contains('!'))
        {
            // Sites installed as apps from Edge are packaged as "www.site.com-XXXX_hash!App".
            return id.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? AppSource.EdgeWebApp : AppSource.Store;
        }

        return AppSource.Desktop;
    }

    private static IEnumerable<AppEntry> ReadDesktopShortcuts()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        ];

        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root).ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new AppEntry { Name = Path.GetFileNameWithoutExtension(file), Id = file, Source = AppSource.Shortcut, TargetExe = ExeOrNull(ShortcutTarget(file)) };
                }
            }
        }
    }

    private static string? ExeOrNull(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;

    /// <summary>The file a .lnk shortcut points to.</summary>
    private static string? ShortcutTarget(string file)
    {
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            return shell.CreateShortcut(file).TargetPath as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<AppGroup> Group(IEnumerable<AppEntry> entries)
    {
        return entries
            .GroupBy(entry => KeyFor(entry.Name))
            .Select(group =>
            {
                var variants = group
                    .DistinctBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(entry => Array.IndexOf(Preference, entry.Source))
                    .ThenBy(entry => entry.Name.Length)
                    .ToList();

                // A desktop shortcut that duplicates a Start menu app adds nothing.
                if (variants.Any(v => v.Source != AppSource.Shortcut))
                {
                    variants.RemoveAll(v => v.Source == AppSource.Shortcut);
                }

                return new AppGroup { Key = group.Key, Name = DuplicateSuffix.Replace(variants[0].Name, string.Empty), Variants = variants };
            })
            .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
