using System.Diagnostics;
using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DesktopOrbit.ShellIntegration;

/// <summary>Quick actions for the System section: screenshots, screen recording, lock, and common Windows pages.</summary>
public static class SystemActions
{
    private const byte VkLeftWin = 0x5B;
    private const byte VkShift = 0x10;

    public static string ScreenshotFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// <summary>Captures every monitor, saves a PNG in Pictures\Screenshots, and copies it to the clipboard.</summary>
    public static string CaptureDesktop() => CaptureDesktop(ScreenshotFolder, copyToClipboard: true);

    public static string CaptureDesktop(string folder, bool copyToClipboard)
    {
        var bounds = Forms.SystemInformation.VirtualScreen;
        using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Desktop {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        bitmap.Save(path, Drawing.Imaging.ImageFormat.Png);

        if (copyToClipboard)
        {
            try
            {
                Forms.Clipboard.SetImage(bitmap);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Another app is holding the clipboard; the file is still saved.
            }
        }

        return path;
    }

    /// <summary>Opens the Windows area snip (same as Win+Shift+S).</summary>
    public static void SnipArea() => Open("ms-screenclip:");

    /// <summary>Starts Snipping Tool screen recording (Win+Shift+R on Windows 11).</summary>
    public static void RecordScreen() => NativeMethods.SendKeyCombo(VkLeftWin, VkShift, (byte)'R');

    public static void ClipboardHistory() => NativeMethods.SendKeyCombo(VkLeftWin, (byte)'V');

    public static void Lock() => NativeMethods.LockWorkStation();

    public static void Open(string target, string? arguments = null)
    {
        var info = arguments is null
            ? new ProcessStartInfo(target) { UseShellExecute = true }
            : new ProcessStartInfo(target, arguments) { UseShellExecute = true };
        Process.Start(info);
    }

    public static void OpenFolder(string shellPath) => Open("explorer.exe", shellPath);
}
