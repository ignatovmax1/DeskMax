using DeskMax.Contracts;
using DeskMax.Server;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("connect", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var forwarding = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1
};
if (IPAddress.TryParse(builder.Configuration["Security:ReverseProxyAddress"], out var proxyAddress))
    forwarding.KnownProxies.Add(proxyAddress);
app.UseForwardedHeaders(forwarding);
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
    {
        context.Response.StatusCode = e switch { UnauthorizedAccessException => 401, KeyNotFoundException => 404, ArgumentException => 400, _ => 409 };
        await context.Response.WriteAsJsonAsync(new { error = e is UnauthorizedAccessException ? "Invalid credentials" : e.Message });
    }
});
app.UseRateLimiter();
app.MapGet("/health", () => new { status = "ok" });
app.MapPost("/api/devices", (RegisterDeviceRequest r, DeviceRegistry db) => db.Register(r));
app.MapPost("/api/devices/{id}/code", (string id, ApproveSessionRequest r, DeviceRegistry db) => db.Code(id, r.DeviceSecret, DateTimeOffset.UtcNow));
app.MapPost("/api/sessions", (CreateSessionRequest r, DeviceRegistry db) => db.Request(r, DateTimeOffset.UtcNow)).RequireRateLimiting("connect");
app.MapPost("/api/sessions/{id:guid}/approve", (Guid id, ApproveSessionRequest r, DeviceRegistry db) => db.Approve(id, r));
app.MapPost("/api/devices/{id}/trusted", (string id, GrantAccessRequest r, DeviceRegistry db) => { db.Grant(id, r); return Results.NoContent(); });
app.MapPost("/api/devices/{id}/trusted/{trustedId}/revoke", (string id, string trustedId, ApproveSessionRequest r, DeviceRegistry db) => { db.Revoke(id, r.DeviceSecret, trustedId); return Results.NoContent(); });
app.MapPost("/api/devices/{id}/sessions/incoming", (string id, ApproveSessionRequest r, DeviceRegistry db) => db.Incoming(id, r.DeviceSecret));
app.MapPost("/api/sessions/{id:guid}/status", (Guid id, SessionCredentialsRequest r, DeviceRegistry db) => db.Status(id, r));
app.MapPost("/api/sessions/{id:guid}/reject", (Guid id, ApproveSessionRequest r, DeviceRegistry db) => db.Reject(id, r));
app.Run();
public partial class Program;
