using System.Security.Cryptography;
using System.Text;
using DeskMax.Contracts;

namespace DeskMax.Server;

public sealed class DeviceRegistry(IConfiguration config)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Device> devices = new();
    private readonly Dictionary<Guid, Session> sessions = new();
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
        if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 100 || request.Platform is not ("Windows" or "Android")) throw new ArgumentException("Invalid device name or platform.");
        lock (gate)
        {
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            string id;
            do id = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(); while (devices.ContainsKey(id));
            devices.Add(id, new(request.DeviceName.Trim(), SHA256.HashData(Encoding.UTF8.GetBytes(secret))));
            return new(id, secret, DateTimeOffset.UtcNow);
        }
    }

    public RotatingCodeResponse Code(string id, string secret, DateTimeOffset now)
    {
        lock (gate)
        {
            Authenticate(id, secret);
            var window = now.ToUnixTimeSeconds() / 1800;
            return new(id, CodeInternal(id, now), DateTimeOffset.FromUnixTimeSeconds((window + 1) * 1800));
        }
    }

    public SessionResponse Request(CreateSessionRequest request, DateTimeOffset now)
    {
        lock (gate)
        {
            Authenticate(request.RequesterDeviceId, request.RequesterDeviceSecret);
            if (request.TargetCode is null || request.TargetCode.Length != 6 || request.TargetCode.Any(c => c < '0' || c > '9')) throw new ArgumentException("Expected six digits.");
            var matches = devices.Keys.Where(id => CodeInternal(id, now) == request.TargetCode).Take(2).ToArray();
            if (matches.Length != 1) throw new KeyNotFoundException("Code invalid, ambiguous or expired.");
            var target = matches[0];
            if (target == request.RequesterDeviceId) throw new ArgumentException("Cannot connect to self.");
            foreach (var old in sessions.Where(s => s.Value.ExpiresAt <= now).Select(s => s.Key).ToArray()) sessions.Remove(old);
            var trusted = devices[target].Trusted.TryGetValue(request.RequesterDeviceId, out var expiry) && (expiry is null || expiry > now);
            var session = new Session(Guid.NewGuid(), request.RequesterDeviceId, target, trusted ? "approved" : "pending-owner-confirmation", trusted ? now.AddHours(1) : now.AddMinutes(2));
            sessions.Add(session.Id, session);
            return session.Response(now);
        }
    }

    public SessionResponse Approve(Guid id, ApproveSessionRequest request) => Decide(id, request, "approved");
    public SessionResponse Reject(Guid id, ApproveSessionRequest request) => Decide(id, request, "rejected");
    private SessionResponse Decide(Guid id, ApproveSessionRequest request, string decision)
    {
        lock (gate)
        {
            var session = FindSession(id);
            Authenticate(session.TargetId, request.DeviceSecret);
            if (session.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Request expired.");
            if (session.Status != "pending-owner-confirmation") throw new InvalidOperationException("Request has already been decided or revoked.");
            session.Status = decision;
            if (decision == "approved") session.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
            return session.Response(DateTimeOffset.UtcNow);
        }
    }

    public IncomingSessionResponse[] Incoming(string ownerId, string secret)
    {
        lock (gate)
        {
            Authenticate(ownerId, secret);
            var now = DateTimeOffset.UtcNow;
            return sessions.Values.Where(s => s.TargetId == ownerId && s.ExpiresAt > now && s.Status == "pending-owner-confirmation")
                .OrderBy(s => s.ExpiresAt).Select(s => new IncomingSessionResponse(s.Id, s.RequesterId, devices[s.RequesterId].Name, s.Status, s.ExpiresAt)).ToArray();
        }
    }

    public SessionResponse Status(Guid id, SessionCredentialsRequest request)
    {
        lock (gate)
        {
            Authenticate(request.DeviceId, request.DeviceSecret);
            var session = FindSession(id);
            if (request.DeviceId != session.RequesterId && request.DeviceId != session.TargetId) throw new UnauthorizedAccessException();
            return session.Response(DateTimeOffset.UtcNow);
        }
    }

    public string AuthorizeTransport(Guid id, SessionCredentialsRequest request)
    {
        lock (gate)
        {
            var response = Status(id, request);
            if (response.Status != "approved") throw new InvalidOperationException("Session is not approved or has expired.");
            return sessions[id].TargetId == request.DeviceId ? "host" : "viewer";
        }
    }
    public SessionResponse Stop(Guid id, SessionCredentialsRequest request)
    {
        lock (gate)
        {
            Status(id, request);
            sessions[id].Status = "ended";
            return sessions[id].Response(DateTimeOffset.UtcNow);
        }
    }
    public void EndTransport(Guid id)
    {
        lock (gate) if (sessions.TryGetValue(id, out var session) && session.Status == "approved") session.Status = "ended";
    }
    public void Grant(string ownerId, GrantAccessRequest request)
    {
        lock (gate)
        {
            Authenticate(ownerId, request.OwnerDeviceSecret);
            if (string.IsNullOrEmpty(request.TrustedDeviceId) || !devices.ContainsKey(request.TrustedDeviceId)) throw new KeyNotFoundException("Trusted device not found.");
            if (ownerId == request.TrustedDeviceId || request.ExpiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("Invalid trust grant.");
            devices[ownerId].Trusted[request.TrustedDeviceId] = request.ExpiresAt;
        }
    }

    public void Revoke(string ownerId, string secret, string trustedId)
    {
        lock (gate)
        {
            Authenticate(ownerId, secret);
            if (string.IsNullOrEmpty(trustedId)) throw new ArgumentException("Trusted device is required.");
            devices[ownerId].Trusted.Remove(trustedId);
            foreach (var session in sessions.Values.Where(s => s.TargetId == ownerId && s.RequesterId == trustedId)) session.Status = "revoked";
        }
    }

    private Session FindSession(Guid id) => sessions.TryGetValue(id, out var session) ? session : throw new KeyNotFoundException("Session not found.");
    private string CodeInternal(string id, DateTimeOffset now)
    {
        var window = now.ToUnixTimeSeconds() / 1800;
        using var hmac = new HMACSHA256(key);
        return (BitConverter.ToUInt32(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}:{window}"))) % 1000000).ToString("D6");
    }
    private void Authenticate(string id, string secret)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret) || !devices.TryGetValue(id, out var d) || !CryptographicOperations.FixedTimeEquals(d.SecretHash, SHA256.HashData(Encoding.UTF8.GetBytes(secret)))) throw new UnauthorizedAccessException();
    }
    private sealed record Device(string Name, byte[] SecretHash) { public Dictionary<string, DateTimeOffset?> Trusted { get; } = new(); }
    private sealed record Session(Guid Id, string RequesterId, string TargetId, DateTimeOffset InitialExpiresAt)
    {
        public DateTimeOffset ExpiresAt { get; set; } = InitialExpiresAt;
        public string Status { get; set; } = "pending-owner-confirmation";
        public Session(Guid id, string requester, string target, string status, DateTimeOffset expires) : this(id, requester, target, expires) => Status = status;
        public SessionResponse Response(DateTimeOffset now) => new(Id, ExpiresAt <= now ? "expired" : Status, TargetId, ExpiresAt);
    }
}
