using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DesktopOrbit.ShellIntegration;

/// <summary>One signed-in browser profile, e.g. Chrome "Jane Smith" or Edge "Work".</summary>
public sealed record BrowserProfile(string Browser, string Folder, string Name, string Account, string? PicturePath, string Executable, string ShortcutName = "")
{
    public string Key => $"{Browser}:{Folder}";

    /// <summary>The account name if signed in, otherwise the profile's own name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Account) ? Name : Account;

    public void Open(string? url = null)
    {
        var arguments = $"--profile-directory=\"{Folder}\"" + (url is null ? string.Empty : $" \"{url}\"");
        Process.Start(new ProcessStartInfo(Executable, arguments) { UseShellExecute = true });
    }
}

/// <summary>Reads Chrome and Edge profiles (names, accounts and pictures) from each browser's "Local State" file.</summary>
public static class BrowserProfiles
{
    private static readonly (string Browser, string DataFolder, string ExeName, string[] Fallbacks)[] Browsers =
    [
        ("Chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data"), "chrome.exe",
            [@"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"]),
        ("Edge", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\User Data"), "msedge.exe",
            [@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"])
    ];

    private static IReadOnlyList<BrowserProfile>? _cache;
    private static DateTime _readAt;

    /// <summary>All profiles, re-read at most once a minute (new profiles show up without restarting).</summary>
    public static IReadOnlyList<BrowserProfile> All()
    {
        if (_cache is not null && DateTime.Now - _readAt < TimeSpan.FromMinutes(1))
        {
            return _cache;
        }

        var profiles = new List<BrowserProfile>();
        foreach (var (browser, dataFolder, exeName, fallbacks) in Browsers)
        {
            var exe = FindExecutable(exeName, fallbacks);
            var localState = Path.Combine(dataFolder, "Local State");
            if (exe is null || !File.Exists(localState))
            {
                continue;
            }

            try
            {
                // The browser keeps the file open; read it shared.
                using var stream = new FileStream(localState, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream);
                if (!document.RootElement.TryGetProperty("profile", out var profile) || !profile.TryGetProperty("info_cache", out var cache))
                {
                    continue;
                }

                foreach (var entry in cache.EnumerateObject())
                {
                    var folder = entry.Name;
                    var name = entry.Value.TryGetProperty("name", out var n) ? n.GetString() ?? folder : folder;
                    var account = entry.Value.TryGetProperty("gaia_name", out var g) ? g.GetString() ?? string.Empty : string.Empty;
                    var shortcut = entry.Value.TryGetProperty("shortcut_name", out var s) ? s.GetString() ?? string.Empty : string.Empty; // Edge shows this ("Personal")
                    string? picture = null;
                    foreach (var file in new[] { "Google Profile Picture.png", "Edge Profile Picture.png" })
                    {
                        var candidate = Path.Combine(dataFolder, folder, file);
                        if (File.Exists(candidate))
                        {
                            picture = candidate;
                            break;
                        }
                    }

                    profiles.Add(new BrowserProfile(browser, folder, name, account, picture, exe, shortcut));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        _readAt = DateTime.Now;
        return _cache = profiles;
    }

    public static BrowserProfile? Find(string key) => All().FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static readonly Dictionary<string, Task<ImageSource?>> Pictures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The profile picture, loaded once and kept.</summary>
    public static Task<ImageSource?> PictureAsync(BrowserProfile profile)
    {
        lock (Pictures)
        {
            if (!Pictures.TryGetValue(profile.Key, out var task))
            {
                task = LoadPictureAsync(profile);
                Pictures[profile.Key] = task;
            }

            return task;
        }
    }

    private static Task<ImageSource?> LoadPictureAsync(BrowserProfile profile) => Task.Run<ImageSource?>(() =>
    {
        if (profile.PicturePath is null)
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(profile.PicturePath);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 72;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    });

    private static string? FindExecutable(string exeName, string[] fallbacks)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            if (key?.GetValue(string.Empty) is string path && File.Exists(path))
            {
                return path;
            }
        }

        return fallbacks.FirstOrDefault(File.Exists);
    }
}
