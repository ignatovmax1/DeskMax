using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskMax.Windows;

public static class DesktopCapture
{
    public static byte[] CaptureJpeg(int maxWidth = 1600, int quality = 65)
    {
        if (maxWidth is < 320 or > 7680) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        int width = GetSystemMetrics(0), height = GetSystemMetrics(1);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("No primary display is available.");
        nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
        try
        {
            if (screen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            memory = CreateCompatibleDC(screen);
            if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!BitBlt(memory, 0, 0, width, height, screen, 0, 0, 0x40CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            if (width > maxWidth || height > maxWidth)
            {
                var scale = (double)maxWidth / Math.Max(width, height);
                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scaled.Freeze();
                source = scaled;
            }
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new System.IO.MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            if (memory != 0 && previous != 0 && previous != -1) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint flags);
}
