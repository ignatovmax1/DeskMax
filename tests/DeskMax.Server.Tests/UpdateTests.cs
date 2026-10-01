using System.Net;
using System.Text.Json;
using DeskMax.Windows;

namespace DeskMax.Server.Tests;

[TestClass]
public class UpdateTests
{
    private const string Download = "https://github.com/ignatovmax1/DeskMax/releases/download/v0.2.0/";
    private static UpdateService Service(string tag, bool preview = false, bool assets = true, string? url = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease = preview,
            assets = assets ? new[] {
                new { name = "DeskMaxSetup.exe", browser_download_url = url ?? Download + "DeskMaxSetup.exe" },
                new { name = "DeskMaxSetup.exe.sha256", browser_download_url = Download + "DeskMaxSetup.exe.sha256" }
            } : []
        });
        return new(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) })), new Version(0, 1, 0, 0));
    }
    [TestMethod] public async Task SameVersionWithDifferentComponentCountIsNotUpdate() => Assert.IsNull(await Service("v0.1.0").CheckAsync());
    [TestMethod] public async Task StableUpdateIsSelected() => Assert.AreEqual("0.2.0", (await Service("v0.2.0").CheckAsync())!.Version);
    [TestMethod] public async Task PreviewIsNotOffered() => Assert.IsNull(await Service("v0.2.0", preview: true).CheckAsync());
    [TestMethod] public async Task MissingAssetsAreRejected() => await Assert.ThrowsExactlyAsync<System.IO.InvalidDataException>(() => Service("v0.2.0", assets: false).CheckAsync());
    [TestMethod] public async Task ForeignDownloadIsRejected() => await Assert.ThrowsExactlyAsync<System.IO.InvalidDataException>(() => Service("v0.2.0", url: "https://example.org/setup.exe").CheckAsync());
    [TestMethod] public async Task NoReleaseIsHandled()
    {
        var service = new UpdateService(new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound))));
        Assert.IsNull(await service.CheckAsync());
    }
    [TestMethod] public async Task EmptyChecksumDoesNotDownloadInstaller()
    {
        var calls = 0;
        var service = new UpdateService(new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.OK) { Content = new StringContent("") }; })));
        await Assert.ThrowsExactlyAsync<System.IO.InvalidDataException>(() => service.DownloadVerifyAndLaunchAsync(new("0.2.0", Download + "DeskMaxSetup.exe", Download + "DeskMaxSetup.exe.sha256")));
        Assert.AreEqual(1, calls);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
