using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SteamMetadataImporter;

/// <summary>Reads public Steam metadata. Media sources are candidates, not downloaded files.</summary>
public sealed class SteamClient : IDisposable
{
    private const string AssetCdn = "https://shared.akamai.steamstatic.com/store_item_assets/";
    private const string VideoCdn = "https://video.akamai.steamstatic.com/store_trailers/";
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public SteamClient()
    {
        client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LaunchBox-ABetterSteamMetadataImporter/1.1");
        ownsClient = true;
    }

    /// <summary>Inject a caller-owned client, for example for deterministic HTTP tests.</summary>
    public SteamClient(HttpClient httpClient) => client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<SteamDetails> GetDetailsAsync(int appId, CancellationToken cancellationToken)
    {
        if (appId <= 0) throw new ArgumentOutOfRangeException(nameof(appId));
        string json = await GetJsonAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=us", cancellationToken).ConfigureAwait(false);
        SteamDetails result = ParseAppDetails(appId, json);
        try
        {
            string input = JsonSerializer.Serialize(new
            {
                ids = new[] { new { appid = appId } },
                context = new { language = "english", country_code = "US" },
                data_request = new { include_assets = true, include_basic_info = true, include_screenshots = true, include_trailers = true, include_release = true }
            });
            string browseJson = await GetJsonAsync("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input), cancellationToken).ConfigureAwait(false);
            EnrichFromStoreBrowse(result, browseJson);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or InvalidDataException)
        {
            result.Warnings.Add("Steam's library artwork service was unavailable; using store metadata, local Steam artwork, and legacy CDN candidates. " + ex.Message);
        }
        cancellationToken.ThrowIfCancellationRequested();
        AddLocalCacheCandidates(result);
        AddLegacyCandidates(result);
        AddMissingWarnings(result);
        return result;
    }

    /// <summary>Accepts an appdetails response envelope or its data object. Performs no file or network I/O.</summary>
    public static SteamDetails ParseAppDetails(int appId, string json)
    {
        if (appId <= 0) throw new ArgumentOutOfRangeException(nameof(appId));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement data = document.RootElement;
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Steam returned an invalid appdetails response.");
        if (data.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out JsonElement envelope))
        {
            if (!Bool(envelope, "success") || !envelope.TryGetProperty("data", out data))
                throw new InvalidDataException($"Steam did not return public metadata for app {appId}. The app may be unavailable in this region or no longer listed.");
        }
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Steam returned an invalid app metadata object.");
        int? reportedId = Number(data, "steam_appid");
        if (reportedId.HasValue && reportedId.Value != appId) throw new InvalidDataException("Steam returned metadata for a different app ID.");
        string name = String(data, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException($"Steam returned no game title for app {appId}.");
        string description = String(data, "about_the_game");
        if (string.IsNullOrWhiteSpace(description)) description = String(data, "detailed_description");
        if (string.IsNullOrWhiteSpace(description)) description = String(data, "short_description");
        var details = new SteamDetails
        {
            AppId = appId, Name = name, Description = StripHtml(description),
            Developer = JoinNames(data, "developers"), Publisher = JoinNames(data, "publishers"),
            Genres = JoinNames(data, "genres", "description")
        };
        if (data.TryGetProperty("release_date", out JsonElement release))
        {
            details.ComingSoon = Bool(release, "coming_soon");
            details.ReleaseDate = ParseExactDate(String(release, "date"));
        }
        var categories = Array(data, "categories").Select(c => Number(c, "id")).ToHashSet();
        var modes = new List<string>();
        if (categories.Contains(2)) modes.Add("Single Player");
        if (categories.Contains(1)) modes.Add("Multiplayer");
        if (categories.Overlaps(new int?[] { 9, 38, 39 })) modes.Add("Cooperative");
        details.PlayMode = string.Join("; ", modes);
        details.MaxPlayers = ExplicitPlayerCount(details.Description);

        AddAsset(details, "header", "Steam header", "Banner", new[] { String(data, "header_image") });
        AddAsset(details, "store-small-capsule", "Steam small store capsule", "Steam Banner", new[] { String(data, "capsule_image"), String(data, "capsule_imagev5") });
        AddAsset(details, "page-background", "Steam store background", "Fanart - Background", new[] { String(data, "background_raw"), String(data, "background") });
        int screenshotIndex = 0;
        foreach (JsonElement shot in Array(data, "screenshots"))
        {
            int index = Number(shot, "id") ?? screenshotIndex;
            AddAsset(details, $"screenshot-{index}", $"Screenshot {index + 1}", "Screenshot - Gameplay", new[] { String(shot, "path_full"), String(shot, "path_thumbnail") });
            screenshotIndex++;
        }
        int movieIndex = 0;
        foreach (JsonElement movie in Array(data, "movies"))
        {
            movieIndex++;
            string id = Number(movie, "id")?.ToString(CultureInfo.InvariantCulture) ?? "store-" + movieIndex;
            string label = String(movie, "name");
            if (string.IsNullOrWhiteSpace(label)) label = "Trailer " + movieIndex;
            List<string> sources = MovieSources(movie);
            if (sources.Count == 0) details.Warnings.Add($"Steam lists '{label}' without a supported full trailer source.");
            else AddAsset(details, "trailer-" + id, label, null, sources, isVideo: true, highlight: Bool(movie, "highlight"));
        }
        return details;
    }

    private async Task<string> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if ((status == 429 || status >= 500) && attempt < 2)
                {
                    TimeSpan delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt + 1);
                    if (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate) delay = retryDate - DateTimeOffset.UtcNow;
                    delay = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0.5, 8));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Steam metadata request timed out.", ex);
            }
        }
    }

    private static void EnrichFromStoreBrowse(SteamDetails details, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("response", out JsonElement response)) throw new InvalidDataException("Steam library response was empty.");
        JsonElement item = Array(response, "store_items").FirstOrDefault(i => (Number(i, "appid") ?? Number(i, "id")) == details.AppId);
        if (item.ValueKind != JsonValueKind.Object || Number(item, "success") != 1) throw new InvalidDataException("Steam library service did not return this app.");
        if (item.TryGetProperty("basic_info", out JsonElement basic))
        {
            details.Series = JoinNames(basic, "franchises", "name");
            if (string.IsNullOrWhiteSpace(details.Developer)) details.Developer = JoinNames(basic, "developers", "name");
            if (string.IsNullOrWhiteSpace(details.Publisher)) details.Publisher = JoinNames(basic, "publishers", "name");
            if (string.IsNullOrWhiteSpace(details.Description)) details.Description = StripHtml(String(basic, "short_description"));
        }
        if (!details.ReleaseDate.HasValue && !details.ComingSoon && item.TryGetProperty("release", out JsonElement release) && release.ValueKind == JsonValueKind.Object)
        {
            // Only a confirmed historical timestamp can fill an absent/uncertain store date.
            if (release.TryGetProperty("steam_release_date", out JsonElement timestamp) && timestamp.TryGetInt64(out long seconds)
                && seconds > 0 && seconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                details.ReleaseDate = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.Date;
        }
        if (item.TryGetProperty("assets", out JsonElement assets))
        {
            string format = String(assets, "asset_url_format");
            if (string.IsNullOrWhiteSpace(format)) format = $"steam/apps/{details.AppId}/${{FILENAME}}";
            string[] AssetUrls(params string[] fields) => fields.Select(f => AssetUrl(format, String(assets, f))).Where(s => s.Length > 0).ToArray();
            string[] portrait = AssetUrls("library_capsule_2x", "library_capsule");
            AddAsset(details, "portrait-front", "Steam library cover", "Box - Front", portrait, prepend: true);
            AddAsset(details, "portrait-steam", "Steam library poster", "Steam Poster", portrait, prepend: true);
            AddAsset(details, "library-hero", "Steam library background", "Fanart - Background", AssetUrls("library_hero_2x", "library_hero"), prepend: true);
            AddAsset(details, "clear-logo", "Steam clear logo", "Clear Logo", AssetUrls("library_logo_2x", "library_logo", "logo"), prepend: true);
            AddAsset(details, "header", "Steam header", "Banner", AssetUrls("header_2x", "header"), prepend: true);
            AddAsset(details, "store-main-capsule", "Steam main store capsule", "Steam Banner", AssetUrls("main_capsule_2x", "main_capsule"), prepend: true);
            AddAsset(details, "store-small-capsule", "Steam small store capsule", "Steam Banner", AssetUrls("small_capsule_2x", "small_capsule"), prepend: true);
            AddAsset(details, "store-hero-capsule", "Steam store hero capsule", "Steam Poster", AssetUrls("hero_capsule_2x", "hero_capsule"), prepend: true);
            AddAsset(details, "page-background", "Steam store background", "Fanart - Background", AssetUrls("raw_page_background", "page_background"), prepend: true);
            string icon = String(assets, "community_icon");
            if (Regex.IsMatch(icon, "^[a-fA-F0-9]{40}$"))
                AddAsset(details, "community-icon", "Steam app icon", "Steam Banner", new[] { $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{details.AppId}/{icon}.jpg" });
        }
        if (item.TryGetProperty("screenshots", out JsonElement screenshots) && screenshots.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty group in screenshots.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
            foreach (JsonElement shot in group.Value.EnumerateArray())
            {
                int? ordinal = Number(shot, "ordinal");
                if (!ordinal.HasValue) continue;
                int index = Math.Max(0, ordinal.Value - 1);
                AddAsset(details, $"screenshot-{index}", $"Screenshot {index + 1}", "Screenshot - Gameplay", new[] { AssetUrl("${FILENAME}", String(shot, "filename")) }, prepend: true);
            }
        }
        if (item.TryGetProperty("trailers", out JsonElement trailers))
        {
            foreach (string group in new[] { "highlights", "other_trailers" })
            foreach (JsonElement trailer in Array(trailers, group))
            {
                string label = String(trailer, "trailer_name");
                string image = String(trailer, "screenshot_full");
                if (string.IsNullOrWhiteSpace(image)) image = String(trailer, "screenshot_medium");
                string imageId = image.Split('/')[0];
                string id = int.TryParse(imageId, out _) ? imageId : "browse-" + (Number(trailer, "trailer_base_id")?.ToString(CultureInfo.InvariantCulture) ?? label);
                SteamAsset? existing = details.Assets.FirstOrDefault(a => a.IsVideo && (a.Key == "trailer-" + id || a.Label.Equals(label, StringComparison.OrdinalIgnoreCase)));
                var sources = new List<string>();
                foreach (string encoding in new[] { "dash_h264", "hls_h264", "dash_av1" })
                foreach (JsonElement adaptive in Array(trailer, "adaptive_trailers").Where(a => String(a, "encoding") == encoding))
                {
                    string path = String(adaptive, "cdn_path");
                    if (!string.IsNullOrWhiteSpace(path)) sources.Add(path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? path : VideoCdn + path.TrimStart('/'));
                }
                // Full legacy streams are retained if this endpoint still returns them. Microtrailers are previews, not full trailers.
                sources.AddRange(MovieSources(trailer));
                AddAsset(details, existing?.Key ?? "trailer-" + id, string.IsNullOrWhiteSpace(label) ? "Trailer " + id : label,
                    null, sources, isVideo: true, highlight: group == "highlights");
            }
        }
    }

    private static List<string> MovieSources(JsonElement movie)
    {
        var values = new List<string>
        {
            NestedString(movie, "mp4", "max"), String(movie, "dash_h264"), String(movie, "hls_h264"),
            NestedString(movie, "mp4", "480"), NestedString(movie, "webm", "max"), NestedString(movie, "webm", "480"), String(movie, "dash_av1")
        };
        return values.Select(SafeHttps).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddLocalCacheCandidates(SteamDetails details)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string path && !string.IsNullOrWhiteSpace(path)) roots.Add(path);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (programFiles.Length > 0) roots.Add(Path.Combine(programFiles, "Steam"));
        }
        foreach (string root in roots)
        {
            string cache = Path.Combine(root, "appcache", "librarycache");
            try
            {
                if (!Directory.Exists(cache)) continue;
                string appFolder = Path.Combine(cache, details.AppId.ToString(CultureInfo.InvariantCulture));
                IEnumerable<string> nested = Directory.Exists(appFolder) ? Directory.EnumerateFiles(appFolder, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>();
                IEnumerable<string> flat = Directory.EnumerateFiles(cache, details.AppId.ToString(CultureInfo.InvariantCulture) + "_*", SearchOption.TopDirectoryOnly);
                foreach (string path in nested.Concat(flat).OrderByDescending(File.GetLastWriteTimeUtc).Take(128))
                {
                    string filename = Path.GetFileName(path);
                    string name = filename.ToLowerInvariant();
                    string prefix = details.AppId.ToString(CultureInfo.InvariantCulture) + "_";
                    if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name.Substring(prefix.Length);
                    string key = "", label = "", type = "";
                    if (name is "library_capsule.jpg" or "library_capsule_2x.jpg" or "library_600x900.jpg" or "library_600x900_2x.jpg")
                    { key = "portrait-front"; label = "Steam library cover"; type = "Box - Front"; }
                    else if (name is "library_hero.jpg" or "library_hero_2x.jpg")
                    { key = "library-hero"; label = "Steam library background"; type = "Fanart - Background"; }
                    else if (name is "logo.png" or "logo_2x.png" or "library_logo.png")
                    { key = "clear-logo"; label = "Steam clear logo"; type = "Clear Logo"; }
                    else if (name is "library_header.jpg" or "header.jpg" or "header_2x.jpg")
                    { key = "header"; label = "Steam header"; type = "Banner"; }
                    else if (Regex.IsMatch(name, "^[a-f0-9]{40}\\.jpg$"))
                    { key = "community-icon"; label = "Steam app icon"; type = "Steam Banner"; }
                    if (key.Length == 0) continue;
                    var sources = new List<string>();
                    string hashFolder = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
                    if (Regex.IsMatch(hashFolder, "^[a-fA-F0-9]{40}$"))
                    {
                        // Modern cache folders preserve Steam's artwork hash. The client renames header.jpg to library_header.jpg.
                        string remoteName = name == "library_header.jpg" ? "header.jpg" : filename;
                        string remote = AssetCdn + $"steam/apps/{details.AppId}/{hashFolder}/";
                        if (name == "library_capsule.jpg") sources.Add(remote + "library_capsule_2x.jpg");
                        if (name == "library_hero.jpg") sources.Add(remote + "library_hero_2x.jpg");
                        sources.Add(remote + remoteName);
                    }
                    sources.Add(Path.GetFullPath(path));
                    AddAsset(details, key, label, type, sources);
                    if (key == "portrait-front") AddAsset(details, "portrait-steam", "Steam library poster", "Steam Poster", sources);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { details.Warnings.Add("Some local Steam library artwork could not be read: " + ex.Message); }
        }
    }

    private static void AddLegacyCandidates(SteamDetails details)
    {
        string cdn = $"https://cdn.akamai.steamstatic.com/steam/apps/{details.AppId}/";
        string[] portrait = { cdn + "library_600x900_2x.jpg", cdn + "library_600x900.jpg" };
        AddAsset(details, "portrait-front", "Steam library cover", "Box - Front", portrait);
        AddAsset(details, "portrait-steam", "Steam library poster", "Steam Poster", portrait);
        AddAsset(details, "library-hero", "Steam library background", "Fanart - Background", new[] { cdn + "library_hero_2x.jpg", cdn + "library_hero.jpg" });
        AddAsset(details, "clear-logo", "Steam clear logo", "Clear Logo", new[] { cdn + "logo.png" });
        AddAsset(details, "header", "Steam header", "Banner", new[] { cdn + "header.jpg" });
        details.Warnings.Add("Artwork sources include fallbacks; Steam may not provide every library image. Unavailable sources are skipped during download.");
    }

    private static void AddMissingWarnings(SteamDetails details)
    {
        if (!details.ReleaseDate.HasValue) details.Warnings.Add("Steam does not provide an exact release date; the existing release date will be preserved.");
        if (!details.MaxPlayers.HasValue) details.Warnings.Add("Steam does not explicitly state a player count; maximum players will be preserved.");
        if (string.IsNullOrWhiteSpace(details.Description)) details.Warnings.Add("Steam did not provide a description.");
        if (string.IsNullOrWhiteSpace(details.Developer)) details.Warnings.Add("Steam did not provide a developer.");
        if (string.IsNullOrWhiteSpace(details.Publisher)) details.Warnings.Add("Steam did not provide a publisher.");
        if (!details.Assets.Any(a => a.IsVideo)) details.Warnings.Add("Steam did not list any supported full trailers.");
    }

    private static void AddAsset(SteamDetails details, string key, string label, string? imageType, IEnumerable<string> sources, bool isVideo = false, bool highlight = false, bool prepend = false)
    {
        List<string> safe = sources.Select(s => !string.IsNullOrWhiteSpace(s) && Path.IsPathFullyQualified(s) && Uri.TryCreate(s, UriKind.Absolute, out Uri? local) && local.IsFile ? s : SafeHttps(s))
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (safe.Count == 0) return;
        SteamAsset? asset = details.Assets.FirstOrDefault(a => a.Key == key);
        if (asset == null)
        {
            details.Assets.Add(new SteamAsset { Key = key, Label = label, ImageType = imageType, IsVideo = isVideo, Highlight = highlight, Sources = safe });
            return;
        }
        asset.Highlight |= highlight;
        asset.Sources = (prepend ? safe.Concat(asset.Sources) : asset.Sources.Concat(safe)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (asset.IsVideo) asset.Sources = asset.Sources.OrderBy(VideoPriority).ToList();
    }

    private static int VideoPriority(string source)
    {
        string path = new Uri(source).AbsolutePath.ToLowerInvariant();
        if (path.Contains("dash_h264", StringComparison.Ordinal)) return 1;
        if (path.Contains("hls_264", StringComparison.Ordinal) || path.Contains("hls_h264", StringComparison.Ordinal)) return 2;
        if (path.EndsWith(".mp4", StringComparison.Ordinal)) return path.Contains("480", StringComparison.Ordinal) ? 3 : 0;
        if (path.EndsWith(".webm", StringComparison.Ordinal)) return 4;
        return 5;
    }

    private static string AssetUrl(string format, string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return "";
        if (filename.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return SafeHttps(filename);
        string path = format.Replace("${FILENAME}", filename, StringComparison.Ordinal);
        return SafeHttps(path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? path : AssetCdn + path.TrimStart('/'));
    }

    private static string SafeHttps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return "";
        bool official = uri.Host.Equals("steamstatic.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("steampowered.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".steampowered.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase);
        if (!official || (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length > 0) return "";
        return uri.Scheme == "https" ? uri.AbsoluteUri : new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri;
    }

    private static DateTime? ParseExactDate(string value)
    {
        string[] formats = { "MMM d, yyyy", "MMMM d, yyyy", "d MMM, yyyy", "d MMMM, yyyy", "d MMM yyyy", "d MMMM yyyy", "MMM d yyyy", "MMMM d yyyy", "yyyy-MM-dd" };
        return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime date) ? date.Date : null;
    }

    private static int? ExplicitPlayerCount(string text)
    {
        var counts = new List<int>();
        foreach (Match match in Regex.Matches(text, @"\b(?<n>\d{1,3}|two|three|four|five|six|seven|eight)\s*[-–]?\s*players?\b", RegexOptions.IgnoreCase))
        {
            string value = match.Groups["n"].Value.ToLowerInvariant();
            int number = value switch { "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7, "eight" => 8, _ => int.TryParse(value, out int parsed) ? parsed : 0 };
            if (number > 0 && number <= 256) counts.Add(number);
        }
        return counts.Count > 0 ? counts.Max() : null;
    }

    private static string StripHtml(string value)
    {
        string text = Regex.Replace(value, @"<(script|style)\b[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<li\b[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(br\s*/?|/p|/div|/h[1-6]|/li)\s*>", "\n", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", ""));
        text = Regex.Replace(text, @"[^\S\r\n]+", " ");
        text = Regex.Replace(text, @"\r\n?|\n", "\n");
        text = Regex.Replace(text, @" *\n *", "\n");
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }

    private static string JoinNames(JsonElement value, string property, string? child = null) => string.Join("; ", Array(value, property)
        .Select(item => child == null && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : String(item, child ?? "name"))
        .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));
    private static IEnumerable<JsonElement> Array(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static string String(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
    private static string NestedString(JsonElement value, string parent, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(parent, out JsonElement nested) ? String(nested, property) : "";
    private static int? Number(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out int result) ? result : null;
    private static bool Bool(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement boolean) && boolean.ValueKind == JsonValueKind.True;
    public void Dispose() { if (ownsClient) client.Dispose(); }
}
