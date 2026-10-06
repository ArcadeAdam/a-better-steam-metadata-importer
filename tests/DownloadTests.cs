using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamMetadataImporter;

internal static partial class Program
{
    private const string TestMediaUrl = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4685520/header.jpg";

    private static void RegisterDownloadTests()
    {
        Test("Image bytes determine output format and checksum", VerifiedImage);
        Test("HTTP and HTML failures leave existing staging files untouched", FailedImage);
        Test("Failed image sources fall back to a valid image", ImageFallback);
        Test("Redirects outside Steam are rejected before contacting destination", UnsafeRedirect);
        Test("Cancellation propagates without leaving temporary downloads", DownloadCancellation);
    }

    private static byte[] PngBytes()
    {
        using var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.MidnightBlue);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static SteamAsset ImageAsset(params string[] sources) => new() { Key = "fixture", Label = "Test image", ImageType = "Banner", Sources = sources.ToList() };

    private static async Task VerifiedImage()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            byte[] bytes = PngBytes();
            using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            using var downloader = new MediaDownloader(fixture, http);
            var result = await downloader.DownloadAsync(ImageAsset(TestMediaUrl), fixture, null, CancellationToken.None);
            Check(result.Success, result.Error ?? "download failed");
            Equal(".png", Path.GetExtension(result.FilePath), "PNG response to .jpg URL");
            Check(bytes.SequenceEqual(File.ReadAllBytes(result.FilePath!)), "verified image bytes changed");
            Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Sha256, "SHA256");
            Check(!Directory.EnumerateDirectories(fixture).Any(), "temporary work directory remained");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static async Task FailedImage()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string sentinel = Path.Combine(fixture, "keep.txt");
            File.WriteAllText(sentinel, "caller-owned");
            foreach (bool notFound in new[] { true, false })
            {
                using var http = new HttpClient(new StubHandler(_ => notFound ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html>Temporary Steam error</html>") }));
                using var downloader = new MediaDownloader(fixture, http);
                var result = await downloader.DownloadAsync(ImageAsset(TestMediaUrl), fixture, null, CancellationToken.None);
                Check(!result.Success && result.FilePath == null && !string.IsNullOrWhiteSpace(result.Error), "bad content reported success");
                Equal("caller-owned", File.ReadAllText(sentinel), "existing staging file preserved");
                Equal(1, Directory.EnumerateFileSystemEntries(fixture).Count(), "failed download left files");
            }
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static async Task ImageFallback()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            int requests = 0;
            using var http = new HttpClient(new StubHandler(_ => ++requests == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes()) }));
            using var downloader = new MediaDownloader(fixture, http);
            var result = await downloader.DownloadAsync(ImageAsset(TestMediaUrl + "?missing", TestMediaUrl), fixture, null, CancellationToken.None);
            Check(result.Success, result.Error ?? "fallback failed");
            Equal(2, requests, "fallback request count");
            Equal(TestMediaUrl, result.Source, "winning source");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static async Task UnsafeRedirect()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            int requests = 0;
            using var http = new HttpClient(new StubHandler(_ => { requests++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://evil.invalid/not-steam.png"); return response; }));
            using var downloader = new MediaDownloader(fixture, http);
            var result = await downloader.DownloadAsync(ImageAsset(TestMediaUrl), fixture, null, CancellationToken.None);
            Check(!result.Success, "unsafe redirect accepted");
            Equal(1, requests, "redirect destination was contacted");
            Check(!Directory.EnumerateFileSystemEntries(fixture).Any(), "rejected redirect left files");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static async Task DownloadCancellation()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Cancelled request contacted network")));
            using var downloader = new MediaDownloader(fixture, http);
            await ThrowsAsync<OperationCanceledException>(() => downloader.DownloadAsync(ImageAsset(TestMediaUrl), fixture, null, cancellation.Token));
            Check(!Directory.EnumerateFileSystemEntries(fixture).Any(), "cancelled download left files");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static string Option(string[] args, string name, string fallback)
    {
        int index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    private static async Task LiveCheck(string[] args)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        string root = Option(args, "--launchbox-root", "");
        string output = Option(args, "--output", Path.Combine(AppContext.BaseDirectory, "live-output", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        SteamAsset? downloadAsset = null;
        if (args.Contains("--live-media", StringComparer.OrdinalIgnoreCase))
        {
            string url = Option(args, "--media-url", "");
            Check(url.Length > 0, "--live-media requires --media-url");
            downloadAsset = new SteamAsset { Key = "live-trailer", Label = "Live trailer", IsVideo = true, Sources = new List<string> { url } };
        }
        else
        {
            int appId = int.Parse(Option(args, "--app-id", "4685520"));
            using var client = new SteamClient();
            var details = await client.GetDetailsAsync(appId, cancellation.Token);
            Console.WriteLine($"LIVE {details.AppId} {details.Name}: {details.Assets.Count} assets; {details.Assets.Count(a => a.IsVideo)} full trailers");
            foreach (var asset in details.Assets) Console.WriteLine($"  {asset.Key}: {asset.Sources.FirstOrDefault()}");
            foreach (string warning in details.Warnings) Console.WriteLine("  WARNING " + warning);
            if (args.Contains("--download-video", StringComparer.OrdinalIgnoreCase))
                downloadAsset = details.Assets.Where(a => a.IsVideo).OrderByDescending(a => a.Highlight).First();
            else if (args.Contains("--download-image", StringComparer.OrdinalIgnoreCase))
                downloadAsset = details.Assets.First(a => a.Key == "portrait-front");
        }
        if (downloadAsset == null) return;
        if (downloadAsset.IsVideo)
            Check(!string.IsNullOrWhiteSpace(root), "Live trailer downloads require --launchbox-root pointing to a LaunchBox installation with ThirdParty/FFMPEG/ffmpeg.exe and ffprobe.exe.");
        using var downloader = new MediaDownloader(string.IsNullOrWhiteSpace(root) ? AppContext.BaseDirectory : root);
        var result = await downloader.DownloadAsync(downloadAsset, output, new Progress<string>(Console.WriteLine), cancellation.Token);
        Check(result.Success, result.Error ?? "Live download failed");
        Console.WriteLine("VERIFIED " + result.FilePath + " SHA256=" + result.Sha256);
        if (!downloadAsset.IsVideo) return;
        var start = new ProcessStartInfo(Path.Combine(root, "ThirdParty", "FFMPEG", "ffprobe.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height:format=duration,format_name", "-of", "json", result.FilePath! }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start ffprobe");
        string probe = await process.StandardOutput.ReadToEndAsync(cancellation.Token);
        string error = await process.StandardError.ReadToEndAsync(cancellation.Token);
        await process.WaitForExitAsync(cancellation.Token);
        Check(process.ExitCode == 0, "ffprobe failed: " + error);
        Console.WriteLine(probe);
        using var parsed = JsonDocument.Parse(probe);
        var streams = parsed.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        Check(streams.Any(s => s.GetProperty("codec_type").GetString() == "video"), "video stream absent");
        Check(streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"), "audio stream absent");
    }
}
