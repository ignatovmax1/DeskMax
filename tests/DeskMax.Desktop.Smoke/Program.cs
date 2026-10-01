using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskMax.Contracts;
using DeskMax.Windows;

internal static class Program
{
    private static readonly DesktopInput DesktopInput = new();
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var input = new TextBox { Margin = new Thickness(24), FontSize = 28, Height = 60 };
        int aKeyEvents = 0;
        input.PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.A) aKeyEvents++; };
        var panel = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(230, 40, 190)) };
        panel.Children.Add(new TextBlock { Text = "DeskMax desktop smoke test", Margin = new Thickness(24), FontSize = 24 });
        panel.Children.Add(input);
        var window = new Window { Title = "DeskMax desktop smoke test", Width = 600, Height = 350,
            Left = 30, Top = 30, Content = panel, Topmost = true };
        GetCursorPos(out var originalCursor);
        int result = 1;
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(args.Contains("--relay") ? 90 : 30) };
        deadline.Tick += (_, _) => { Console.Error.WriteLine("FAIL: desktop smoke timeout"); DesktopInput.ReleaseAll(); window.Close(); app.Shutdown(1); };
        window.Loaded += async (_, _) =>
        {
            try
            {
                window.Activate();
                input.Focus();
                await Task.Delay(500);
                nint handle = new WindowInteropHelper(window).Handle;
                Require(GetForegroundWindow() == handle && input.IsKeyboardFocused, "Test window did not receive keyboard focus; input not injected.");

                byte[] jpeg = DesktopCapture.CaptureJpeg();
                using var stream = new System.IO.MemoryStream(jpeg);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                Require(frame.PixelWidth > 0 && frame.PixelWidth <= 1600 && frame.PixelHeight > 0, "Invalid capture dimensions.");
                var pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
                var data = new byte[pixels.PixelWidth * pixels.PixelHeight * 4];
                pixels.CopyPixels(data, pixels.PixelWidth * 4, 0);
                int magenta = 0;
                for (int i = 0; i < data.Length; i += 4)
                    if (data[i] > 130 && data[i + 1] < 100 && data[i + 2] > 170) magenta++;
                Require(magenta > 1000, "Capture did not contain the visible colored test window (blank/protected desktop possible).");
                Console.WriteLine($"PASS capture: {frame.PixelWidth}x{frame.PixelHeight}, {jpeg.Length} bytes, colored test pixels={magenta}");

                int gdiBefore = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                for (int i = 0; i < 25; i++) DesktopCapture.CaptureJpeg(640, 45);
                int gdiAfter = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                Require(gdiBefore > 0 && gdiAfter <= gdiBefore + 4, $"Possible GDI leak: {gdiBefore} -> {gdiAfter}");
                Console.WriteLine($"PASS repeated capture: 25 frames, GDI handles {gdiBefore} -> {gdiAfter}");

                Require(GetForegroundWindow() == handle && input.IsKeyboardFocused, "Focus changed; keyboard injection skipped.");
                DesktopInput.Apply(new RemoteInputMessage("keyDown", Key: 0x41));
                DesktopInput.Apply(new RemoteInputMessage("keyUp", Key: 0x41));
                var wait = Stopwatch.StartNew();
                while (input.Text.Length == 0 && wait.ElapsedMilliseconds < 2000) await Task.Delay(25);
                Require(aKeyEvents == 1 && input.Text.Length == 1, $"SendInput did not deliver A and insert one character in own textbox (events {aKeyEvents}, text length {input.Text.Length}).");
                Console.WriteLine("PASS keyboard: virtual A received and one character inserted in own textbox (active keyboard layout respected)");

                Require(GetForegroundWindow() == handle, "Focus changed; held-key cleanup test skipped.");
                DesktopInput.Apply(new RemoteInputMessage("keyDown", Key: 0x41));
                await Task.Delay(50);
                Require((GetAsyncKeyState(0x41) & 0x8000) != 0, "Injected A was not held before cleanup.");
                DesktopInput.ReleaseAll();
                await Task.Delay(50);
                Require((GetAsyncKeyState(0x41) & 0x8000) == 0, "ReleaseAll left injected A held.");
                Console.WriteLine("PASS disconnect cleanup: held A released by ReleaseAll");

                DesktopInput.Apply(new RemoteInputMessage("move", X: 0.25, Y: 0.25));
                await Task.Delay(100);
                Require(GetCursorPos(out var moved), "Could not read cursor position.");
                int expectedX = (int)(GetSystemMetrics(0) * 0.25), expectedY = (int)(GetSystemMetrics(1) * 0.25);
                Require(Math.Abs(moved.X - expectedX) <= 2 && Math.Abs(moved.Y - expectedY) <= 2,
                    $"Normalized cursor mismatch: {moved.X},{moved.Y}; expected {expectedX},{expectedY}");
                Console.WriteLine($"PASS mouse: normalized (0.25,0.25) -> ({moved.X},{moved.Y})");
                if (args.Contains("--relay"))
                    await RelaySmoke.RunAsync(window, input, () => GetForegroundWindow() == handle, () => (GetAsyncKeyState(0x41) & 0x8000) != 0);
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"FAIL: {ex.Message}"); }
            finally
            {
                deadline.Stop();
                DesktopInput.ReleaseAll();
                SetCursorPos(originalCursor.X, originalCursor.Y);
                window.Close();
                app.Shutdown(result);
            }
        };
        deadline.Start();
        app.Run(window);
        SetCursorPos(originalCursor.X, originalCursor.Y);
        return result;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetGuiResources(nint process, int flags);
}
