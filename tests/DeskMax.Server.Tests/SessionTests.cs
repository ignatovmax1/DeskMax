using DeskMax.Contracts;
using Microsoft.Extensions.Configuration;
namespace DeskMax.Server.Tests;
[TestClass]
public class SessionTests
{
    private static DeviceRegistry Registry() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Security:RotatingCodeKey"] = Convert.ToBase64String(new byte[32]) }).Build());
    private static SessionResponse Request(DeviceRegistry db, RegisterDeviceResponse owner, RegisterDeviceResponse client, DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.UtcNow;
        return db.Request(new(db.Code(owner.DeviceId, owner.DeviceSecret, time).Code, client.DeviceId, client.DeviceSecret), time);
    }
    [TestMethod]
    public void OwnerCanDiscoverAndRejectRequestAndRequesterCanPoll()
    {
        var db = Registry(); var owner = db.Register(new("Owner", "Windows")); var client = db.Register(new("Phone", "Android"));
        var session = Request(db, owner, client);
        var incoming = db.Incoming(owner.DeviceId, owner.DeviceSecret);
        Assert.HasCount(1, incoming);
        Assert.AreEqual("Phone", incoming[0].RequesterDeviceName);
        Assert.AreEqual(session.SessionId, incoming[0].SessionId);
        Assert.AreEqual("rejected", db.Reject(session.SessionId, new(owner.DeviceSecret)).Status);
        Assert.IsEmpty(db.Incoming(owner.DeviceId, owner.DeviceSecret));
        Assert.AreEqual("rejected", db.Status(session.SessionId, new(client.DeviceId, client.DeviceSecret)).Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => db.Approve(session.SessionId, new(owner.DeviceSecret)));
    }
    [TestMethod]
    public void RevokedRequestCannotBeApprovedAgain()
    {
        var db = Registry(); var owner = db.Register(new("Owner", "Windows")); var client = db.Register(new("Client", "Windows"));
        var session = Request(db, owner, client);
        db.Revoke(owner.DeviceId, owner.DeviceSecret, client.DeviceId);
        Assert.ThrowsExactly<InvalidOperationException>(() => db.Approve(session.SessionId, new(owner.DeviceSecret)));
        Assert.AreEqual("revoked", db.Status(session.SessionId, new(client.DeviceId, client.DeviceSecret)).Status);
    }
    [TestMethod]
    public void OtherDevicesCannotReadOrDecideSessions()
    {
        var db = Registry(); var owner = db.Register(new("Owner", "Windows")); var client = db.Register(new("Client", "Windows")); var stranger = db.Register(new("Stranger", "Windows"));
        var session = Request(db, owner, client);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => db.Incoming(owner.DeviceId, stranger.DeviceSecret));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => db.Status(session.SessionId, new(stranger.DeviceId, stranger.DeviceSecret)));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => db.Approve(session.SessionId, new(client.DeviceSecret)));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => db.Reject(session.SessionId, new(client.DeviceSecret)));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => db.Code(null!, owner.DeviceSecret, DateTimeOffset.UtcNow));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => db.Code(owner.DeviceId, null!, DateTimeOffset.UtcNow));
    }
    [TestMethod]
    public void ExpiredRequestsAreHiddenAndCannotBeApproved()
    {
        var db = Registry(); var owner = db.Register(new("Owner", "Windows")); var client = db.Register(new("Client", "Windows"));
        var session = Request(db, owner, client, DateTimeOffset.UtcNow.AddMinutes(-3));
        Assert.IsEmpty(db.Incoming(owner.DeviceId, owner.DeviceSecret));
        Assert.AreEqual("expired", db.Status(session.SessionId, new(client.DeviceId, client.DeviceSecret)).Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => db.Approve(session.SessionId, new(owner.DeviceSecret)));
    }
    [TestMethod]
    public void ConcurrentDecisionsHaveOnlyOneWinner()
    {
        var db = Registry(); var owner = db.Register(new("Owner", "Windows")); var client = db.Register(new("Client", "Windows"));
        var session = Request(db, owner, client); var wins = 0;
        Parallel.For(0, 20, i => { try { if (i % 2 == 0) db.Approve(session.SessionId, new(owner.DeviceSecret)); else db.Reject(session.SessionId, new(owner.DeviceSecret)); Interlocked.Increment(ref wins); } catch (InvalidOperationException) { } });
        Assert.AreEqual(1, wins);
    }
}
