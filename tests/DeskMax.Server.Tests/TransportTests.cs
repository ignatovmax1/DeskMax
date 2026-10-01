using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DeskMax.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DeskMax.Server.Tests;
[TestClass]
public class TransportTests
{
    [TestMethod]
    public void ApprovalExtendsLifetimeAndStopDeniesTransport()
    {
        var db = Registry(); var owner = db.Register(new("Owner", "Windows")); var viewer = db.Register(new("Viewer", "Windows"));
        var session = db.Request(new(db.Code(owner.DeviceId, owner.DeviceSecret, DateTimeOffset.UtcNow).Code, viewer.DeviceId, viewer.DeviceSecret), DateTimeOffset.UtcNow);
        Assert.ThrowsExactly<InvalidOperationException>(() => db.AuthorizeTransport(session.SessionId, new(viewer.DeviceId, viewer.DeviceSecret)));
        var approved = db.Approve(session.SessionId, new(owner.DeviceSecret));
        Assert.IsTrue(approved.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(59));
        Assert.AreEqual("host", db.AuthorizeTransport(session.SessionId, new(owner.DeviceId, owner.DeviceSecret)));
        Assert.AreEqual("viewer", db.AuthorizeTransport(session.SessionId, new(viewer.DeviceId, viewer.DeviceSecret)));
        Assert.AreEqual("ended", db.Stop(session.SessionId, new(viewer.DeviceId, viewer.DeviceSecret)).Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => db.AuthorizeTransport(session.SessionId, new(owner.DeviceId, owner.DeviceSecret)));
    }
    [TestMethod]
    public void MalformedInputIsRejected()
    {
        Assert.IsFalse(SessionRelay.ValidInput(new("move", double.NaN)));
        Assert.IsFalse(SessionRelay.ValidInput(new("move", 1.1)));
        Assert.IsFalse(SessionRelay.ValidInput(new("keyDown", Key: 256)));
        Assert.IsFalse(SessionRelay.ValidInput(new("down", Button: "unknown")));
        Assert.IsFalse(SessionRelay.ValidInput(new("wheel", Delta: int.MaxValue)));
        Assert.IsTrue(SessionRelay.ValidInput(new("move", .5, .5)));
    }
    [TestMethod]
    public void UnicodeTextAcceptsBoundedContentAndRejectsEmptyOversizedOrNul()
    {
        Assert.IsTrue(SessionRelay.ValidInput(new("text", Text: "Привет 世界 👋")));
        Assert.IsTrue(SessionRelay.ValidInput(new("text", Text: new string('я', 1024))));
        Assert.IsFalse(SessionRelay.ValidInput(new("text")));
        Assert.IsFalse(SessionRelay.ValidInput(new("text", Text: "")));
        Assert.IsFalse(SessionRelay.ValidInput(new("text", Text: new string('я', 1025))));
        Assert.IsFalse(SessionRelay.ValidInput(new("text", Text: "before\0after")));
    }
    [TestMethod]
    public async Task RealWebSocketsRelayFramesInputAndStopIdleSession()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var db = Registry(); var relay = new SessionRelay(db);
        await using var app = builder.Build(); app.UseWebSockets();
        app.MapGet("/api/sessions/{id:guid}/transport", (Guid id, Microsoft.AspNetCore.Http.HttpContext context) => relay.Handle(id, context));
        await app.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("http:", "ws:");
        var owner = db.Register(new("Host", "Windows")); var viewer = db.Register(new("Viewer", "Windows"));
        var session = db.Request(new(db.Code(owner.DeviceId, owner.DeviceSecret, DateTimeOffset.UtcNow).Code, viewer.DeviceId, viewer.DeviceSecret), DateTimeOffset.UtcNow);
        db.Approve(session.SessionId, new(owner.DeviceSecret));
        using var hostSocket = new ClientWebSocket(); using var viewerSocket = new ClientWebSocket();
        await hostSocket.ConnectAsync(new Uri($"{address}/api/sessions/{session.SessionId}/transport"), timeout.Token);
        await Credentials(hostSocket, owner, timeout.Token); Assert.Contains("host", await Text(hostSocket, timeout.Token));
        await viewerSocket.ConnectAsync(new Uri($"{address}/api/sessions/{session.SessionId}/transport"), timeout.Token);
        await Credentials(viewerSocket, viewer, timeout.Token); Assert.Contains("viewer", await Text(viewerSocket, timeout.Token));
        Assert.Contains("ready", await Text(hostSocket, timeout.Token)); Assert.Contains("ready", await Text(viewerSocket, timeout.Token));
        using (var duplicate = new ClientWebSocket())
        {
            await duplicate.ConnectAsync(new Uri($"{address}/api/sessions/{session.SessionId}/transport"), timeout.Token);
            await Credentials(duplicate, viewer, timeout.Token);
            await AssertClosed(duplicate, timeout.Token);
        }
        byte[] frame = [255,216,1,2,255,217];
        await hostSocket.SendAsync(frame, WebSocketMessageType.Binary, true, timeout.Token);
        var buffer = new byte[100]; var received = await viewerSocket.ReceiveAsync(buffer, timeout.Token);
        CollectionAssert.AreEqual(frame, buffer[..received.Count]);
        var input = Encoding.UTF8.GetBytes("{\"kind\":\"keyDown\",\"key\":65}");
        await viewerSocket.SendAsync(input, WebSocketMessageType.Text, true, timeout.Token);
        Assert.Contains("keyDown", await Text(hostSocket, timeout.Token));
        var unicode = "Привет 世界 👋";
        await viewerSocket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new RemoteInputMessage("text", Text: unicode), new JsonSerializerOptions(JsonSerializerDefaults.Web))), WebSocketMessageType.Text, true, timeout.Token);
        using (var relayed = JsonDocument.Parse(await Text(hostSocket, timeout.Token)))
        {
            Assert.AreEqual("text", relayed.RootElement.GetProperty("kind").GetString());
            Assert.AreEqual(unicode, relayed.RootElement.GetProperty("text").GetString());
        }
        db.Stop(session.SessionId, new(owner.DeviceId, owner.DeviceSecret));
        await AssertClosed(hostSocket, timeout.Token);
        await AssertClosed(viewerSocket, timeout.Token);
        Assert.AreEqual("ended", db.Status(session.SessionId, new(viewer.DeviceId, viewer.DeviceSecret)).Status);
        await app.StopAsync();
    }
    [TestMethod]
    public async Task PendingAndOutsiderCredentialsCannotAttach()
    {
        await using var server = await Fixture.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var pending = await server.Connect(server.Viewer, timeout.Token)) await AssertClosed(pending, timeout.Token);
        Assert.AreEqual("pending-owner-confirmation", server.Db.Status(server.Id, server.Credentials(server.Viewer)).Status);
        server.Approve();
        var stranger = server.Db.Register(new("Stranger", "Windows"));
        using (var outsider = await server.Connect(stranger, timeout.Token)) await AssertClosed(outsider, timeout.Token);
        Assert.AreEqual("approved", server.Db.Status(server.Id, server.Credentials(server.Viewer)).Status);
        using var host = await server.Connect(server.Host, timeout.Token);
        Assert.Contains("host", await Text(host, timeout.Token));
    }
    [TestMethod]
    [DataRow("revoke")]
    [DataRow("invalid-key")]
    [DataRow("malformed-json")]
    [DataRow("oversized")]
    [DataRow("wrong-message-type")]
    public async Task RevocationAndInvalidInputCloseBothPeers(string scenario)
    {
        await using var server = await Fixture.Start(); server.Approve();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var host = await server.Connect(server.Host, timeout.Token); Assert.Contains("host", await Text(host, timeout.Token));
        using var viewer = await server.Connect(server.Viewer, timeout.Token); Assert.Contains("viewer", await Text(viewer, timeout.Token));
        Assert.Contains("ready", await Text(host, timeout.Token)); Assert.Contains("ready", await Text(viewer, timeout.Token));
        if (scenario == "revoke") server.Db.Revoke(server.Host.DeviceId, server.Host.DeviceSecret, server.Viewer.DeviceId);
        else
        {
            var bytes = Encoding.UTF8.GetBytes(scenario switch { "invalid-key" => "{\"kind\":\"keyDown\",\"key\":0}", "malformed-json" => "{broken", "oversized" => new string('x', 4097), _ => "{\"kind\":\"move\"}" });
            await viewer.SendAsync(bytes, scenario == "wrong-message-type" ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, timeout.Token);
        }
        await AssertClosed(host, timeout.Token); await AssertClosed(viewer, timeout.Token);
        Assert.AreEqual(scenario == "revoke" ? "revoked" : "ended", server.Db.Status(server.Id, server.Credentials(server.Host)).Status);
    }
    private static async Task AssertClosed(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                var value = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Assert.IsTrue(result.MessageType == WebSocketMessageType.Text && value.Contains("heartbeat"), "Expected transport closure; received application data: " + value);
            }
        }
        catch (WebSocketException) { }
        // Cancellation is deliberately not accepted: a socket that remains open fails the test.
    }
    private sealed class Fixture(WebApplication app, string address, DeviceRegistry db, RegisterDeviceResponse host, RegisterDeviceResponse viewer, Guid id) : IAsyncDisposable
    {
        public DeviceRegistry Db => db;
        public RegisterDeviceResponse Host => host;
        public RegisterDeviceResponse Viewer => viewer;
        public Guid Id => id;
        public SessionCredentialsRequest Credentials(RegisterDeviceResponse device) => new(device.DeviceId, device.DeviceSecret);
        public void Approve() => db.Approve(id, new(host.DeviceSecret));
        public async Task<ClientWebSocket> Connect(RegisterDeviceResponse device, CancellationToken token)
        {
            var socket = new ClientWebSocket();
            try { await socket.ConnectAsync(new Uri($"{address}/api/sessions/{id}/transport"), token); await TransportTests.Credentials(socket, device, token); return socket; }
            catch { socket.Dispose(); throw; }
        }
        public static async Task<Fixture> Start()
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var db = Registry(); var relay = new SessionRelay(db);
            var app = builder.Build(); app.UseWebSockets();
            app.MapGet("/api/sessions/{id:guid}/transport", (Guid id, Microsoft.AspNetCore.Http.HttpContext context) => relay.Handle(id, context));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("http:", "ws:");
            var host = db.Register(new("Host", "Windows")); var viewer = db.Register(new("Viewer", "Windows"));
            var session = db.Request(new(db.Code(host.DeviceId, host.DeviceSecret, DateTimeOffset.UtcNow).Code, viewer.DeviceId, viewer.DeviceSecret), DateTimeOffset.UtcNow);
            return new(app, address, db, host, viewer, session.SessionId);
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
    private static DeviceRegistry Registry() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Security:RotatingCodeKey"] = Convert.ToBase64String(new byte[32]) }).Build());
    private static Task Credentials(ClientWebSocket socket, RegisterDeviceResponse device, CancellationToken token) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new SessionCredentialsRequest(device.DeviceId, device.DeviceSecret), new JsonSerializerOptions(JsonSerializerDefaults.Web)), WebSocketMessageType.Text, true, token);
    private static async Task<string> Text(ClientWebSocket socket, CancellationToken token) { var bytes = new byte[4096]; var result = await socket.ReceiveAsync(bytes, token); Assert.AreEqual(WebSocketMessageType.Text, result.MessageType); Assert.IsTrue(result.EndOfMessage); return Encoding.UTF8.GetString(bytes, 0, result.Count); }
}
