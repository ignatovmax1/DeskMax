using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DeskMax.Contracts;

namespace DeskMax.Windows;

public partial class RemoteWindow : Window
{
    private readonly RemoteConnection connection;
    private readonly HashSet<int> keys = [];
    private bool closing;
    private bool hotkeyRegistered;
    private long lastMove;
    private double lastX, lastY;
    private readonly object frameGate = new();
    private byte[]? pendingFrame;
    private int frameDecodeScheduled;
    private HwndSource? source;
    private const int StopHotkey = 0xD35;
    public RemoteWindow(Uri server, RegisterDeviceResponse device, Guid sessionId, bool host)
    {
        connection = new(server, device, sessionId, host);
        InitializeComponent();
        if (host)
        {
            Title = "DeskMax — трансляция вашего экрана"; SessionTitle.Text = "Трансляция вашего экрана";
            ViewerArea.Visibility = Visibility.Collapsed; HostArea.Visibility = Visibility.Visible;
            ControlEnabled.Visibility = Visibility.Collapsed; Width = 520; Height = 290; Topmost = true;
            HelpText.Text = "Доступ действует до завершения сеанса, максимум 1 час.";
        }
        connection.StateChanged = text => OnUi(() => StateText.Text = text);
        connection.Finished = text => OnUi(() => { StateText.Text = text; ControlEnabled.IsEnabled = false; StopButton.Content = "Закрыть"; });
        connection.FrameReceived = frame =>
        {
            if (closing) return Task.CompletedTask;
            lock (frameGate)
            {
                pendingFrame = frame;
                if (frameDecodeScheduled != 0) return Task.CompletedTask;
                frameDecodeScheduled = 1;
            }
            _ = Task.Run(DecodeLatestFramesAsync).ContinueWith(task =>
            {
                if (task.Exception is not null) OnUi(() => { StateText.Text = "Сеанс остановлен: повреждённый видеокадр."; Close(); });
            }, TaskContinuationOptions.OnlyOnFaulted);
            return Task.CompletedTask;
        };
    }
    private async Task DecodeLatestFramesAsync()
    {
        while (!closing)
        {
            byte[]? frame;
            lock (frameGate)
            {
                frame = pendingFrame;
                pendingFrame = null;
                if (frame is null) { frameDecodeScheduled = 0; return; }
            }
            JpegFrame.ReadSize(frame);
            using var memory = new MemoryStream(frame);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.DecodePixelWidth = 1280;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            if (bitmap.PixelWidth > 4096 || bitmap.PixelHeight > 4096) throw new InvalidDataException("Недопустимое разрешение кадра.");
            bitmap.Freeze();
            await Dispatcher.InvokeAsync(() => { if (!closing) { RemoteImage.Source = bitmap; WaitingText.Visibility = Visibility.Collapsed; } });
        }
    }
    private void OnUi(Action action) { if (!closing && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (!closing) action(); }); }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await connection.StartAsync();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closing = true;
        connection.Cancel();
        if (hotkeyRegistered && source is not null) UnregisterHotKey(source.Handle, StopHotkey);
        if (source is not null) source.RemoveHook(HotkeyHook);
        _ = connection.StopAsync();
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Deactivated(object? sender, EventArgs e) => ReleaseInput();
    private void Image_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ReleaseInput();
    private void Control_Changed(object sender, RoutedEventArgs e) { if (connection is not null) ReleaseInput(); }
    private void ReleaseInput() { keys.Clear(); connection.SendInput(new("releaseAll")); if (RemoteImage?.IsMouseCaptured == true) RemoteImage.ReleaseMouseCapture(); }
    private bool CanControl => !connection.IsHost && !closing && ControlEnabled.IsChecked == true && ControlEnabled.IsEnabled && RemoteImage.Source is BitmapSource;
    private bool Position(MouseEventArgs e, out double x, out double y)
    {
        x = y = 0;
        if (RemoteImage.Source is not BitmapSource image) return false;
        var p = e.GetPosition(RemoteImage);
        return RemoteViewport.TryNormalize(RemoteImage.ActualWidth, RemoteImage.ActualHeight, image.PixelWidth, image.PixelHeight, p.X, p.Y, out x, out y);
    }
    private void Image_MouseMove(object sender, MouseEventArgs e)
    {
        if (!CanControl || !RemoteImage.IsKeyboardFocused || Environment.TickCount64 - lastMove < 33 || !Position(e, out var x, out var y)) return;
        lastMove = Environment.TickCount64; lastX = x; lastY = y; connection.SendInput(new("move", x, y));
    }
    private static string? ButtonName(MouseButton button) => button switch { MouseButton.Left => "left", MouseButton.Right => "right", MouseButton.Middle => "middle", _ => null };
    private void Image_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanControl || ButtonName(e.ChangedButton) is not string button || !Position(e, out var x, out var y)) return;
        RemoteImage.Focus(); RemoteImage.CaptureMouse(); lastX = x; lastY = y;
        connection.SendInput(new("down", x, y, Button: button)); e.Handled = true;
    }
    private void Image_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanControl || ButtonName(e.ChangedButton) is not string button) return;
        if (Position(e, out var x, out var y)) { lastX = x; lastY = y; }
        connection.SendInput(new("up", lastX, lastY, Button: button));
        if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released) RemoteImage.ReleaseMouseCapture(); e.Handled = true;
    }
    private void Image_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!CanControl || !RemoteImage.IsKeyboardFocused || !Position(e, out var x, out var y)) return;
        connection.SendInput(new("wheel", x, y, Delta: Math.Clamp(e.Delta, -1200, 1200))); e.Handled = true;
    }
    private void Image_KeyDown(object sender, KeyEventArgs e)
    {
        if (!CanControl) return;
        var key = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (key is < 1 or > 255) return;
        keys.Add(key); connection.SendInput(new("keyDown", Key: key)); e.Handled = true;
    }
    private void Image_KeyUp(object sender, KeyEventArgs e)
    {
        if (!CanControl) return;
        var key = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (keys.Remove(key)) connection.SendInput(new("keyUp", Key: key)); e.Handled = true;
    }
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (!connection.IsHost) return;
        source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source?.AddHook(HotkeyHook);
        hotkeyRegistered = source is not null && RegisterHotKey(source.Handle, StopHotkey, 0x4003, 0x7B);
        if (!hotkeyRegistered) HelpText.Text = "Горячая клавиша занята. Используйте кнопку завершения сеанса.";
    }
    private IntPtr HotkeyHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == StopHotkey) { handled = true; Close(); } return IntPtr.Zero;
    }
    private void Window_StateChanged(object? sender, EventArgs e) { if (connection.IsHost && WindowState == WindowState.Minimized) WindowState = WindowState.Normal; }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
