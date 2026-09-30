using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DeskMax.Contracts;

namespace DeskMax.Server;

public sealed class DeviceRegistry(IConfiguration config)
{
    private readonly ConcurrentDictionary<string, Device> devices = new();
    private readonly ConcurrentDictionary<Guid, Session> sessions = new();
    private readonly byte[] key = ReadKey(config);

    private static byte[] ReadKey(IConfiguration config)
    {
        var encoded = config["Security:RotatingCodeKey"];
        if (string.IsNullOrWhiteSpace(encoded)) throw new InvalidOperationException("Security:RotatingCodeKey is required.");
        var value = Convert.FromBase64String(encoded);
        if (value.Length < 32) throw new InvalidOperationException("Security:RotatingCodeKey must contain at least 32 bytes.");
        return value;
    }

    public RegisterDeviceResponse Register(RegisterDeviceRequest request)
    {
        string id; do id = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(); while (devices.ContainsKey(id));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        devices[id] = new(id, SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        return new(id, secret, DateTimeOffset.UtcNow);
    }

    public RotatingCodeResponse Code(string id, string secret, DateTimeOffset now)
    {
        Authenticate(id, secret);
        var window = now.ToUnixTimeSeconds() / 1800;
        using var hmac = new HMACSHA256(key);
        var code = (BitConverter.ToUInt32(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}:{window}"))) % 1000000).ToString("D6");
        return new(id, code, DateTimeOffset.FromUnixTimeSeconds((window + 1) * 1800));
    }

    public SessionResponse Request(CreateSessionRequest request, DateTimeOffset now)
    {
        if (!devices.TryGetValue(request.RequesterDeviceId, out _)) throw new KeyNotFoundException("Requester not found");
        var target = devices.Keys.FirstOrDefault(id => CodeInternal(id, now) == request.TargetCode) ?? throw new KeyNotFoundException("Code invalid or expired");
        var status = devices[target].Trusted.ContainsKey(request.RequesterDeviceId) ? "approved" : "pending-owner-confirmation";
        var session = new Session(Guid.NewGuid(), request.RequesterDeviceId, target, status, now.AddMinutes(2));
        sessions[session.Id] = session;
        return session.Response();
    }

    public SessionResponse Approve(Guid id, ApproveSessionRequest request)
    {
        var session = sessions[id]; Authenticate(session.TargetId, request.DeviceSecret);
        if (session.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Request expired");
        session.Status = "approved"; return session.Response();
    }

    public void Grant(string ownerId, GrantAccessRequest request)
    {
        Authenticate(ownerId, request.OwnerDeviceSecret);
        if (!devices.ContainsKey(request.TrustedDeviceId)) throw new KeyNotFoundException("Trusted device not found");
        devices[ownerId].Trusted[request.TrustedDeviceId] = request.ExpiresAt;
    }

    private string CodeInternal(string id, DateTimeOffset now)
    {
        var window = now.ToUnixTimeSeconds() / 1800; using var hmac = new HMACSHA256(key);
        return (BitConverter.ToUInt32(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}:{window}"))) % 1000000).ToString("D6");
    }
    private void Authenticate(string id, string secret)
    {
        if (!devices.TryGetValue(id, out var d) || !CryptographicOperations.FixedTimeEquals(d.SecretHash, SHA256.HashData(Encoding.UTF8.GetBytes(secret))))
            throw new UnauthorizedAccessException();
    }
    private sealed record Device(string Id, byte[] SecretHash) { public ConcurrentDictionary<string, DateTimeOffset?> Trusted { get; } = new(); }
    private sealed record Session(Guid Id, string RequesterId, string TargetId, DateTimeOffset ExpiresAt)
    {
        public string Status { get; set; } = "pending-owner-confirmation";
        public Session(Guid id, string requester, string target, string status, DateTimeOffset expires) : this(id, requester, target, expires) => Status = status;
        public SessionResponse Response() => new(Id, Status, TargetId, ExpiresAt);
    }
}
