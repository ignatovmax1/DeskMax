using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
namespace DeskMax.Windows;
public sealed class UpdateService
{
    private const string Endpoint = "https://api.github.com/repos/ignatovmax1/DeskMax/releases/latest";
    private readonly HttpClient http = new();
    private static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new(0, 0);
    public UpdateService() => http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeskMax", Current.ToString()));
    public async Task<UpdateInfo?> CheckAsync()
    {
        using var response = await http.GetAsync(Endpoint); response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync()); var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "0.0.0"; if (!Version.TryParse(tag, out var version) || version <= Current) return null;
        string? installer = null, checksum = null; foreach (var a in root.GetProperty("assets").EnumerateArray()) { var name = a.GetProperty("name").GetString(); var url = a.GetProperty("browser_download_url").GetString(); if (name == "DeskMaxSetup.exe") installer = url; else if (name == "DeskMaxSetup.exe.sha256") checksum = url; }
        if (installer is null || checksum is null) throw new InvalidDataException("В релизе нет установщика или SHA-256."); return new(tag, installer, checksum);
    }
    public async Task DownloadVerifyAndLaunchAsync(UpdateInfo update)
    {
        var path = Path.Combine(Path.GetTempPath(), $"DeskMaxSetup-{update.Version}.exe"); await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(update.InstallerUrl));
        var expected = (await http.GetStringAsync(update.ChecksumUrl)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]; await using var file = File.OpenRead(path); var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) { File.Delete(path); throw new CryptographicException("SHA-256 обновления не совпала."); } Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
public sealed record UpdateInfo(string Version, string InstallerUrl, string ChecksumUrl);
