using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SteamMetadataImporter;
using Unbroken.LaunchBox.Plugins.Data;

internal static partial class Program
{
    private static void RegisterImportEngineLimitTests()
    {
        Test("Engine fills one remaining screenshot slot and repeat import does not grow a stale SDK cache", EngineExistingScreenshotCap);
        Test("Engine failed assets leave capacity for later successful downloads", EngineFailedAssetCapacity);
        Test("Engine zero disables a category and unlimited imports all successful candidates", EngineZeroAndUnlimited);
        Test("Engine enforces different media type caps independently", EngineIndependentTypeCaps);
        Test("Engine retains edited manifest history when replacing an asset across repeated capped imports", EngineEditedManifestHistory);
    }

    private static Task EngineExistingScreenshotCap() => RunEngineOnSta(async dispatcher =>
    {
        using var fixture = new EngineLimitFixture(dispatcher);
        byte[] original = EnginePng(10);
        string existing = fixture.AddExistingImage("Screenshot - Gameplay", original, exposeInSdk: true);
        fixture.Responses["new.png"] = EnginePng(20);
        fixture.Responses["third.png"] = EnginePng(30);
        fixture.Responses["fourth.png"] = EnginePng(40);
        var details = fixture.SteamDetails(
            fixture.Asset("screenshot-0", "Screenshot - Gameplay", "missing.png", "new.png"),
            fixture.Asset("screenshot-1", "Screenshot - Gameplay", "third.png"),
            fixture.Asset("screenshot-2", "Screenshot - Gameplay", "fourth.png"));
        string first = await fixture.Import(details);
        Equal(2, fixture.ImageFiles("Screenshot - Gameplay").Length, "existing plus imported screenshot cap");
        Check(fixture.Requests.SequenceEqual(new[] { "missing.png", "new.png" }), "fallback or cap made unexpected HTTP requests");
        Check(first.Contains("1 files added", StringComparison.Ordinal), "first import did not add exactly one file");
        string repeat = await fixture.Import(details);
        Equal(2, fixture.ImageFiles("Screenshot - Gameplay").Length, "repeat import added a third screenshot");
        Equal(2, fixture.Requests.Count, "repeat downloaded despite full SDK/manifest union");
        Check(repeat.Contains("1 reused", StringComparison.Ordinal), "repeat did not reuse the validated manifest record");
        Check(original.SequenceEqual(File.ReadAllBytes(existing)), "pre-existing image bytes changed");
        Equal(2, fixture.PersistenceCalls, "persistence hook called once per import");
        fixture.AssertNoTemporaryFiles();
    });

    private static Task EngineFailedAssetCapacity() => RunEngineOnSta(async dispatcher =>
    {
        using var fixture = new EngineLimitFixture(dispatcher);
        fixture.Responses["one.png"] = EnginePng(51);
        fixture.Responses["two.png"] = EnginePng(52);
        fixture.Responses["three.png"] = EnginePng(53);
        var details = fixture.SteamDetails(
            fixture.Asset("screenshot-0", "Screenshot - Gameplay", "unavailable.png"),
            fixture.Asset("screenshot-1", "Screenshot - Gameplay", "one.png"),
            fixture.Asset("screenshot-2", "Screenshot - Gameplay", "two.png"),
            fixture.Asset("screenshot-3", "Screenshot - Gameplay", "three.png"));
        string summary = await fixture.Import(details);
        Equal(2, fixture.ImageFiles("Screenshot - Gameplay").Length, "a failed first asset consumed a quota slot");
        Check(fixture.Requests.SequenceEqual(new[] { "unavailable.png", "one.png", "two.png" }), "engine truncated candidates before successful downloads filled the cap");
        Check(summary.Contains("2 files added", StringComparison.Ordinal) && summary.Contains("1 unavailable", StringComparison.Ordinal), "download outcomes missing from import summary");
        await fixture.Import(details);
        Equal(3, fixture.Requests.Count, "repeat retried unavailable media after the cap was already full");
        Equal(2, fixture.ImageFiles("Screenshot - Gameplay").Length, "failed-first repeat exceeded cap");
        fixture.AssertNoTemporaryFiles();
    });

    private static Task EngineZeroAndUnlimited() => RunEngineOnSta(async dispatcher =>
    {
        using var fixture = new EngineLimitFixture(dispatcher);
        var assets = new List<SteamAsset>();
        for (int i = 0; i < 4; i++)
        {
            fixture.Responses[$"shot-{i}.png"] = EnginePng((byte)(60 + i));
            fixture.Responses[$"banner-{i}.png"] = EnginePng((byte)(80 + i));
            assets.Add(fixture.Asset($"screenshot-{i}", "Screenshot - Gameplay", $"shot-{i}.png"));
            assets.Add(fixture.Asset($"banner-{i}", "Banner", $"banner-{i}.png"));
        }
        var options = EngineOptions();
        options.MediaTypeLimits["Screenshot - Gameplay"] = 0;
        options.MediaTypeLimits["Banner"] = null;
        var details = fixture.SteamDetails(assets.ToArray());
        await fixture.Import(details, options);
        Equal(0, fixture.ImageFiles("Screenshot - Gameplay").Length, "zero cap imported a screenshot");
        Equal(4, fixture.ImageFiles("Banner").Length, "unlimited cap stopped at the default two");
        Equal(4, fixture.Requests.Count, "zero-cap assets contacted HTTP");
        Check(fixture.Requests.All(p => p.StartsWith("banner-", StringComparison.Ordinal)), "disabled category was downloaded");
        await fixture.Import(details, options);
        Equal(4, fixture.Requests.Count, "unlimited repeat ignored valid existing manifest entries");
        Equal(4, fixture.ImageFiles("Banner").Length, "unlimited repeat duplicated files");
        fixture.AssertNoTemporaryFiles();
    });

    private static Task EngineIndependentTypeCaps() => RunEngineOnSta(async dispatcher =>
    {
        using var fixture = new EngineLimitFixture(dispatcher);
        var assets = new List<SteamAsset>();
        for (int i = 0; i < 3; i++)
        {
            fixture.Responses[$"image-{i}.png"] = EnginePng((byte)(100 + i));
            assets.Add(fixture.Asset($"screenshot-{i}", "Screenshot - Gameplay", $"image-{i}.png"));
            assets.Add(fixture.Asset($"banner-{i}", "Banner", $"image-{i}.png"));
        }
        var options = EngineOptions();
        options.MediaTypeLimits["Screenshot - Gameplay"] = 1;
        options.MediaTypeLimits["Banner"] = 2;
        await fixture.Import(fixture.SteamDetails(assets.ToArray()), options);
        Equal(1, fixture.ImageFiles("Screenshot - Gameplay").Length, "screenshot cap was not independent");
        Equal(2, fixture.ImageFiles("Banner").Length, "banner cap inherited screenshot count");
        Equal(3, fixture.Requests.Count, "category quotas count alternatives or bytes across types");
        // Identical bytes in two different LaunchBox categories must occupy a slot in each.
        string screenshot = fixture.ImageFiles("Screenshot - Gameplay").Single();
        Check(fixture.ImageFiles("Banner").Any(b => File.ReadAllBytes(b).SequenceEqual(File.ReadAllBytes(screenshot))), "same artwork was incorrectly deduplicated across categories");
        fixture.AssertNoTemporaryFiles();
    });

    private static Task EngineEditedManifestHistory() => RunEngineOnSta(async dispatcher =>
    {
        using var fixture = new EngineLimitFixture(dispatcher);
        byte[] supplied = EnginePng(121);
        byte[] edited = EnginePng(122);
        fixture.Responses["original.png"] = supplied;
        fixture.Responses["next.png"] = EnginePng(123);
        var original = fixture.Asset("screenshot-0", "Screenshot - Gameplay", "original.png");
        await fixture.Import(fixture.SteamDetails(original));
        string oldPath = fixture.ImageFiles("Screenshot - Gameplay").Single();
        File.WriteAllBytes(oldPath, edited);
        // The SDK intentionally never reports either imported path, reproducing a stale cache.
        var laterDetails = fixture.SteamDetails(original, fixture.Asset("screenshot-1", "Screenshot - Gameplay", "next.png"));
        await fixture.Import(laterDetails);
        Equal(2, fixture.ImageFiles("Screenshot - Gameplay").Length, "replacement discarded history and admitted a third file");
        Equal(2, fixture.Requests.Count, "later asset downloaded after edited plus replacement files filled cap");
        Check(edited.SequenceEqual(File.ReadAllBytes(oldPath)), "edited old file was overwritten");
        var manifest = fixture.ReadManifest();
        var historical = manifest.Assets.Where(a => a.Key == "screenshot-0").ToArray();
        Equal(2, historical.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "same-key different-path history was lost");
        Check(historical.Any(a => string.Equals(Path.GetFullPath(a.Path, fixture.Root), oldPath, StringComparison.OrdinalIgnoreCase)), "edited old path no longer counts in inventory");
        string repeat = await fixture.Import(laterDetails);
        Equal(2, fixture.ImageFiles("Screenshot - Gameplay").Length, "repeat grew beyond cap after a user edit");
        Equal(2, fixture.Requests.Count, "repeat re-downloaded instead of finding the newer valid history record");
        Check(repeat.Contains("1 reused", StringComparison.Ordinal), "repeat did not locate the newest reusable same-key record");
        fixture.AssertNoTemporaryFiles();
    });

    private static ImportOptions EngineOptions() => new() { Metadata = false, Artwork = true, Videos = false, ThemeVideo = false };

    private static byte[] EnginePng(byte marker)
    {
        using var bitmap = new Bitmap(3, 3);
        bitmap.SetPixel(0, 0, Color.FromArgb(255, marker, 40, 90));
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static Task RunEngineOnSta(Func<Dispatcher, Task> action)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { await action(dispatcher); complete.TrySetResult(); }
                    catch (Exception error) { complete.TrySetException(error); }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                }));
                Dispatcher.Run();
            }
            catch (Exception error) { complete.TrySetException(error); }
        }) { IsBackground = true, Name = "Steam importer engine test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return complete.Task;
    }

    private sealed class EngineLimitFixture : IDisposable
    {
        public string Root { get; } = NewFixtureDirectory();
        public Dictionary<string, byte[]> Responses { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = new();
        public int PersistenceCalls { get; private set; }
        private readonly Dictionary<string, List<string>> sdkImages = new(StringComparer.OrdinalIgnoreCase);
        private readonly IGame game;
        private readonly HttpClient http;
        private readonly ImportEngine engine;
        private const string UrlRoot = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4685520/fixture/";

        public EngineLimitFixture(Dispatcher dispatcher)
        {
            game = DispatchProxy.Create<IGame, GameProxy>();
            var proxy = (GameProxy)(object)game;
            proxy.Values["Id"] = Guid.NewGuid().ToString();
            game.Title = "Fixture Game";
            game.Platform = "Windows";
            game.Region = "";
            proxy.Methods["GetVideoPath"] = _ => "";
            proxy.Methods["GetThemeVideoPath"] = _ => "";
            proxy.Methods["GetAllImagesWithDetails"] = args =>
            {
                string type = (string)args![0]!;
                return sdkImages.TryGetValue(type, out var paths) ? paths.Select(p => new ImageDetails(p, type, "")).ToArray() : Array.Empty<ImageDetails>();
            };
            proxy.Methods["GetNextAvailableImageFilePath"] = args => NextImagePath((string)args![1]!, (string)args[0]!);
            proxy.Methods["GetNextVideoFilePath"] = _ => Path.Combine(Root, "Videos", "Windows", "Fixture Game-01.mp4");
            http = new HttpClient(new StubHandler(request =>
            {
                string name = Path.GetFileName(request.RequestUri!.AbsolutePath);
                Requests.Add(name);
                return Responses.TryGetValue(name, out byte[]? bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            engine = new ImportEngine(Root, dispatcher, () => new MediaDownloader(Root, http), () => PersistenceCalls++);
        }

        public SteamAsset Asset(string key, string type, params string[] sources) => new()
        {
            Key = key, Label = key, ImageType = type, Sources = sources.Select(s => UrlRoot + s).ToList()
        };

        public SteamDetails SteamDetails(params SteamAsset[] assets) => new() { AppId = 4685520, Name = "Fixture Game", Assets = assets.ToList() };

        public Task<string> Import(SteamDetails details, ImportOptions? options = null) => engine.ImportAsync(game, details, options ?? EngineOptions(), new SilentEngineProgress(), CancellationToken.None);

        public string AddExistingImage(string type, byte[] bytes, bool exposeInSdk)
        {
            string path = NextImagePath(type, ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            if (exposeInSdk)
            {
                if (!sdkImages.TryGetValue(type, out var paths)) sdkImages[type] = paths = new List<string>();
                paths.Add(path);
            }
            return path;
        }

        private string NextImagePath(string type, string extension)
        {
            string folder = Path.Combine(Root, "Images", "Windows", type);
            int number = 1;
            string path;
            do { path = Path.Combine(folder, "Fixture Game-" + number++.ToString("00") + extension); } while (File.Exists(path));
            return path;
        }

        public string[] ImageFiles(string type)
        {
            string folder = Path.Combine(Root, "Images", "Windows", type);
            return Directory.Exists(folder) ? Directory.GetFiles(folder) : Array.Empty<string>();
        }

        public GameImportManifest ReadManifest()
        {
            string folder = Path.Combine(Root, "Plugins", "SteamMetadataImporter", "Data", "Manifests");
            return JsonSerializer.Deserialize<GameImportManifest>(File.ReadAllText(Directory.GetFiles(folder, "*.json").Single()))!;
        }

        public void AssertNoTemporaryFiles()
        {
            Check(!Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Any(p => p.EndsWith(".importing", StringComparison.Ordinal) || p.EndsWith(".part", StringComparison.Ordinal) || p.EndsWith(".tmp", StringComparison.Ordinal)), "engine left partial media files");
            string staging = Path.Combine(Root, "Plugins", "SteamMetadataImporter", "Data", "Staging");
            Check(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any(), "engine left its per-import staging directory");
        }

        public void Dispose()
        {
            http.Dispose();
            DeleteFixtureDirectory(Root);
        }
    }

    private sealed class SilentEngineProgress : IProgress<string>
    {
        public void Report(string value) { }
    }
}
