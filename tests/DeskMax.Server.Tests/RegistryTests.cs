using DeskMax.Contracts;
using Microsoft.Extensions.Configuration;
namespace DeskMax.Server.Tests;
[TestClass]
public class RegistryTests
{
    private static DeviceRegistry Registry() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Security:RotatingCodeKey"] = Convert.ToBase64String(new byte[32]) }).Build());
    [TestMethod]
    public void SpoofedIdentityIsRejected()
    {
        var db = Registry(); var owner = db.Register(new("Owner", "Windows")); var client = db.Register(new("Client", "Android")); var now = DateTimeOffset.UtcNow;
        db.Grant(owner.DeviceId, new(owner.DeviceSecret, client.DeviceId, null));
        var code = db.Code(owner.DeviceId, owner.DeviceSecret, now);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => db.Request(new(code.Code, client.DeviceId, "wrong"), now));
    }
    [TestMethod]
    public void TrustRequiresConsentAndSupportsRevocation()
    {
        var db = Registry(); var owner = db.Register(new("Owner", "Windows")); var client = db.Register(new("Client", "Android")); var now = DateTimeOffset.UtcNow;
        var request = new CreateSessionRequest(db.Code(owner.DeviceId, owner.DeviceSecret, now).Code, client.DeviceId, client.DeviceSecret);
        Assert.AreEqual("pending-owner-confirmation", db.Request(request, now).Status);
        db.Grant(owner.DeviceId, new(owner.DeviceSecret, client.DeviceId, null));
        Assert.AreEqual("approved", db.Request(request, now).Status);
        db.Revoke(owner.DeviceId, owner.DeviceSecret, client.DeviceId);
        Assert.AreEqual("pending-owner-confirmation", db.Request(request, now).Status);
    }
    [TestMethod]
    public void ExpiredTrustDoesNotApprove()
    {
        var db = Registry(); var owner = db.Register(new("Owner", "Windows")); var client = db.Register(new("Client", "Windows")); var now = DateTimeOffset.UtcNow;
        db.Grant(owner.DeviceId, new(owner.DeviceSecret, client.DeviceId, now.AddSeconds(1)));
        var later = now.AddSeconds(2);
        Assert.AreEqual("pending-owner-confirmation", db.Request(new(db.Code(owner.DeviceId, owner.DeviceSecret, later).Code, client.DeviceId, client.DeviceSecret), later).Status);
    }
}
