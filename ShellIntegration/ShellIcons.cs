using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopOrbit.ShellIntegration;

/// <summary>Gets the real Windows icon for any shell item (Store apps, web apps, folders, files) on a background STA thread.</summary>
public static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockingCollection<(string Path, TaskCompletionSource<ImageSource?> Result)> Queue = new();
    private static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    static ShellIcons()
    {
        var worker = new Thread(Work) { IsBackground = true, Name = "ShellIcons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static Task<ImageSource?> GetAsync(string shellPath) =>
        Cache.GetOrAdd(shellPath, path =>
        {
            var result = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Add((path, result));
            return result.Task;
        });

    private static void Work()
    {
        foreach (var (path, result) in Queue.GetConsumingEnumerable())
        {
            try
            {
                result.SetResult(Load(path, 64));
            }
            catch
            {
                result.SetResult(null);
            }
        }
    }

    private static ImageSource? Load(string path, int size)
    {
        SHCreateItemFromParsingName(path, IntPtr.Zero, ImageFactoryId, out var factory);
        const int iconOnly = 0x4;
        if (factory.GetImage(new Size { cx = size, cy = size }, iconOnly, out var hbitmap) != 0 || hbitmap == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return FromHBitmap(hbitmap);
        }
        finally
        {
            NativeMethods.DeleteObject(hbitmap);
            Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        if (NativeMethods.GetObject(hbitmap, Marshal.SizeOf<NativeMethods.BITMAP>(), out var bitmap) == 0)
        {
            return null;
        }

        int width = bitmap.bmWidth, height = Math.Abs(bitmap.bmHeight);
        var info = new NativeMethods.BITMAPINFO
        {
            biSize = 40,
            biWidth = width,
            biHeight = -height, // ask for top-down rows
            biPlanes = 1,
            biBitCount = 32
        };
        var pixels = new byte[width * height * 4];
        var dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (NativeMethods.GetDIBits(dc, hbitmap, 0, (uint)height, pixels, ref info, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            NativeMethods.DeleteDC(dc);
        }

        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(Size size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
}
