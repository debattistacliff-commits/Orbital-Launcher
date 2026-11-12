using System.IO;
using System.IO.Pipes;
using Microsoft.Win32;

namespace DesktopOrbit.ShellIntegration;

/// <summary>The "Orbital Launcher" entry in the desktop right-click menu, and starting with Windows. Both live in HKCU, so no admin rights are needed.</summary>
public static class DesktopIntegration
{
    private const string MenuKey = @"Software\Classes\DesktopBackground\Shell\OrbitalLauncher";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "OrbitalLauncher";

    private static readonly (string Id, string Label, string Section)[] MenuItems =
    [
        ("01ring", "Open ring", ""),
        ("02apps", "Apps", "apps"),
        ("03web", "Web and Social", "web"),
        ("04files", "Files", "files"),
        ("05system", "System", "system")
    ];

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");

    public static bool IsDesktopMenuRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(MenuKey);
        return key is not null;
    }

    public static void RegisterDesktopMenu()
    {
        var exe = ExePath;
        using var root = Registry.CurrentUser.CreateSubKey(MenuKey);
        root.SetValue("MUIVerb", "Orbital Launcher");
        root.SetValue("Icon", $"\"{exe}\",0");
        root.SetValue("SubCommands", string.Empty);
        root.SetValue("Position", "Top");

        foreach (var (id, label, section) in MenuItems)
        {
            using var item = root.CreateSubKey($@"shell\{id}");
            item.SetValue("MUIVerb", label);
            using var command = item.CreateSubKey("command");
            command.SetValue(string.Empty, $"\"{exe}\" --ring {section}".TrimEnd());
        }
    }

    /// <summary>Removes the entries left by the old name ("Titanium Orbital"), keeping start-with-Windows if it was on.</summary>
    public static void RemoveOldNameEntries()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\DesktopBackground\Shell\TitaniumOrbital", throwOnMissingSubKey: false);
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue("TitaniumOrbitalLauncher", throwOnMissingValue: false);
    }

    public static void UnregisterDesktopMenu() => Registry.CurrentUser.DeleteSubKeyTree(MenuKey, throwOnMissingSubKey: false);

    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(RunValue, $"\"{ExePath}\" --tray");
        }
        else
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}

/// <summary>Keeps one copy running. A second launch (e.g. from the right-click menu) hands its arguments to the first and exits.</summary>
public static class SingleInstance
{
    private static readonly string PipeName = $"OrbitalLauncher.v2.{Environment.UserName}";
    private const char Separator = '\u001f';

    public static bool TrySend(string[] args)
    {
        try
        {
            // Let the running copy bring the ring to the front.
            NativeMethods.AllowSetForegroundWindow(-1);
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.WriteLine(string.Join(Separator, args));
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Listen(Action<string[]> onArguments)
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync() ?? string.Empty;
                    onArguments(line.Length == 0 ? [] : line.Split(Separator));
                }
                catch (IOException)
                {
                    await Task.Delay(200);
                }
            }
        });
    }
}
