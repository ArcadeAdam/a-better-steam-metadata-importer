using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamMetadataImporter;

internal static partial class Program
{
    private const string BasicApp = """
        {"4685520":{"success":true,"data":{"steam_appid":4685520,"name":"Railbreak DX","about_the_game":"<p>Blast <b>zombies</b> &amp; survive.</p>","developers":["Dead Drop Studios LLC"],"publishers":["Dead Drop Studios LLC"],"genres":[{"id":"1","description":"Action"}],"categories":[{"id":2,"description":"Single-player"}],"release_date":{"coming_soon":true,"date":"To be announced"},"header_image":"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4685520/abc/header.jpg","screenshots":[{"id":0,"path_full":"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4685520/abc/ss_one.jpg"}]}}}
        """;

    private static void RegisterClientTests()
    {
        Test("An unreleased game without movies parses with screenshots", MissingMovies);
        Test("Legacy full trailer streams remain available", LegacyMovie);
        Test("Modern adaptive trailers exclude microtrailers", AdaptiveMovie);
        Test("Unlisted, malformed, and mismatched app responses are rejected", BadMetadata);
        Test("Hashed artwork and modern trailers are read from StoreBrowse", BrowseEnrichment);
        Test("Required Steam HTTP errors are surfaced", RequiredHttpError);
        Test("Optional artwork service errors retain metadata with a warning", OptionalHttpError);
    }

    private static void MissingMovies()
    {
        var details = SteamClient.ParseAppDetails(4685520, BasicApp);
        Equal("Railbreak DX", details.Name, "name");
        Check(details.ComingSoon && details.ReleaseDate == null, "TBA parsed as a fabricated date");
        Check(details.Description.Contains("Blast zombies & survive.", StringComparison.Ordinal), "HTML not converted to plain text");
        Check(!details.Assets.Any(a => a.IsVideo), "missing movies fabricated a trailer");
        Check(details.Assets.Any(a => a.ImageType == "Screenshot - Gameplay"), "screenshots lost without movies");
    }

    private static void LegacyMovie()
    {
        const string json = """
          {"name":"Legacy game","steam_appid":100,"release_date":{"coming_soon":false,"date":"1 Jan, 2020"},"movies":[{"id":256001,"name":"Trailer","highlight":true,"mp4":{"max":"https://video.akamai.steamstatic.com/steam/apps/256001/movie_max.mp4","480":"https://video.akamai.steamstatic.com/steam/apps/256001/movie480.mp4"},"webm":{"max":"https://video.akamai.steamstatic.com/steam/apps/256001/movie_max.webm"}}]}
          """;
        var details = SteamClient.ParseAppDetails(100, json);
        var trailer = details.Assets.Single(a => a.IsVideo);
        Check(trailer.Highlight, "highlight flag lost");
        Equal("https://video.akamai.steamstatic.com/steam/apps/256001/movie_max.mp4", trailer.Sources.First(), "full-resolution MP4 preferred");
        Check(trailer.Sources.Any(s => s.EndsWith(".webm", StringComparison.Ordinal)), "legacy WebM fallback missing");
        Equal<DateTime?>(new DateTime(2020, 1, 1), details.ReleaseDate, "known date");
    }

    private static void AdaptiveMovie()
    {
        const string json = """
          {"name":"Modern game","steam_appid":101,"movies":[{"id":257001,"name":"Full trailer","dash_h264":"https://video.akamai.steamstatic.com/store_trailers/101/hash/dash_h264.mpd","hls_h264":"https://video.akamai.steamstatic.com/store_trailers/101/hash/hls_h264.m3u8","microtrailer":{"mp4":"https://video.akamai.steamstatic.com/101/micro.mp4"}}]}
          """;
        var trailer = SteamClient.ParseAppDetails(101, json).Assets.Single(a => a.IsVideo);
        Check(trailer.Sources.Any(s => s.EndsWith(".mpd", StringComparison.Ordinal)), "DASH full trailer missing");
        Check(trailer.Sources.Any(s => s.EndsWith(".m3u8", StringComparison.Ordinal)), "HLS full trailer fallback missing");
        Check(!trailer.Sources.Any(s => s.Contains("micro", StringComparison.OrdinalIgnoreCase)), "microtrailer substituted for full trailer");
    }

    private static void BadMetadata()
    {
        Throws<ArgumentOutOfRangeException>(() => SteamClient.ParseAppDetails(0, BasicApp));
        Throws<JsonException>(() => SteamClient.ParseAppDetails(100, "{"));
        Throws<InvalidDataException>(() => SteamClient.ParseAppDetails(100, "{\"100\":{\"success\":false}}"));
        Throws<InvalidDataException>(() => SteamClient.ParseAppDetails(100, "{\"name\":\"Wrong game\",\"steam_appid\":101}"));
        Throws<InvalidDataException>(() => SteamClient.ParseAppDetails(100, "{}"));
    }

    private static async Task BrowseEnrichment()
    {
        const string browse = """
          {"response":{"store_items":[{"appid":4685520,"success":1,"basic_info":{"franchises":[{"name":"Railbreak"}]},"assets":{"asset_url_format":"steam/apps/4685520/${FILENAME}","library_capsule_2x":"a1b2c3/library_capsule_2x.jpg","library_capsule":"a1b2c3/library_capsule.jpg","library_hero":"a1b2c3/library_hero.jpg","library_logo":"a1b2c3/logo.png"},"trailers":{"highlights":[{"trailer_name":"Full launch trailer","trailer_base_id":99,"screenshot_full":"257001/screenshot.jpg","adaptive_trailers":[{"encoding":"dash_h264","cdn_path":"4685520/hash/dash_h264.mpd"}]}]}}]}}
          """;
        using var http = new HttpClient(new StubHandler(request => JsonResponse(request.RequestUri!.Host == "api.steampowered.com" ? browse : BasicApp)));
        using var client = new SteamClient(http);
        var details = await client.GetDetailsAsync(4685520, CancellationToken.None);
        Equal("Railbreak", details.Series, "franchise");
        var portrait = details.Assets.Single(a => a.Key == "portrait-front");
        Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4685520/a1b2c3/library_capsule_2x.jpg", portrait.Sources.First(), "current hashed high-resolution portrait first");
        Check(details.Assets.Any(a => a.IsVideo && a.Sources.Contains("https://video.akamai.steamstatic.com/store_trailers/4685520/hash/dash_h264.mpd")), "StoreBrowse adaptive trailer missing");
    }

    private static async Task RequiredHttpError()
    {
        int count = 0;
        using var http = new HttpClient(new StubHandler(_ => { count++; return new HttpResponseMessage(HttpStatusCode.NotFound); }));
        using var client = new SteamClient(http);
        await ThrowsAsync<HttpRequestException>(() => client.GetDetailsAsync(4685520, CancellationToken.None));
        Equal(1, count, "permanent 404 should not retry");
    }

    private static async Task OptionalHttpError()
    {
        using var http = new HttpClient(new StubHandler(request => request.RequestUri!.Host == "api.steampowered.com" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : JsonResponse(BasicApp)));
        using var client = new SteamClient(http);
        var details = await client.GetDetailsAsync(4685520, CancellationToken.None);
        Equal("Railbreak DX", details.Name, "required metadata retained");
        Check(details.Warnings.Any(w => w.Contains("unavailable", StringComparison.OrdinalIgnoreCase)), "optional failure warning missing");
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> response;
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => this.response = response;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(response(request));
    }
}
