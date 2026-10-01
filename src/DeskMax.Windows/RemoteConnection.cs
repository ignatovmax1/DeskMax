using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using DeskMax.Contracts;

namespace DeskMax.Windows;

/// <summary>One explicitly approved session. Each socket has one reader and one writer.</summary>
public sealed class RemoteConnection(Uri server, RegisterDeviceResponse device, Guid sessionId, bool isHost)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<RemoteInputMessage> inputs = Channel.CreateBounded<RemoteInputMessage>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DesktopInput input = new();
    private Task? run;
    private int stopped;
    public bool IsHost => isHost;
    public Func<byte[], Task>? FrameReceived { get; set; }
    public Action<string>? StateChanged { get; set; }
    public Action<string>? Finished { get; set; }
    public Task StartAsync() => run ??= RunAsync();
    public void SendInput(RemoteInputMessage message)
    {
        if (isHost || stop.IsCancellationRequested || !ready.Task.IsCompletedSuccessfully) return;
        if (!inputs.Writer.TryWrite(message)) { StateChanged?.Invoke("Очередь ввода переполнена. Сеанс остановлен."); Cancel(); }
    }
    public void Cancel() { if (Interlocked.Exchange(ref stopped, 1) != 0) return; stop.Cancel(); socket.Abort(); if (isHost) lock (input) input.ReleaseAll(); }
    public async Task StopAsync()
    {
        Cancel();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.PostAsJsonAsync(new Uri(server, $"api/sessions/{sessionId}/stop"), new SessionCredentialsRequest(device.DeviceId, device.DeviceSecret));
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        if (run is not null) await run;
    }
    private async Task RunAsync()
    {
        string result = "Сеанс завершён.";
        Task? receive = null, send = null;
        try
        {
            var address = new UriBuilder(new Uri(server, $"api/sessions/{sessionId}/transport")) { Scheme = server.Scheme == "https" ? "wss" : "ws" };
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(address.Uri, handshake.Token);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new SessionCredentialsRequest(device.DeviceId, device.DeviceSecret), Json), WebSocketMessageType.Text, true, handshake.Token);
                var welcome = await ReadMessageAsync(handshake.Token);
                if (welcome.Type != WebSocketMessageType.Text) throw new InvalidDataException("Сервер не подтвердил роль сеанса.");
                using var role = JsonDocument.Parse(welcome.Data);
                if (role.RootElement.GetProperty("type").GetString() != "role" || role.RootElement.GetProperty("role").GetString() != (isHost ? "host" : "viewer")) throw new InvalidDataException("Неверная роль сеанса.");
            }
            StateChanged?.Invoke(isHost ? "Ожидаем подключения зрителя…" : "Ожидаем трансляцию владельца…");
            receive = ReceiveAsync();
            send = SendAsync();
            await Task.WhenAny(receive, send);
            var completed = receive.IsCompleted ? receive : send;
            await completed;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (WebSocketException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { result = ex is WebSocketException ? "Соединение потеряно. Доступ остановлен." : $"Сеанс остановлен: {ex.Message}"; }
        finally
        {
            Cancel();
            if (receive is not null) { try { await receive; } catch { } }
            if (send is not null) { try { await send; } catch { } }
            if (isHost) input.ReleaseAll();
            socket.Dispose();
            Finished?.Invoke(result);
        }
    }
    private async Task ReceiveAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            using var activity = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            activity.CancelAfter(TimeSpan.FromSeconds(20));
            var message = await ReadMessageAsync(activity.Token);
            if (message.Type == WebSocketMessageType.Close) return;
            if (message.Type == WebSocketMessageType.Binary)
            {
                if (isHost) throw new InvalidDataException("Неожиданный видеокадр.");
                if (FrameReceived is not null) await FrameReceived(message.Data);
            }
            else
            {
                using var json = JsonDocument.Parse(message.Data);
                if (json.RootElement.TryGetProperty("type", out var type))
                {
                    if (type.GetString() == "ready") { ready.TrySetResult(); StateChanged?.Invoke(isHost ? "Экран транслируется. Удалённое управление разрешено." : "Подключено. Нажмите на экран для управления."); }
                    else if (type.GetString() == "error") throw new InvalidDataException("Сервер отклонил соединение.");
                }
                else if (isHost && ready.Task.IsCompletedSuccessfully)
                {
                    var command = JsonSerializer.Deserialize<RemoteInputMessage>(message.Data, Json) ?? throw new InvalidDataException("Пустая команда ввода.");
                    lock (input) { if (!stop.IsCancellationRequested) input.Apply(command); }
                }
            }
        }
    }
    private async Task SendAsync()
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), stop.Token);
        if (isHost)
        {
            while (!stop.IsCancellationRequested)
            {
                var frame = await Task.Run(() => DesktopCapture.CaptureJpeg(), stop.Token);
                if (frame.Length > 2 * 1024 * 1024) throw new InvalidDataException("Кадр превышает допустимый размер.");
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, stop.Token);
                await Task.Delay(200, stop.Token);
            }
        }
        else
        {
            await foreach (var command in inputs.Reader.ReadAllAsync(stop.Token))
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(command, Json), WebSocketMessageType.Text, true, stop.Token);
        }
    }
    private async Task<(WebSocketMessageType Type, byte[] Data)> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (part.MessageType == WebSocketMessageType.Close) return (part.MessageType, []);
            if (message.Length + part.Count > (part.MessageType == WebSocketMessageType.Binary ? 2 * 1024 * 1024 : 4096)) throw new InvalidDataException("Сообщение слишком велико.");
            message.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return (part.MessageType, message.ToArray());
    }
}
