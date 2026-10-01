using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using DeskMax.Contracts;

namespace DeskMax.Server;

public sealed class SessionRelay(DeviceRegistry registry)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, Room> rooms = new();
    public static bool ValidInput(RemoteInputMessage input) =>
        double.IsFinite(input.X) && double.IsFinite(input.Y) && input.X is >= 0 and <= 1 && input.Y is >= 0 and <= 1 &&
        input.Kind switch
        {
            "move" or "releaseAll" => true,
            "down" or "up" => input.Button is "left" or "right" or "middle",
            "keyDown" or "keyUp" => input.Key is >= 1 and <= 255,
            "wheel" => input.Delta is >= -1200 and <= 1200,
            _ => false
        };
    public async Task Handle(Guid id, HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        Room? room = null; bool attached = false;
        try
        {
            using var authentication = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            authentication.CancelAfter(TimeSpan.FromSeconds(10));
            var first = await Read(socket, 4096, authentication.Token);
            if (first.Type != WebSocketMessageType.Text) throw new InvalidOperationException();
            var credentials = JsonSerializer.Deserialize<SessionCredentialsRequest>(first.Data, Json) ?? throw new InvalidOperationException();
            var role = registry.AuthorizeTransport(id, credentials);
            room = rooms.GetOrAdd(id, _ => new Room());
            var peer = new Peer(socket, role);
            lock (room.Gate)
            {
                if (room.Cancellation.IsCancellationRequested || (role == "host" ? room.Host : room.Viewer) != null) throw new InvalidOperationException();
                peer.Send(new(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new { type = "role", role }, Json)));
                if (role == "host") room.Host = peer; else room.Viewer = peer;
                attached = true;
                if (room.Host != null && room.Viewer != null)
                {
                    var ready = new Packet(WebSocketMessageType.Text, JsonSerializer.SerializeToUtf8Bytes(new { type = "ready" }, Json));
                    room.Host.Send(ready); room.Viewer.Send(ready);
                }
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, room.Cancellation.Token);
            var send = Send(peer, linked.Token);
            var receive = Receive(id, credentials, room, peer, linked.Token);
            var watch = Watch(id, credentials, room, peer, linked.Token);
            await Task.WhenAny(send, receive, watch);
            linked.Cancel();
            socket.Abort();
            try { await Task.WhenAll(send, receive, watch); } catch (Exception e) when (e is OperationCanceledException or WebSocketException or InvalidOperationException or JsonException) { }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or InvalidOperationException or JsonException or UnauthorizedAccessException or KeyNotFoundException) { }
        finally
        {
            if (room != null && attached)
            {
                lock (room.Gate)
                {
                    room.Cancellation.Cancel();
                    room.Host?.Socket.Abort(); room.Viewer?.Socket.Abort();
                }
                registry.EndTransport(id);
                rooms.TryRemove(new KeyValuePair<Guid, Room>(id, room));
            }
            socket.Abort();
        }
    }
    private async Task Watch(Guid id, SessionCredentialsRequest credentials, Room room, Peer peer, CancellationToken token)
    {
        var ticks = 0;
        while (true)
        {
            await Task.Delay(1000, token); registry.AuthorizeTransport(id, credentials);
            if (++ticks % 5 == 0) lock (room.Gate) peer.Send(new(WebSocketMessageType.Text, "{\"type\":\"heartbeat\"}"u8.ToArray()));
        }
    }
    private async Task Receive(Guid id, SessionCredentialsRequest credentials, Room room, Peer source, CancellationToken token)
    {
        while (true)
        {
            var packet = await Read(source.Socket, source.Role == "host" ? 2 * 1024 * 1024 : 4096, token);
            registry.AuthorizeTransport(id, credentials);
            if (source.Role == "host")
            {
                if (packet.Type != WebSocketMessageType.Binary || packet.Data.Length < 4 || packet.Data[0] != 0xff || packet.Data[1] != 0xd8 || packet.Data[^2] != 0xff || packet.Data[^1] != 0xd9) throw new InvalidOperationException("Invalid frame.");
            }
            else
            {
                if (packet.Type != WebSocketMessageType.Text) throw new InvalidOperationException();
                var input = JsonSerializer.Deserialize<RemoteInputMessage>(packet.Data, Json);
                if (input == null || !ValidInput(input)) throw new InvalidOperationException("Invalid input.");
            }
            lock (room.Gate)
            {
                var target = source.Role == "host" ? room.Viewer : room.Host;
                target?.Send(packet);
            }
        }
    }
    private static async Task Send(Peer peer, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Packet? packet = null;
            if (peer.Queue.Reader.TryRead(out var control)) packet = control;
            else if (peer.Frames.Reader.TryRead(out var frame)) packet = frame;
            if (packet != null) { await peer.Socket.SendAsync(packet.Data.AsMemory(), packet.Type, true, token); continue; }
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
            var controls = peer.Queue.Reader.WaitToReadAsync(waiting.Token).AsTask();
            var frames = peer.Frames.Reader.WaitToReadAsync(waiting.Token).AsTask();
            await Task.WhenAny(controls, frames);
            waiting.Cancel();
            try { await Task.WhenAll(controls, frames); } catch (OperationCanceledException) { }
        }
    }
    private static async Task<Packet> Read(WebSocket socket, int limit, CancellationToken token)
    {
        var buffer = new byte[limit + 1]; var count = 0; WebSocketMessageType? type = null;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(count), token);
            if (result.MessageType == WebSocketMessageType.Close || (type != null && type != result.MessageType)) throw new InvalidOperationException();
            type = result.MessageType; count += result.Count;
            if (count > limit) throw new InvalidOperationException("Message too large.");
            if (result.EndOfMessage) return new(result.MessageType, buffer[..count]);
        }
    }
    private sealed record Packet(WebSocketMessageType Type, byte[] Data);
    private sealed class Peer(WebSocket socket, string role)
    {
        public WebSocket Socket { get; } = socket;
        public string Role { get; } = role;
        public Channel<Packet> Queue { get; } = Channel.CreateBounded<Packet>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public Channel<Packet> Frames { get; } = Channel.CreateBounded<Packet>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        public void Send(Packet packet)
        {
            if (packet.Type == WebSocketMessageType.Binary) Frames.Writer.TryWrite(packet);
            else if (!Queue.Writer.TryWrite(packet)) throw new InvalidOperationException("Input queue overflow.");
        }
    }
    private sealed class Room
    {
        public object Gate { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public Peer? Host { get; set; }
        public Peer? Viewer { get; set; }
    }
}
