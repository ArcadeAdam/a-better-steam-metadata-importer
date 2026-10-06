using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SteamMetadataImporter;

/// <summary>Downloads verified media into a unique staging file without changing LaunchBox data.</summary>
public sealed class MediaDownloader : IDisposable
{
    private const long ImageLimit = 100L * 1024 * 1024;
    private const long VideoLimit = 2L * 1024 * 1024 * 1024;
    private const int ManifestLimit = 2 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string ffmpeg;
    private readonly string ffprobe;
    private readonly List<string> steamCacheRoots;

    public MediaDownloader(string launchBoxRoot) : this(launchBoxRoot, CreateHttpClient(), true) { }

    /// <summary>Allows a caller to supply a no-auto-redirect client; the caller retains ownership.</summary>
    public MediaDownloader(string launchBoxRoot, HttpClient client) : this(launchBoxRoot, client, false) { }

    private MediaDownloader(string launchBoxRoot, HttpClient client, bool ownsClient)
    {
        if (string.IsNullOrWhiteSpace(launchBoxRoot)) throw new ArgumentException("A LaunchBox root is required.", nameof(launchBoxRoot));
        string root = Path.GetFullPath(launchBoxRoot);
        ffmpeg = Path.Combine(root, "ThirdParty", "FFMPEG", "ffmpeg.exe");
        ffprobe = Path.Combine(root, "ThirdParty", "FFMPEG", "ffprobe.exe");
        http = client ?? throw new ArgumentNullException(nameof(client));
        ownsHttp = ownsClient;
        steamCacheRoots = FindSteamCacheRoots();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
        { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LaunchBox-ABetterSteamMetadataImporter/1.1");
        return client;
    }

    public async Task<DownloadResult> DownloadAsync(SteamAsset asset, string stagingDirectory,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(stagingDirectory)) throw new ArgumentException("A staging directory is required.");
        string staging = Path.GetFullPath(stagingDirectory);
        Directory.CreateDirectory(staging);
        var errors = new List<string>();
        foreach (string source in asset.Sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Every temporary path belongs to this new attempt; no caller-owned files are removed.
            string work = Path.Combine(staging, ".steam-download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(asset.IsVideo ? 20 : 3));
            try
            {
                progress?.Report("Downloading " + asset.Label + "…");
                string candidate = Path.Combine(work, "download.part");
                string extension;
                if (Path.IsPathFullyQualified(source) && !source.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                {
                    if (asset.IsVideo) throw new InvalidDataException("Local video sources are not supported; use an official Steam trailer URL.");
                    string local = ValidateLocalArtwork(source);
                    await using var input = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    await using (var output = NewFile(candidate))
                        await CopyLimitedAsync(input, output, ImageLimit, timeout.Token).ConfigureAwait(false);
                    extension = ValidateImage(candidate);
                }
                else
                {
                    Uri uri = ValidateSteamUri(source);
                    string suffix = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                    if (asset.IsVideo && suffix == ".mpd")
                    {
                        RequireVideoTools();
                        candidate = await DownloadDashAsync(uri, work, progress, timeout.Token).ConfigureAwait(false);
                        extension = await ValidateVideoAsync(candidate, true, timeout.Token).ConfigureAwait(false);
                    }
                    else if (asset.IsVideo && suffix == ".m3u8")
                    {
                        RequireVideoTools();
                        candidate = await DownloadHlsAsync(uri, work, progress, timeout.Token).ConfigureAwait(false);
                        extension = await ValidateVideoAsync(candidate, true, timeout.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await DownloadFileAsync(uri, candidate, asset.IsVideo ? VideoLimit : ImageLimit, timeout.Token).ConfigureAwait(false);
                        extension = asset.IsVideo
                            ? await ValidateVideoAsync(candidate, false, timeout.Token).ConfigureAwait(false)
                            : ValidateImage(candidate);
                    }
                }
                string hash;
                await using (var verified = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    hash = Convert.ToHexString(await SHA256.HashDataAsync(verified, timeout.Token).ConfigureAwait(false)).ToLowerInvariant();
                timeout.Token.ThrowIfCancellationRequested();
                string final = Path.Combine(staging, Guid.NewGuid().ToString("N") + extension);
                File.Move(candidate, final, false);
                progress?.Report("Verified " + asset.Label + ".");
                return new DownloadResult { Success = true, FilePath = final, Source = source, Sha256 = hash };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { errors.Add("Source timed out."); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or XmlException or JsonException or InvalidOperationException or FormatException or OverflowException or System.ComponentModel.Win32Exception)
            { errors.Add(ex.Message); }
            finally
            {
                // Both paths are canonical and work is a generated immediate child of staging.
                if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(work)), Path.TrimEndingDirectorySeparator(staging), StringComparison.OrdinalIgnoreCase))
                    TryDeleteDirectory(work);
            }
        }
        return new DownloadResult { Success = false, Error = errors.Count == 0 ? "No media sources were supplied." : string.Join(" | ", errors.Distinct().Take(6)) };
    }

    private static Uri ValidateSteamUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.IsLoopback ||
            Uri.CheckHostName(uri.IdnHost) != UriHostNameType.Dns)
            throw new InvalidDataException("Media must use an HTTPS Steam CDN URL without credentials or a custom port.");
        string host = uri.IdnHost.TrimEnd('.');
        string[] roots = { "steamstatic.com", "steamusercontent.com", "steamcdn-a.akamaihd.net" };
        if (!roots.Any(root => host.Equals(root, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Media host is not a permitted Steam CDN: " + host);
        return uri;
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct)
    {
        for (int redirect = 0; redirect <= 8; redirect++)
        {
            ValidateSteamUri(uri.AbsoluteUri);
            HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new HttpRequestException("Steam CDN redirect has no destination.");
                uri = ValidateSteamUri(new Uri(uri, location).AbsoluteUri);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new HttpRequestException("Steam CDN returned HTTP " + status + ".");
            }
            return response;
        }
        throw new HttpRequestException("Too many Steam CDN redirects.");
    }

    private async Task<(string Text, Uri Uri)> GetManifestAsync(Uri uri, CancellationToken ct)
    {
        using HttpResponseMessage response = await GetAsync(uri, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > ManifestLimit) throw new InvalidDataException("Steam manifest is too large.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        await CopyLimitedAsync(stream, bytes, ManifestLimit, ct).ConfigureAwait(false);
        return (Encoding.UTF8.GetString(bytes.ToArray()).TrimStart('\uFEFF'), response.RequestMessage?.RequestUri ?? uri);
    }

    private async Task DownloadFileAsync(Uri uri, string destination, long limit, CancellationToken ct)
    {
        await using var output = NewFile(destination);
        await AppendUrlAsync(uri, output, limit, ct).ConfigureAwait(false);
    }

    private async Task AppendUrlAsync(Uri uri, Stream output, long remaining, CancellationToken ct)
    {
        using HttpResponseMessage response = await GetAsync(uri, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > remaining) throw new InvalidDataException("Steam media exceeds the download size limit.");
        await using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await CopyLimitedAsync(input, output, remaining, ct).ConfigureAwait(false);
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("Steam media exceeds the download size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        if (total == 0) throw new InvalidDataException("Steam returned an empty media file.");
    }

    private static FileStream NewFile(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);

    private string ValidateLocalArtwork(string source)
    {
        if (!Path.IsPathFullyQualified(source) || source.StartsWith("\\\\", StringComparison.Ordinal) || source.StartsWith("\\?", StringComparison.Ordinal))
            throw new InvalidDataException("Local media must be an absolute file path in the installed Steam artwork cache.");
        string full = Path.GetFullPath(source);
        if (!steamCacheRoots.Any(root => full.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Local artwork is outside the installed Steam appcache/librarycache directory.");
        // Reparse points could escape a permitted cache, so reject them along the path.
        for (FileSystemInfo? item = new FileInfo(full); item != null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked Steam cache paths are not accepted.");
        return full;
    }

    private static List<string> FindSteamCacheRoots()
    {
        var installs = new List<string>();
        try
        {
            string? path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (!string.IsNullOrWhiteSpace(path)) installs.Add(path);
            path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (!string.IsNullOrWhiteSpace(path)) installs.Add(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        installs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return installs.Select(path => Path.GetFullPath(Path.Combine(path, "appcache", "librarycache")) + Path.DirectorySeparatorChar)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ValidateImage(string path)
    {
        byte[] head = new byte[16];
        using (FileStream file = File.OpenRead(path)) file.ReadExactly(head);
        string extension;
        Guid expected;
        if (head[0] == 0x89 && Encoding.ASCII.GetString(head, 1, 3) == "PNG") { extension = ".png"; expected = ImageFormat.Png.Guid; }
        else if (head[0] == 0xff && head[1] == 0xd8 && head[2] == 0xff) { extension = ".jpg"; expected = ImageFormat.Jpeg.Guid; }
        else if (Encoding.ASCII.GetString(head, 0, 3) == "GIF") { extension = ".gif"; expected = ImageFormat.Gif.Guid; }
        else if (head[0] == 'B' && head[1] == 'M') { extension = ".bmp"; expected = ImageFormat.Bmp.Guid; }
        else if ((head[0] == 'I' && head[1] == 'I' && head[2] == 42) || (head[0] == 'M' && head[1] == 'M' && head[3] == 42))
        { extension = ".tif"; expected = ImageFormat.Tiff.Guid; }
        else if (Encoding.ASCII.GetString(head, 0, 4) == "RIFF" && Encoding.ASCII.GetString(head, 8, 4) == "WEBP")
            throw new InvalidDataException("This Steam image is WebP; the importer requires a decodable PNG or JPEG fallback.");
        else throw new InvalidDataException("Steam response is not a supported image file.");
        try
        {
            using Image image = Image.FromFile(path, true);
            if (image.RawFormat.Guid != expected || image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > 100_000_000)
                throw new InvalidDataException("Steam image dimensions or format are invalid.");
            // Force decoding while the file is still open; headers alone do not establish a valid image.
            using var decoded = new Bitmap(image);
            _ = decoded.GetPixel(decoded.Width - 1, decoded.Height - 1);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        { throw new InvalidDataException("Steam image could not be decoded.", ex); }
        return extension;
    }

    private void RequireVideoTools()
    {
        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
            throw new InvalidDataException("LaunchBox ThirdParty/FFMPEG must contain ffmpeg.exe and ffprobe.exe to download and verify trailers.");
    }

    private async Task<string> DownloadDashAsync(Uri uri, string work, IProgress<string>? progress, CancellationToken ct)
    {
        (string text, Uri actual) = await GetManifestAsync(uri, ct).ConfigureAwait(false);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = ManifestLimit };
        using var input = new StringReader(text);
        using XmlReader reader = XmlReader.Create(input, settings);
        XDocument document = XDocument.Load(reader);
        XElement root = document.Root ?? throw new InvalidDataException("DASH manifest is empty.");
        if (root.Name.LocalName != "MPD" || (string?)root.Attribute("type") == "dynamic") throw new InvalidDataException("Only static Steam DASH trailers are supported.");
        XElement[] periods = root.Elements().Where(x => x.Name.LocalName == "Period").ToArray();
        if (periods.Length != 1) throw new InvalidDataException("Steam DASH trailer requires exactly one period.");
        XElement period = periods[0];
        string? durationValue = (string?)period.Attribute("duration") ?? (string?)root.Attribute("mediaPresentationDuration");
        double duration = durationValue == null ? 0 : XmlConvert.ToTimeSpan(durationValue).TotalSeconds;
        if (duration <= 0 || duration > 7200) throw new InvalidDataException("Steam DASH trailer has no bounded duration.");
        var representations = period.Descendants().Where(x => x.Name.LocalName == "Representation").ToList();
        XElement? video = representations.Where(x => Value(x, "codecs").StartsWith("avc", StringComparison.OrdinalIgnoreCase) && Number(x, "height") is > 0 and <= 1080)
            .OrderByDescending(x => Number(x, "height")).ThenByDescending(x => Number(x, "bandwidth")).FirstOrDefault();
        XElement? audio = representations.Where(x => Value(x, "codecs").StartsWith("mp4a", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => Number(x, "bandwidth")).FirstOrDefault();
        if (video == null || audio == null) throw new InvalidDataException("Steam DASH trailer has no H.264 video at 1080p or below with AAC audio.");
        progress?.Report("Downloading " + Number(video, "height") + "p trailer video and audio…");
        string videoFile = Path.Combine(work, "video.mp4"), audioFile = Path.Combine(work, "audio.mp4");
        await DownloadDashTrackAsync(actual, video, duration, videoFile, ct).ConfigureAwait(false);
        await DownloadDashTrackAsync(actual, audio, duration, audioFile, ct).ConfigureAwait(false);
        return await RemuxAsync(videoFile, audioFile, work, ct).ConfigureAwait(false);
    }

    private async Task DownloadDashTrackAsync(Uri manifest, XElement rep, double duration, string file, CancellationToken ct)
    {
        if (rep.AncestorsAndSelf().Any(x => x.Elements().Any(y => y.Name.LocalName == "ContentProtection")))
            throw new InvalidDataException("Encrypted Steam trailers are not supported.");
        Uri baseUri = manifest;
        foreach (XElement element in rep.AncestorsAndSelf().Reverse())
        {
            string? url = element.Elements().FirstOrDefault(x => x.Name.LocalName == "BaseURL")?.Value;
            if (!string.IsNullOrWhiteSpace(url)) baseUri = ValidateSteamUri(new Uri(baseUri, url.Trim()).AbsoluteUri);
        }
        XElement? template = rep.AncestorsAndSelf().SelectMany(x => x.Elements().Where(y => y.Name.LocalName == "SegmentTemplate")).FirstOrDefault();
        if (template == null)
        {
            if (baseUri == manifest) throw new InvalidDataException("Unsupported DASH segment addressing.");
            await DownloadFileAsync(baseUri, file, VideoLimit, ct).ConfigureAwait(false);
            return;
        }
        string initialization = (string?)template.Attribute("initialization") ?? throw new InvalidDataException("DASH initialization is missing.");
        string media = (string?)template.Attribute("media") ?? throw new InvalidDataException("DASH media template is missing.");
        long start = AttributeLong(template, "startNumber", 1), scale = AttributeLong(template, "timescale", 1);
        if (scale <= 0 || start < 0) throw new InvalidDataException("Invalid DASH segment numbering.");
        var segments = new List<(long Number, long Time)>();
        XElement? timeline = template.Elements().FirstOrDefault(x => x.Name.LocalName == "SegmentTimeline");
        if (timeline != null)
        {
            long time = 0, number = start;
            foreach (XElement item in timeline.Elements().Where(x => x.Name.LocalName == "S"))
            {
                time = AttributeLong(item, "t", time);
                long length = AttributeLong(item, "d", 0), repeat = AttributeLong(item, "r", 0);
                if (length <= 0 || repeat < 0 || repeat > 10000) throw new InvalidDataException("Unsupported DASH timeline.");
                for (long i = 0; i <= repeat; i++)
                {
                    if (segments.Count >= 10000) throw new InvalidDataException("DASH trailer contains too many segments.");
                    segments.Add((number++, time));
                    time = checked(time + length);
                }
            }
        }
        else
        {
            long length = AttributeLong(template, "duration", 0);
            if (length <= 0) throw new InvalidDataException("DASH segment duration is missing.");
            double countValue = Math.Ceiling(duration * scale / length);
            if (countValue < 1 || countValue > 10000) throw new InvalidDataException("DASH trailer segment count is invalid.");
            for (long i = 0; i < (long)countValue; i++) segments.Add((start + i, i * length));
        }
        await using var output = NewFile(file);
        await AppendUrlAsync(ResolveTemplate(baseUri, initialization, rep, start, 0), output, VideoLimit, ct).ConfigureAwait(false);
        foreach ((long number, long time) in segments)
            await AppendUrlAsync(ResolveTemplate(baseUri, media, rep, number, time), output, VideoLimit - output.Length, ct).ConfigureAwait(false);
    }

    private static Uri ResolveTemplate(Uri baseUri, string template, XElement rep, long number, long time)
    {
        string expanded = Regex.Replace(template, @"\$(RepresentationID|Bandwidth|Number|Time)(%0(\d+)d)?\$", match =>
        {
            string name = match.Groups[1].Value;
            if (name == "RepresentationID") return (string?)rep.Attribute("id") ?? throw new InvalidDataException("DASH representation ID is missing.");
            long value = name == "Number" ? number : name == "Time" ? time : Number(rep, "bandwidth");
            int digits = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            if (digits > 12) throw new InvalidDataException("DASH number formatting is invalid.");
            return value.ToString(digits == 0 ? "0" : new string('0', digits), CultureInfo.InvariantCulture);
        }, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (expanded.Contains('$')) throw new InvalidDataException("Unsupported DASH template variable.");
        return ValidateSteamUri(new Uri(baseUri, expanded).AbsoluteUri);
    }

    private async Task<string> DownloadHlsAsync(Uri uri, string work, IProgress<string>? progress, CancellationToken ct)
    {
        (string text, Uri actual) = await GetManifestAsync(uri, ct).ConfigureAwait(false);
        string[] lines = HlsLines(text);
        var variants = new List<(Uri Uri, int Height, long Bandwidth, string Audio)>();
        var audioGroups = new Dictionary<string, Uri>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var attrs = HlsAttributes(lines[i]);
                if (Get(attrs, "TYPE") == "AUDIO" && Get(attrs, "URI") is string audioUri && audioUri.Length > 0)
                    audioGroups.TryAdd(Get(attrs, "GROUP-ID"), ValidateSteamUri(new Uri(actual, audioUri).AbsoluteUri));
            }
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;
            var values = HlsAttributes(lines[i]);
            string codecs = Get(values, "CODECS"), resolution = Get(values, "RESOLUTION");
            if (!codecs.Contains("avc", StringComparison.OrdinalIgnoreCase)) continue;
            string[] dimensions = resolution.Split('x');
            if (dimensions.Length != 2 || !int.TryParse(dimensions[1], out int height) || height <= 0 || height > 1080) continue;
            string? next = lines.Skip(i + 1).FirstOrDefault(x => x.Length > 0 && !x.StartsWith('#'));
            if (next == null) continue;
            long.TryParse(Get(values, "BANDWIDTH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bandwidth);
            variants.Add((ValidateSteamUri(new Uri(actual, next).AbsoluteUri), height, bandwidth, Get(values, "AUDIO")));
        }
        string videoFile = Path.Combine(work, "video.mp4"), audioFile = Path.Combine(work, "audio.mp4");
        if (variants.Count == 0) throw new InvalidDataException("HLS master has no H.264 video at 1080p or below.");
        var best = variants.OrderByDescending(x => x.Height).ThenByDescending(x => x.Bandwidth).First();
        progress?.Report("Downloading " + best.Height + "p HLS trailer video and audio…");
        await DownloadHlsTrackAsync(best.Uri, videoFile, ct).ConfigureAwait(false);
        string? audio = null;
        if (best.Audio.Length > 0)
        {
            if (!audioGroups.TryGetValue(best.Audio, out Uri? audioUri)) throw new InvalidDataException("HLS audio playlist is missing.");
            await DownloadHlsTrackAsync(audioUri, audioFile, ct).ConfigureAwait(false);
            audio = audioFile;
        }
        return await RemuxAsync(videoFile, audio, work, ct).ConfigureAwait(false);
    }

    private async Task DownloadHlsTrackAsync(Uri uri, string file, CancellationToken ct)
    {
        (string text, Uri actual) = await GetManifestAsync(uri, ct).ConfigureAwait(false);
        string[] lines = HlsLines(text);
        if (!lines.Contains("#EXT-X-ENDLIST", StringComparer.Ordinal)) throw new InvalidDataException("Only completed HLS trailers are supported.");
        if (lines.Any(x => x.StartsWith("#EXT-X-BYTERANGE", StringComparison.Ordinal) || x.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal)))
            throw new InvalidDataException("This HLS byte-range or discontinuity layout is not supported; try the DASH source.");
        var urls = new List<Uri>();
        foreach (string line in lines)
        {
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) && Get(HlsAttributes(line), "METHOD") != "NONE")
                throw new InvalidDataException("Encrypted HLS trailers are not supported.");
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var attrs = HlsAttributes(line);
                if (attrs.ContainsKey("BYTERANGE")) throw new InvalidDataException("HLS initialization byte ranges are not supported.");
                urls.Add(ValidateSteamUri(new Uri(actual, Get(attrs, "URI")).AbsoluteUri));
            }
            else if (line.Length > 0 && !line.StartsWith('#')) urls.Add(ValidateSteamUri(new Uri(actual, line).AbsoluteUri));
        }
        if (urls.Count is < 1 or > 10000) throw new InvalidDataException("HLS trailer segment count is invalid.");
        await using var output = NewFile(file);
        foreach (Uri segment in urls) await AppendUrlAsync(segment, output, VideoLimit - output.Length, ct).ConfigureAwait(false);
    }

    private static string[] HlsLines(string text)
    {
        string[] lines = text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (lines.Length == 0 || lines[0] != "#EXTM3U") throw new InvalidDataException("Steam response is not an HLS playlist.");
        return lines;
    }

    private static Dictionary<string, string> HlsAttributes(string line)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(line[(line.IndexOf(':') + 1)..], "([A-Z0-9-]+)=(?:\"([^\"]*)\"|([^,]*))", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            values[match.Groups[1].Value] = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
        return values;
    }

    private static string Get(Dictionary<string, string> values, string key) => values.TryGetValue(key, out string? value) ? value : "";
    private static string Value(XElement rep, string name) => (string?)rep.Attribute(name) ?? (string?)rep.Parent?.Attribute(name) ?? "";
    private static long Number(XElement rep, string name) => long.TryParse(Value(rep, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
    private static long AttributeLong(XElement element, string name, long fallback) => long.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : fallback;

    private async Task<string> RemuxAsync(string video, string? audio, string work, CancellationToken ct)
    {
        string output = Path.Combine(work, "trailer.mp4");
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-protocol_whitelist", "file", "-i", video };
        if (audio != null) args.AddRange(new[] { "-protocol_whitelist", "file", "-i", audio });
        args.AddRange(new[] { "-map", "0:v:0", "-map", audio == null ? "0:a:0" : "1:a:0", "-c", "copy", "-movflags", "+faststart", "-f", "mp4", output });
        await RunProcessAsync(ffmpeg, args, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        return output;
    }

    private async Task<string> ValidateVideoAsync(string path, bool requireH264, CancellationToken ct)
    {
        RequireVideoTools();
        byte[] magic = new byte[16];
        using (FileStream file = File.OpenRead(path)) file.ReadExactly(magic);
        bool mp4 = Encoding.ASCII.GetString(magic, 4, 4) == "ftyp";
        bool webm = magic[0] == 0x1a && magic[1] == 0x45 && magic[2] == 0xdf && magic[3] == 0xa3;
        if (!mp4 && !webm) throw new InvalidDataException("Steam response is not an MP4/WebM video (a playlist or HTML page is not a video).");
        string json = await RunProcessAsync(ffprobe, new[] { "-v", "error", "-protocol_whitelist", "file", "-show_entries", "stream=index,codec_type,codec_name,width,height:format=format_name,duration", "-of", "json", path }, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        using JsonDocument data = JsonDocument.Parse(json);
        JsonElement root = data.RootElement;
        if (!root.TryGetProperty("streams", out JsonElement streams)) throw new InvalidDataException("Trailer contains no media streams.");
        var videos = streams.EnumerateArray().Where(x => JsonString(x, "codec_type") == "video").ToList();
        var audios = streams.EnumerateArray().Where(x => JsonString(x, "codec_type") == "audio").ToList();
        if (videos.Count == 0 || audios.Count == 0 || videos.Any(x => JsonString(x, "codec_name").Length == 0) || audios.Any(x => JsonString(x, "codec_name").Length == 0))
            throw new InvalidDataException("Trailer must contain valid video and audio streams.");
        JsonElement first = videos[0];
        int width = first.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0;
        int height = first.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : 0;
        if (width <= 0 || height <= 0 || (requireH264 && (height > 1080 || JsonString(first, "codec_name") != "h264")))
            throw new InvalidDataException("Trailer video resolution or codec is invalid.");
        if (!root.TryGetProperty("format", out JsonElement format) || !double.TryParse(JsonString(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration) || !double.IsFinite(duration) || duration <= 0 || duration > 7200)
            throw new InvalidDataException("Trailer has no valid bounded playback duration.");
        string formatName = JsonString(format, "format_name");
        if ((mp4 && !formatName.Contains("mp4", StringComparison.Ordinal)) || (webm && !formatName.Contains("webm", StringComparison.Ordinal)))
            throw new InvalidDataException("Trailer container does not match its file signature.");
        // Decode a short sample as well as probing metadata, so a header-only response is rejected.
        await RunProcessAsync(ffmpeg, new[] { "-nostdin", "-hide_banner", "-v", "error", "-xerror", "-protocol_whitelist", "file", "-i", path, "-t", "2", "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-" }, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        return mp4 ? ".mp4" : ".webm";
    }

    private static string JsonString(JsonElement element, string key) => element.TryGetProperty(key, out JsonElement value) ? value.ToString() : "";

    private static async Task<string> RunProcessAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (string argument in args) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Could not start " + Path.GetFileName(exe) + ".");
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput, 128 * 1024);
        Task<string> stderr = ReadBoundedAsync(process.StandardError, 32 * 1024);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            throw new IOException(Path.GetFileName(exe) + " timed out.");
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidDataException(Path.GetFileName(exe) + " could not verify/remux the trailer: " + (await stderr.ConfigureAwait(false)).Trim());
        return await stdout.ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int max)
    {
        var result = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            if (result.Length < max) result.Append(buffer, 0, Math.Min(count, max - result.Length));
        return result.ToString();
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
