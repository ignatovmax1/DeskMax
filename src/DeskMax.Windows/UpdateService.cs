using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
namespace DeskMax.Windows;
public sealed class UpdateService
{
    private const string Endpoint = "https://api.github.com/repos/ignatovmax1/DeskMax/releases/latest";
    private readonly HttpClient http;
    private readonly Version Current;
    public UpdateService(HttpClient? client = null, Version? currentVersion = null)
    {
        http = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Current = currentVersion ?? Assembly.GetExecutingAssembly().GetName().Version ?? new(0, 0);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeskMax", Current.ToString()));
    }
    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(Endpoint, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken)); var root = json.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var preview) && preview.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "0.0.0"; if (!Version.TryParse(tag, out var version) || new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)) <= new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build), Math.Max(0, Current.Revision))) return null;
        string? installer = null, checksum = null; foreach (var a in root.GetProperty("assets").EnumerateArray()) { var name = a.GetProperty("name").GetString(); var url = a.GetProperty("browser_download_url").GetString(); if (name == "DeskMaxSetup.exe") installer = url; else if (name == "DeskMaxSetup.exe.sha256") checksum = url; }
        if (installer is null || checksum is null) throw new InvalidDataException("В релизе нет установщика или SHA-256.");
        ValidateUrl(installer); ValidateUrl(checksum); return new(tag, installer, checksum);
    }
    private static void ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.AbsolutePath.StartsWith("/ignatovmax1/DeskMax/releases/download/", StringComparison.Ordinal)) throw new InvalidDataException("Недопустимый адрес обновления.");
    }
    public async Task DownloadVerifyAndLaunchAsync(UpdateInfo update, CancellationToken cancellationToken = default)
    {
        ValidateUrl(update.InstallerUrl); ValidateUrl(update.ChecksumUrl);
        var parts = (await http.GetStringAsync(update.ChecksumUrl, cancellationToken)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new InvalidDataException("Пустая контрольная сумма.");
        var expected = parts[0];
        if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Некорректная контрольная сумма.");
        var path = Path.Combine(Path.GetTempPath(), $"DeskMaxSetup-{Guid.NewGuid():N}.exe");
        try
        {
        using (var response = await http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, cancellationToken);
        }
        string actual;
        await using (var file = File.OpenRead(path)) actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("SHA-256 обновления не совпала.");
        cancellationToken.ThrowIfCancellationRequested();
        if (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }) is null) throw new InvalidOperationException("Не удалось запустить установщик.");
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
}
public sealed record UpdateInfo(string Version, string InstallerUrl, string ChecksumUrl);
