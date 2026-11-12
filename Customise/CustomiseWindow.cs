using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopOrbit.Ring;
using DesktopOrbit.ShellIntegration;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace DesktopOrbit.Customise;

/// <summary>
/// One place to make the ring your own: look (theme, watch, background), places, radio countries, browser accounts,
/// hotkey and start-up, and saving / loading your whole setup. "Save and apply" restarts the ring with the changes.
/// </summary>
public sealed class CustomiseWindow : Window
{
    private readonly RingSettings _settings;
    private readonly Action _apply;
    private readonly ContentControl _page = new();
    private readonly WorldService _world = new();
    private readonly Brush _accent = new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x14));

    public CustomiseWindow(RingSettings settings, Action apply)
    {
        _settings = settings;
        _apply = apply;
        Title = "Customise Orbital Launcher";
        Width = 860;
        Height = 640;
        MinWidth = 700;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9));

        var layout = new DockPanel();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
        bottom.Children.Add(MakeButton("Close", Close, primary: false));
        bottom.Children.Add(MakeButton("Save and apply", () => { _settings.Save(); _apply(); }, primary: true));
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(bottom);

        var nav = new StackPanel { Width = 190, Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEE, 0xF2)) };
        nav.Children.Add(new TextBlock { Text = "Customise", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(18, 18, 18, 14) });
        foreach (var (name, build) in new (string, Func<UIElement>)[]
                 {
                     ("Look", LookPage), ("Places", PlacesPage), ("Radio", RadioPage), ("Accounts", AccountsPage), ("Start-up", StartupPage), ("My setup", SetupPage)
                 })
        {
            var item = new Button
            {
                Content = name,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(18, 9, 18, 9),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 15,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            item.Click += (_, _) =>
            {
                foreach (var other in nav.Children.OfType<Button>())
                {
                    other.FontWeight = FontWeights.Normal;
                }

                item.FontWeight = FontWeights.SemiBold;
                _page.Content = new ScrollViewer { Content = build(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(26, 20, 26, 10) };
            };
            nav.Children.Add(item);
        }

        DockPanel.SetDock(nav, Dock.Left);
        layout.Children.Add(nav);
        layout.Children.Add(_page);
        Content = layout;
        Loaded += (_, _) => (nav.Children.OfType<Button>().First()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    // ---------------------------------------------------------------- pages

    private UIElement LookPage()
    {
        var page = Page("Look", "Choose the theme for the whole ring, and your watch.");
        page.Children.Add(Heading("Theme"));
        var grid = new WrapPanel();
        foreach (var theme in Theme.All())
        {
            grid.Children.Add(ThemeCard(theme));
        }

        page.Children.Add(grid);
        var themeButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        themeButtons.Children.Add(MakeButton("Load a theme file…", ImportTheme, primary: false));
        themeButtons.Children.Add(MakeButton("Open my themes folder", () =>
        {
            Directory.CreateDirectory(Theme.ThemesFolder);
            SystemActions.OpenFolder(Theme.ThemesFolder);
        }, primary: false));
        page.Children.Add(themeButtons);
        page.Children.Add(Note("Make your own: copy any theme file in that folder, change its colours, and it appears here."));

        page.Children.Add(Heading("Watch"));
        page.Children.Add(Labelled("Watch you see first", Choice(RingWindow.WatchFaceNames, _settings.ClockFace, i => _settings.ClockFace = i)));
        var background = Choice(RingWindow.WatchBackgroundNames, _settings.ClockColour, i => _settings.ClockColour = i);
        page.Children.Add(Labelled("Watch background (colour or flag)", background));
        page.Children.Add(Note("On the ring: click the watch or ‹ › to change watch, click the small swatch to change background."));

        page.Children.Add(Heading("Your flag"));
        page.Children.Add(Note("Put your own country's flag on the watch. Every country is here: type to find yours. Or use any picture you like."));
        page.Children.Add(MyFlagPicker(background));
        return page;
    }

    /// <summary>
    /// Choose any country's flag (or your own picture) for the watch. It's added after the built-in flags
    /// and chosen as the watch background straight away.
    /// </summary>
    private ComboBox? _myFlagBox;
    private ComboBox? _backgroundBox;
    private Func<string, string?>? _usePicture;

    /// <summary>Self-test: builds the Look page and does what a person does in "Your flag".</summary>
    public (string? Saved, string? Shown, string? Background) YourFlagForTest(string? pick = null, string? typed = null, string? picture = null)
    {
        if (_myFlagBox is null)
        {
            LookPage();
        }

        if (pick is not null)
        {
            _myFlagBox!.SelectedItem = pick;
        }

        if (typed is not null)
        {
            _myFlagBox!.Text = typed; // typing into the box picks the matching country
        }

        if (picture is not null)
        {
            _usePicture!(picture);
        }

        return (_settings.MyFlag, _myFlagBox!.SelectedItem as string, _backgroundBox!.SelectedItem as string);
    }

    private UIElement MyFlagPicker(ComboBox background)
    {
        const string none = "None";
        const string picture = "My picture";
        var names = new List<string> { none };
        names.AddRange(RingWindow.Countries.Select(c => c.Name));
        if (!string.IsNullOrEmpty(_settings.MyFlag) && !_settings.MyFlag.StartsWith("flag:", StringComparison.Ordinal))
        {
            names.Insert(1, picture);
        }

        var countries = new ComboBox
        {
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = names,
            IsEditable = true,
            IsTextSearchEnabled = true,
            SelectedItem = string.IsNullOrEmpty(_settings.MyFlag) ? none : RingWindow.MyFlagName
        };

        void Use(string? flag)
        {
            _settings.MyFlag = flag;
            RingWindow.MyFlag = flag;
            background.ItemsSource = RingWindow.WatchBackgroundNames;
            var mine = RingWindow.MyFlagIndex;
            _settings.ClockColour = mine >= 0 ? mine : Math.Min(_settings.ClockColour, RingWindow.WatchBackgroundNames.Count - 1);
            background.SelectedIndex = _settings.ClockColour;
        }

        countries.SelectionChanged += (_, _) =>
        {
            switch (countries.SelectedItem as string)
            {
                case null:
                    return;
                case none:
                    Use(null);
                    break;
                case picture:
                    break;
                case var name:
                    var code = RingWindow.Countries.FirstOrDefault(c => c.Name == name).Code;
                    if (code is not null)
                    {
                        Use("flag:" + code);
                    }

                    break;
            }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(countries);
        _usePicture = file =>
        {
            // Keep a copy, so the watch still has it if the original is moved or deleted.
            var copy = Path.Combine(Path.GetDirectoryName(RingSettings.SettingsPath)!, "my-flag" + Path.GetExtension(file).ToLowerInvariant());
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy, overwrite: true);
            }
            catch (IOException)
            {
                return null;
            }

            if (!names.Contains(picture))
            {
                names.Insert(1, picture);
                countries.ItemsSource = null;
                countries.ItemsSource = names;
            }

            Use(copy);
            countries.SelectedItem = picture;
            return copy;
        };
        _myFlagBox = countries;
        _backgroundBox = background;

        var own = MakeButton("Use my own picture…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a picture for your watch",
                Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif"
            };
            if (dialog.ShowDialog(this) == true)
            {
                _usePicture(dialog.FileName);
            }
        }, primary: false);
        own.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(own);
        return row;
    }

    private UIElement ThemeCard(Theme theme)
    {
        var selected = theme.Name.Equals(_settings.ThemeName, StringComparison.OrdinalIgnoreCase);
        var card = new Button
        {
            Width = 172,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(12),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.White,
            BorderBrush = selected ? _accent : new SolidColorBrush(Color.FromRgb(0xD6, 0xDA, 0xE0)),
            BorderThickness = new Thickness(selected ? 2.5 : 1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var stack = new StackPanel();
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var colour in new[] { theme.Base, theme.Active, theme.Accent, theme.Ink })
        {
            swatches.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Theme.Parse(colour)), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5)
            });
        }

        stack.Children.Add(swatches);
        stack.Children.Add(new TextBlock { Text = theme.Name + (selected ? "  ✓" : string.Empty), FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = theme.Description, FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        card.Content = stack;
        card.Click += (_, _) =>
        {
            _settings.ThemeName = theme.Name;
            _settings.ClockColour = theme.WatchColour; // a watch colour that suits it (changeable below)
            _page.Content = new ScrollViewer { Content = LookPage(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(26, 20, 26, 10) };
        };
        return card;
    }

    private void ImportTheme()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Theme files (*.json)|*.json", Title = "Load a theme file" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var theme = Theme.Import(dialog.FileName);
            _settings.ThemeName = theme.Name;
            _page.Content = new ScrollViewer { Content = LookPage(), Padding = new Thickness(26, 20, 26, 10) };
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"That file isn't a theme: {ex.Message}", "Load a theme", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private UIElement PlacesPage()
    {
        var page = Page("Places", "Cities for the World and Weather watches. The first one is shown on the Weather watch.");
        var list = new StackPanel();
        void Fill()
        {
            list.Children.Clear();
            foreach (var place in _settings.Places.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var remove = MakeButton("Remove", () => { _settings.Places.Remove(place); Fill(); }, primary: false);
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                var up = MakeButton("Move up", () =>
                {
                    var i = _settings.Places.IndexOf(place);
                    if (i > 0)
                    {
                        _settings.Places.RemoveAt(i);
                        _settings.Places.Insert(i - 1, place);
                        Fill();
                    }
                }, primary: false);
                DockPanel.SetDock(up, Dock.Right);
                row.Children.Add(up);
                row.Children.Add(new TextBlock { Text = $"{place.Name}  ·  {place.Country}  ·  {WorldService.TimeIn(place):HH:mm} now", VerticalAlignment = VerticalAlignment.Center });
                list.Children.Add(row);
            }

            if (_settings.Places.Count == 0)
            {
                list.Children.Add(Note("No places yet. Find one below."));
            }
        }

        Fill();
        page.Children.Add(list);
        page.Children.Add(Heading("Add a place"));
        var search = new TextBox { Width = 260, Padding = new Thickness(6), Margin = new Thickness(0, 0, 8, 0) };
        var results = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        async void Find()
        {
            results.Children.Clear();
            results.Children.Add(Note("Looking…"));
            var found = await _world.FindAsync(search.Text, CancellationToken.None);
            results.Children.Clear();
            foreach (var place in found)
            {
                results.Children.Add(MakeButton($"Add {place.Name}, {place.Country}", () =>
                {
                    if (_settings.Places.All(p => p.Key != place.Key) && _settings.Places.Count < 6)
                    {
                        _settings.Places.Add(place);
                    }

                    results.Children.Clear();
                    search.Text = string.Empty;
                    Fill();
                }, primary: false));
            }

            if (found.Count == 0)
            {
                results.Children.Add(Note("No city found with that name."));
            }
        }

        search.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Find(); };
        var row2 = new StackPanel { Orientation = Orientation.Horizontal };
        row2.Children.Add(search);
        row2.Children.Add(MakeButton("Find", Find, primary: false));
        page.Children.Add(row2);
        page.Children.Add(results);
        page.Children.Add(Note("Up to 6 places. On the ring you can also type \"w paris\"."));
        return page;
    }

    private UIElement RadioPage()
    {
        var page = Page("Radio", "Countries offered in the Radio section (World is always there).");
        var box = new TextBox { Text = string.Join(" ", _settings.RadioCountries), Width = 300, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left };
        var names = Note(Describe(_settings.RadioCountries));
        box.TextChanged += (_, _) =>
        {
            var codes = box.Text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim().ToUpperInvariant())
                .Where(c => c.Length == 2 && IsCountry(c))
                .Distinct()
                .Take(5)
                .ToList();
            _settings.RadioCountries = codes;
            names.Text = Describe(codes);
        };
        page.Children.Add(Labelled("Two-letter country codes, e.g. MT GB IT (up to 5)", box));
        page.Children.Add(names);
        page.Children.Add(Note("Codes: MT Malta · GB United Kingdom · IT Italy · IE Ireland · FR France · DE Germany · ES Spain · US United States · AU Australia · CA Canada"));
        return page;

        static string Describe(IEnumerable<string> codes) => "Shown: " + string.Join(", ", codes.Select(RadioService.CountryName).Append("World"));
        static bool IsCountry(string code)
        {
            try
            {
                _ = new RegionInfo(code);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    private UIElement AccountsPage()
    {
        var page = Page("Accounts", "Browser accounts shown straight in Social (all of them are under Browsers).");
        foreach (var profile in BrowserProfiles.All())
        {
            var box = new CheckBox
            {
                Content = $"{profile.DisplayName}  ({profile.Browser} · {profile.Name})",
                IsChecked = _settings.PinnedBrowserProfiles.Contains(profile.Key),
                Margin = new Thickness(0, 4, 0, 4)
            };
            box.Checked += (_, _) => { if (!_settings.PinnedBrowserProfiles.Contains(profile.Key)) _settings.PinnedBrowserProfiles.Add(profile.Key); };
            box.Unchecked += (_, _) => _settings.PinnedBrowserProfiles.Remove(profile.Key);
            page.Children.Add(box);
        }

        if (BrowserProfiles.All().Count == 0)
        {
            page.Children.Add(Note("No Chrome or Edge profiles found on this PC."));
        }

        return page;
    }

    private UIElement StartupPage()
    {
        var page = Page("Start-up", "How the ring opens.");
        page.Children.Add(Heading("Hotkey"));
        foreach (var key in new[] { "Alt+Space", "Ctrl+Alt+Space" })
        {
            var option = new RadioButton { Content = key, GroupName = "hotkey", IsChecked = _settings.Hotkey == key, Margin = new Thickness(0, 3, 0, 3) };
            option.Checked += (_, _) => _settings.Hotkey = key;
            page.Children.Add(option);
        }

        page.Children.Add(Note("If another app already uses Alt+Space (for example PowerToys Run), choose Ctrl+Alt+Space."));
        page.Children.Add(Heading("Windows"));
        var startWithWindows = new CheckBox { Content = "Start with Windows (in the tray, ready for the hotkey)", IsChecked = _settings.StartWithWindows, Margin = new Thickness(0, 3, 0, 3) };
        startWithWindows.Checked += (_, _) => { _settings.StartWithWindows = true; DesktopIntegration.SetStartWithWindows(true); };
        startWithWindows.Unchecked += (_, _) => { _settings.StartWithWindows = false; DesktopIntegration.SetStartWithWindows(false); };
        page.Children.Add(startWithWindows);
        var rightClick = new CheckBox { Content = "\"Orbital Launcher\" in the desktop right-click menu", IsChecked = _settings.DesktopMenuEnabled == true, Margin = new Thickness(0, 3, 0, 3) };
        rightClick.Checked += (_, _) => { _settings.DesktopMenuEnabled = true; DesktopIntegration.RegisterDesktopMenu(); };
        rightClick.Unchecked += (_, _) => { _settings.DesktopMenuEnabled = false; DesktopIntegration.UnregisterDesktopMenu(); };
        page.Children.Add(rightClick);
        return page;
    }

    private UIElement SetupPage()
    {
        var page = Page("My setup", "Save everything you've chosen to a file, to move it to another PC or give it to someone.");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(MakeButton("Save my setup to a file…", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Orbital Launcher setup (*.json)|*.json", FileName = "my-orbital-setup.json" };
            if (dialog.ShowDialog(this) == true)
            {
                _settings.Save();
                File.Copy(RingSettings.SettingsPath, dialog.FileName, overwrite: true);
                System.Windows.MessageBox.Show(this, "Saved. Load it on another PC from this page.", "My setup");
            }
        }, primary: false));
        buttons.Children.Add(MakeButton("Load a setup file…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Orbital Launcher setup (*.json)|*.json" };
            if (dialog.ShowDialog(this) == true && RingSettings.TryLoadFrom(dialog.FileName) is { } loaded)
            {
                File.Copy(dialog.FileName, RingSettings.SettingsPath, overwrite: true);
                _apply(); // restart with the loaded setup
            }
        }, primary: false));
        page.Children.Add(buttons);
        page.Children.Add(Note("A setup file holds your apps, pins, places, radio, accounts, theme and watch. It doesn't hold passwords."));
        return page;
    }

    /// <summary>Self-test: draws every page of this window into one picture (without showing it).</summary>
    public void RenderPagesForTest(string path)
    {
        var frames = new List<System.Windows.Media.Imaging.BitmapSource>();
        foreach (var build in new Func<UIElement>[] { LookPage, PlacesPage, RadioPage, AccountsPage, StartupPage, SetupPage })
        {
            var host = new Border { Width = 660, Height = 1000, Background = Brushes.White, Padding = new Thickness(22), Child = build() };
            host.Measure(new System.Windows.Size(660, 1000));
            host.Arrange(new Rect(0, 0, 660, 1000));
            host.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(660, 1000, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            bitmap.Freeze();
            frames.Add(bitmap);
        }

        RingWindow.SaveStrip(frames, path, columns: 3);
    }

    // ---------------------------------------------------------------- building blocks

    private static StackPanel Page(string title, string intro)
    {
        var page = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        page.Children.Add(new TextBlock { Text = intro, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) });
        return page;
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8) };

    private static TextBlock Note(string text) =>
        new() { Text = text, FontSize = 12.5, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };

    private static UIElement Labelled(string label, UIElement control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(control);
        return stack;
    }

    private static ComboBox Choice(IReadOnlyList<string> options, int selected, Action<int> chosen)
    {
        var box = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = options, SelectedIndex = Math.Clamp(selected, 0, options.Count - 1) };
        box.SelectionChanged += (_, _) => chosen(box.SelectedIndex);
        return box;
    }

    private Button MakeButton(string text, Action click, bool primary)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 6),
            Background = primary ? _accent : Brushes.White,
            Foreground = primary ? Brushes.White : Brushes.Black,
            BorderBrush = primary ? _accent : new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        button.Click += (_, _) => click();
        return button;
    }
}
