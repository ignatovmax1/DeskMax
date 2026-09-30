using DeskMax.Contracts;
using DeskMax.Server;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("connect", x => { x.PermitLimit = 8; x.Window = TimeSpan.FromMinutes(1); x.QueueLimit = 0; }));
var app = builder.Build(); app.UseRateLimiter();
app.MapGet("/health", () => new { status = "ok" });
app.MapPost("/api/devices", (RegisterDeviceRequest r, DeviceRegistry db) => db.Register(r));
app.MapGet("/api/devices/{id}/code", (string id, string secret, DeviceRegistry db) => db.Code(id, secret, DateTimeOffset.UtcNow));
app.MapPost("/api/sessions", (CreateSessionRequest r, DeviceRegistry db) => db.Request(r, DateTimeOffset.UtcNow)).RequireRateLimiting("connect");
app.MapPost("/api/sessions/{id:guid}/approve", (Guid id, ApproveSessionRequest r, DeviceRegistry db) => db.Approve(id, r));
app.MapPost("/api/devices/{id}/trusted", (string id, GrantAccessRequest r, DeviceRegistry db) => { db.Grant(id, r); return Results.NoContent(); });
app.Run();
public partial class Program;
