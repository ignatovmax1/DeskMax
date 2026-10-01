using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DeskMax.Contracts;
using DeskMax.Windows;

internal static class RelaySmoke
{
    public static async Task RunAsync(Window window, TextBox text, Func<bool> ownForeground, Func<bool> aHeld)
    {
        string root = FindRoot();
        string? serverDll = new[] { "Release", "Debug" }.Select(c => Path.Combine(root, "src", "DeskMax.Server", "bin", c, "net8.0", "DeskMax.Server.dll"))
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (serverDll is null) throw new InvalidOperationException("Build DeskMax.Server first; no server assembly found.");
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        int port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        var server = new Uri($"http://127.0.0.1:{port}/");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(serverDll)! };
        start.ArgumentList.Add(serverDll);
        start.Environment["ASPNETCORE_URLS"] = server.AbsoluteUri.TrimEnd('/');
        start.Environment["Security__RotatingCodeKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start loopback relay.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        RemoteConnection? host = null, viewer = null;
        try
        {
            using var http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(3) };
            await WaitFor(async () => { if (process.HasExited) throw new InvalidOperationException("Loopback server exited."); try { using var response = await http.GetAsync("health"); return response.IsSuccessStatusCode; } catch (HttpRequestException) { return false; } }, 15000, "Loopback server startup");
            var owner = await Post<RegisterDeviceRequest, RegisterDeviceResponse>(http, "api/devices", new("Smoke owner", "Windows"));
            var guest = await Post<RegisterDeviceRequest, RegisterDeviceResponse>(http, "api/devices", new("Smoke viewer", "Windows"));
            var code = await Post<ApproveSessionRequest, RotatingCodeResponse>(http, $"api/devices/{owner.DeviceId}/code", new(owner.DeviceSecret));
            var session = await Post<CreateSessionRequest, SessionResponse>(http, "api/sessions", new(code.Code, guest.DeviceId, guest.DeviceSecret));
            await Post<ApproveSessionRequest, SessionResponse>(http, $"api/sessions/{session.SessionId}/approve", new(owner.DeviceSecret));
            // Construct both actual WPF views without connecting or injecting input.
            new RemoteWindow(server, owner, Guid.Empty, true).Close();
            new RemoteWindow(server, guest, Guid.Empty, false).Close();
            Console.WriteLine("PASS remote window: host and viewer XAML initialized");
            host = new(server, owner, session.SessionId, true);
            viewer = new(server, guest, session.SessionId, false);
            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.FrameReceived = bytes => { received.TrySetResult(bytes); return Task.CompletedTask; };
            _ = host.StartAsync(); _ = viewer.StartAsync();
            byte[] bytes = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using (var stream = new MemoryStream(bytes))
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) throw new InvalidOperationException("Relay delivered invalid JPEG.");
                Console.WriteLine($"PASS relay frame: actual host JPEG decoded by viewer {frame.PixelWidth}x{frame.PixelHeight}, {bytes.Length} bytes");
            }
            window.Activate(); text.Focus(); text.Clear();
            await Task.Delay(100);
            if (!ownForeground() || !text.IsKeyboardFocused) throw new InvalidOperationException("Own test textbox lost focus; relay input skipped.");
            viewer.SendInput(new("keyDown", Key: 0x41)); viewer.SendInput(new("keyUp", Key: 0x41));
            await WaitFor(() => Task.FromResult(text.Text.Length == 1), 3000, "Relay keyboard into own textbox");
            Console.WriteLine("PASS relay keyboard: viewer command inserted one character in host textbox");
            int clicks = 0, wheelDelta = 0;
            text.PreviewMouseDown += (_, args) => { if (args.ChangedButton == System.Windows.Input.MouseButton.Left) clicks++; };
            text.PreviewMouseWheel += (_, args) => wheelDelta += args.Delta;
            var center = text.PointToScreen(new Point(text.ActualWidth / 2, text.ActualHeight / 2));
            double mouseX = center.X / GetSystemMetrics(0), mouseY = center.Y / GetSystemMetrics(1);
            if (!ownForeground() || mouseX is < 0 or > 1 || mouseY is < 0 or > 1)
                throw new InvalidOperationException("Own test window not foreground or textbox outside primary monitor; relay click skipped.");
            viewer.SendInput(new("move", mouseX, mouseY));
            viewer.SendInput(new("down", mouseX, mouseY, Button: "left"));
            viewer.SendInput(new("up", mouseX, mouseY, Button: "left"));
            await WaitFor(() => Task.FromResult(clicks == 1), 3000, "Relay left click on own textbox");
            Console.WriteLine("PASS relay mouse: viewer left click received by own host textbox");
            if (!ownForeground()) throw new InvalidOperationException("Own window lost focus; relay wheel skipped.");
            viewer.SendInput(new("wheel", mouseX, mouseY, Delta: 120));
            await WaitFor(() => Task.FromResult(wheelDelta == 120), 3000, "Relay wheel on own textbox");
            Console.WriteLine("PASS relay wheel: viewer delta 120 received by own host textbox");
            if (!ownForeground()) throw new InvalidOperationException("Test window lost focus before relay cleanup test.");
            viewer.SendInput(new("keyDown", Key: 0x41));
            await WaitFor(() => Task.FromResult(aHeld()), 3000, "Relay held A");
            await viewer.StopAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await WaitFor(() => Task.FromResult(!aHeld()), 3000, "Host input release after viewer disconnect");
            Console.WriteLine("PASS relay disconnect: host released viewer-held A");
        }
        finally
        {
            viewer?.Cancel(); host?.Cancel();
            if (viewer is not null) { try { await viewer.StopAsync().WaitAsync(TimeSpan.FromSeconds(8)); } catch { } }
            if (host is not null) { try { await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(8)); } catch { } }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<TResponse> Post<TRequest, TResponse>(HttpClient http, string path, TRequest request)
    {
        using var response = await http.PostAsJsonAsync(path, request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>() ?? throw new InvalidOperationException("Empty relay API response.");
    }
    private static async Task WaitFor(Func<Task<bool>> condition, int milliseconds, string step)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { if (await condition()) return; await Task.Delay(50); }
        throw new TimeoutException(step);
    }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) { if (Directory.Exists(Path.Combine(directory.FullName, "src", "DeskMax.Server"))) return directory.FullName; directory = directory.Parent; }
        throw new InvalidOperationException("Cannot locate repository/server assembly.");
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
