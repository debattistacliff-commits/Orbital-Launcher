using System.IO;
using System.Windows;
using System.Windows.Input;
using DesktopOrbit.ShellIntegration;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using WpfPoint = System.Windows.Point;

namespace DesktopOrbit.Ring;

/// <summary>
/// Adding your own things to the ring: drag a shortcut, program, file or folder onto a section (Apps, Files or Social),
/// or copy it (Ctrl+C) and press Ctrl+V in the ring. Right-click an added bubble to remove it.
/// </summary>
public sealed partial class RingWindow
{
    private static readonly string[] PinSections = ["apps", "files", "web"];
    private int _dropSector = -1;

    private void SetUpDropAndPaste()
    {
        AllowDrop = true;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => EndDragHighlight();
        Drop += (_, e) =>
        {
            var section = SectionAt(e.GetPosition(_root));
            _dropCount++;
            Activate(); // come back to the front after a drop from the desktop
            EndDragHighlight();
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                Flash(AddPins(paths, section));
            }

            e.Handled = true;
        };
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Link;
        var section = SectionAt(e.GetPosition(_root));
        var index = SectorIndex(section);
        if (index != _dropSector)
        {
            _dropSector = index;
            _hoverSector = index;
            PaintSectors();
        }

        SetHint($"Let go to pin it to {Sectors[index].Label.ToLowerInvariant() switch { "social" => "Social", "files" => "Files", _ => "Apps" }}");
        e.Handled = true;
    }

    private void EndDragHighlight()
    {
        _dropSector = -1;
        _hoverSector = -1;
        PaintSectors();
        UpdateHub();
    }

    /// <summary>Apps, Files or Social under the mouse; anywhere else counts as Apps (or the open section if that's one of them).</summary>
    private string SectionAt(WpfPoint point)
    {
        var dx = point.X - _center.X;
        var dy = point.Y - _center.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > HubDark && distance <= Radius)
        {
            var degrees = Math.Atan2(dy, dx) * 180 / Math.PI;
            var index = (int)Math.Floor(((degrees + 120 + 360) % 360) / 60); // sectors start at -120°
            var name = Sectors[index].Name;
            return PinSections.Contains(name) ? name : "apps";
        }

        var open = Sectors[_sector].Name;
        return PinSections.Contains(open) ? open : "apps";
    }

    /// <summary>Adds dropped or pasted paths to a section. Returns a message for the ring.</summary>
    public string AddPins(IEnumerable<string> paths, string section)
    {
        var added = new List<string>();
        var skipped = new List<string>();
        foreach (var path in paths.Where(p => File.Exists(p) || Directory.Exists(p)))
        {
            var name = Directory.Exists(path) ? new DirectoryInfo(path).Name : System.IO.Path.GetFileNameWithoutExtension(path);

            // An app already known from the Start menu is pinned as itself (keeps its icon, all its versions, open/close).
            if (section == "apps" && _catalog.Find(AppCatalog.KeyFor(name)) is { } group)
            {
                var favorites = _settings.FavoriteApps ??= [];
                if (favorites.Contains(group.Key))
                {
                    skipped.Add($"{name} (already there)");
                }
                else if (PinCount("apps") >= LimitFor("apps"))
                {
                    skipped.Add($"{name} (Apps is full)");
                }
                else
                {
                    favorites.Add(group.Key);
                    added.Add(group.Name);
                }

                continue;
            }

            if (_settings.CustomPins.Any(p => p.Section == section && p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                skipped.Add($"{name} (already there)");
                continue;
            }

            if (PinCount(section) >= LimitFor(section))
            {
                skipped.Add($"{name} ({SectionTitle(section)} is full)");
                continue;
            }

            _settings.CustomPins.Add(new CustomPin { Path = path, Name = name, Section = section, Target = ProgramBehind(path) });
            added.Add(name);
        }

        _settings.Save();
        _sector = SectorIndex(section);
        _socialView = null;
        _systemView = null;
        _query = string.Empty;
        Refresh();

        var message = added.Count == 0 ? "Nothing pinned" : $"Pinned {string.Join(", ", added)} to {SectionTitle(section)} · right-click it to unpin";
        return skipped.Count == 0 ? message : $"{message} · skipped {string.Join(", ", skipped)}";
    }

    private int PinCount(string section) =>
        _settings.CustomPins.Count(p => p.Section == section) + (section == "apps" ? (_settings.FavoriteApps?.Count ?? 0) : 0);

    private static string SectionTitle(string section) => section switch { "files" => "Files", "web" => "Social", _ => "Apps" };

    /// <summary>How many you can pin: Apps pages through 14; Files and Social share their bubbles with what's already there.</summary>
    private static int LimitFor(string section) => section switch { "files" => 8, "web" => 5, _ => RingSettings.MaxFavorites };

    private int _appsPage; // 0 = first apps, 1 = "More"

    /// <summary>For a shortcut, the program it starts; for a program, itself. Used to find its open windows.</summary>
    private static string ProgramBehind(string path)
    {
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return string.Empty;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(path);
            string target = shortcut.TargetPath ?? string.Empty;
            return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Bubbles for what you added to a section.</summary>
    private List<RingItem> CustomPinItems(string section) =>
        _settings.CustomPins.Where(p => p.Section == section).Select(CustomPinItem).ToList();

    private RingItem CustomPinItem(CustomPin pin)
    {
        var exists = File.Exists(pin.Path) || Directory.Exists(pin.Path);
        return new RingItem
        {
            Title = pin.Name,
            IconPath = exists ? pin.Path : null,
            Glyph = Glyphs.Files,
            Badge = exists ? null : "missing",
            Subtitle = pin.Path,
            CountKey = "pin:" + pin.Path,
            Hint = exists ? "Pinned by you · right-click: unpin" : "This file was moved or deleted · right-click: unpin",
            Activate = () =>
            {
                if (File.Exists(pin.Path) || Directory.Exists(pin.Path))
                {
                    SystemActions.Open(pin.Path);
                }
                else
                {
                    _notify($"{pin.Name} was moved or deleted. Right-click its bubble to remove it.");
                }
            },
            OpenWindows = pin.Target.Length == 0 ? null
                : windows => windows.Where(w => w.ExePath.Equals(pin.Target, StringComparison.OrdinalIgnoreCase)).ToList(),
            Secondary = () =>
            {
                _settings.CustomPins.Remove(pin);
                _settings.Save();
                Refresh();
                return $"Unpinned {pin.Name} from {SectionTitle(pin.Section)}";
            }
        };
    }

    /// <summary>After the mouse is let go: close the ring unless the drag ended on it (a drop) or you came back to it.</summary>
    private void WaitForDragToEnd()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        var dropsBefore = _dropCount;
        timer.Tick += (_, _) =>
        {
            if (DropPinsHelpers.LeftButtonDown())
            {
                return;
            }

            timer.Stop();
            if (!IsActive && _dropCount == dropsBefore && !_settings.Pinned)
            {
                HideRing();
            }
        };
        timer.Start();
    }

    private int _dropCount;

    /// <summary>Self-test: the section a drop lands in at an angle around the ring (0° = right, 90° = down).</summary>
    public string SectionAtAngleForTest(double degrees)
    {
        var at = Polar(_center, (HubDark + Radius) / 2, degrees);
        return SectionAt(at);
    }

    /// <summary>Self-test: the titles a section shows.</summary>
    public IReadOnlyList<string> TitlesInForTest(string section)
    {
        if (section != "apps")
        {
            return ItemsFor(section).Select(i => i.Title).ToList();
        }

        // Apps may page: look at both pages.
        var page = _appsPage;
        _appsPage = 0;
        var titles = ItemsFor("apps").Select(i => i.Title).ToList();
        _appsPage = 1;
        titles.AddRange(ItemsFor("apps").Select(i => i.Title));
        _appsPage = page;
        return titles;
    }

    /// <summary>Self-test: unpins whatever was pinned from these paths.</summary>
    public void UnpinForTest(IEnumerable<string> paths)
    {
        var set = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _settings.CustomPins.RemoveAll(p => set.Contains(p.Path));
        _settings.Save();
    }

    /// <summary>Ctrl+V in the ring: adds copied files and shortcuts to the open section.</summary>
    private bool PasteFromClipboard()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsFileDropList())
            {
                Flash("Copy a shortcut, program, file or folder first (select it, Ctrl+C), then Ctrl+V here");
                return true;
            }

            var paths = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList();
            var open = Sectors[_sector].Name;
            Flash(AddPins(paths, PinSections.Contains(open) ? open : "apps"));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Flash("Couldn't read the clipboard, try again");
        }

        return true;
    }
}

internal static class DropPinsHelpers
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>True while the left mouse button is held down (honours swapped buttons).</summary>
    public static bool LeftButtonDown()
    {
        var key = System.Windows.Forms.SystemInformation.MouseButtonsSwapped ? 0x02 : 0x01;
        return (GetAsyncKeyState(key) & 0x8000) != 0;
    }
}
