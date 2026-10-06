using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SteamMetadataImporter;
using Unbroken.LaunchBox.Plugins.Data;

internal static partial class Program
{
    private static void RegisterMediaInventoryTests()
    {
        Test("Image inventory unions SDK and manifest paths without counting aliases or missing files", ImageInventoryUnion);
        Test("Image inventory isolates types while retaining legacy and current record mappings", ImageInventoryTypeIsolation);
        Test("Stale SDK inventory and edited imported files still occupy the per-type cap", ImageInventoryStaleCache);
        Test("Video inventory combines assignments, resolved paths, manifest records and numbered siblings", VideoInventoryUnion);
        Test("Video inventory strips only the SDK counter and preserves numeric game titles", VideoInventoryNumericTitle);
        Test("Video inventory keeps legacy fallback trailer records absent from current Steam data", VideoInventoryLegacyRecords);
        Test("Video inventory ignores absent files and works without a proposed media folder", VideoInventoryMissingFiles);
    }

    private static (IGame Game, GameProxy Proxy) InventoryGame(string proposed = "")
    {
        IGame game = DispatchProxy.Create<IGame, GameProxy>();
        var proxy = (GameProxy)(object)game;
        proxy.Methods["GetAllImagesWithDetails"] = _ => Array.Empty<ImageDetails>();
        proxy.Methods["GetVideoPath"] = args =>
        {
            Equal(false, (bool)args![0]!, "inventory resolves normal video rather than theme priority");
            return "";
        };
        proxy.Methods["GetThemeVideoPath"] = _ => "";
        proxy.Methods["GetNextVideoFilePath"] = args =>
        {
            Equal("", (string)args![0]!, "standard video destination type");
            Equal(".mp4", (string)args[1]!, "video inventory naming probe extension");
            return proposed;
        };
        return (game, proxy);
    }

    private static string InventoryFile(string fixture, string relative, string content = "Media presence fixture")
    {
        string path = Path.GetFullPath(relative, fixture);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void EqualInventory(IEnumerable<string> actual, params string[] expected)
    {
        var paths = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(paths.SetEquals(expected), "inventory paths differ; expected [" + string.Join(", ", expected) + "] but got [" + string.Join(", ", paths) + "]");
    }

    private static void ImageInventoryUnion()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string sdk = InventoryFile(fixture, "Images/Windows/Box - Front/World/Game-01.png");
            string imported = InventoryFile(fixture, "Images/Windows/Box - Front/Game-02.png");
            string absent = Path.Combine(fixture, "Images/Windows/Box - Front/Absent.png");
            var (game, proxy) = InventoryGame();
            proxy.Methods["GetAllImagesWithDetails"] = args =>
            {
                Equal("Box - Front", (string)args![0]!, "SDK exact image type requested");
                return new[]
                {
                    new ImageDetails(Path.GetRelativePath(fixture, sdk), "Box - Front", "World"),
                    new ImageDetails(sdk.ToUpperInvariant(), "Box - Front", "World"),
                    new ImageDetails(absent, "Box - Front", "")
                };
            };
            var manifest = new GameImportManifest
            {
                Assets = new List<AssetRecord>
                {
                    new() { Key = "portrait-front", Path = sdk },
                    new() { Key = "second-cover", MediaType = "Box - Front", Path = Path.GetRelativePath(fixture, imported) },
                    new() { Key = "third-cover", MediaType = "Box - Front", Path = absent },
                    new() { Key = "invalid-cover", MediaType = "Box - Front", Path = "invalid\0path" },
                    new() { Key = "folder-is-not-a-file", MediaType = "Box - Front", Path = Path.GetDirectoryName(imported)! }
                }
            };
            EqualInventory(ImportEngine.GetKnownMediaPaths(game, "Box - Front", manifest, new SteamDetails(), fixture), sdk, imported);
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void ImageInventoryTypeIsolation()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string front = InventoryFile(fixture, "Images/Box - Front/Game.png", "identical portrait");
            string poster = InventoryFile(fixture, "Images/Steam Poster/Game.png", "identical portrait");
            string logo = InventoryFile(fixture, "Images/Clear Logo/Game.png");
            string banner = InventoryFile(fixture, "Images/Banner/Game.jpg");
            string other = InventoryFile(fixture, "Images/Unknown/Game.jpg");
            var (game, proxy) = InventoryGame();
            proxy.Methods["GetAllImagesWithDetails"] = args => (string)args![0]! switch
            {
                "Box - Front" => new[] { new ImageDetails(front, "Box - Front", "") },
                "Steam Poster" => new[] { new ImageDetails(poster, "Steam Poster", "") },
                _ => Array.Empty<ImageDetails>()
            };
            var manifest = new GameImportManifest
            {
                Assets = new List<AssetRecord>
                {
                    new() { Key = "portrait-front", Path = front },
                    new() { Key = "portrait-steam", Path = poster },
                    new() { Key = "current-custom-key", Path = logo },
                    new() { Key = "header", Path = banner },
                    new() { Key = "unknown-old-key", Path = other }
                }
            };
            var details = new SteamDetails { Assets = new List<SteamAsset> { new() { Key = "current-custom-key", ImageType = "Clear Logo" } } };
            EqualInventory(ImportEngine.GetKnownMediaPaths(game, "Box - Front", manifest, details, fixture), front);
            EqualInventory(ImportEngine.GetKnownMediaPaths(game, "Steam Poster", manifest, details, fixture), poster);
            EqualInventory(ImportEngine.GetKnownMediaPaths(game, "Clear Logo", manifest, details, fixture), logo);
            EqualInventory(ImportEngine.GetKnownMediaPaths(game, "Banner", manifest, details, fixture), banner);
            EqualInventory(ImportEngine.GetKnownMediaPaths(game, "Screenshot - Gameplay", manifest, details, fixture));
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void ImageInventoryStaleCache()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string first = InventoryFile(fixture, "Images/Screenshot - Gameplay/Game-01.jpg");
            string second = InventoryFile(fixture, "Images/Screenshot - Gameplay/Game-02.jpg");
            var (game, _) = InventoryGame(); // The SDK deliberately reports no cached images.
            var manifest = new GameImportManifest
            {
                Assets = new List<AssetRecord>
                {
                    new() { Key = "screenshot-0", Path = first, Sha256 = "old imported hash" },
                    new() { Key = "screenshot-1", Path = second, Sha256 = "old imported hash" }
                }
            };
            File.WriteAllText(second, "User replaced the imported screenshot");
            var inventory = ImportEngine.GetKnownMediaPaths(game, "Screenshot - Gameplay", manifest, new SteamDetails(), fixture);
            EqualInventory(inventory, first, second);
            Check(!MediaLimits.HasRoom(2, inventory), "repeat import exceeds cap when SDK cache is stale");
            Equal("User replaced the imported screenshot", File.ReadAllText(second), "inventory changed user replacement");
            File.Delete(first);
            inventory = ImportEngine.GetKnownMediaPaths(game, "Screenshot - Gameplay", manifest, new SteamDetails(), fixture);
            EqualInventory(inventory, second);
            Check(MediaLimits.HasRoom(2, inventory), "a deleted file permanently consumes capacity");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void VideoInventoryUnion()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            const string folder = "Videos/Windows";
            string first = InventoryFile(fixture, folder + "/Game [DX]+-01.mp4");
            string second = InventoryFile(fixture, folder + "/Game [DX]+-02.WEBM");
            string unnumbered = InventoryFile(fixture, folder + "/Game [DX]+.mkv");
            string nestedCounter = InventoryFile(fixture, folder + "/Game [DX]+-02-03.mp4");
            string custom = InventoryFile(fixture, "Custom/user-chosen.mp4");
            string theme = InventoryFile(fixture, "Theme/custom-theme.mp4");
            string tracked = InventoryFile(fixture, "Archived/imported-trailer.mp4");
            foreach (string unrelated in new[] { "Other Game-01.mp4", "Game [DX]+2.mp4", "Game [DX]+-trailer.mp4", "Game [DX]+-01.jpg", "Game [DX]+-01.mp4.importing" })
                InventoryFile(fixture, folder + "/" + unrelated);
            InventoryFile(fixture, folder + "/Other/Game [DX]+-03.mp4");
            var (game, proxy) = InventoryGame(folder + "/Game [DX]+-99.mp4");
            game.VideoPath = Path.GetRelativePath(fixture, custom);
            game.ThemeVideoPath = custom.ToUpperInvariant(); // Same file must occupy only one slot.
            proxy.Methods["GetVideoPath"] = _ => first;
            proxy.Methods["GetThemeVideoPath"] = _ => Path.GetRelativePath(fixture, theme);
            var manifest = new GameImportManifest
            {
                Assets = new List<AssetRecord>
                {
                    new() { Key = "trailer-100", Path = first.ToUpperInvariant() },
                    new() { Key = "tracked", MediaType = "Video", Path = tracked },
                    new() { Key = "missing", MediaType = "Video", Path = Path.Combine(fixture, "missing.mp4") }
                }
            };
            EqualInventory(ImportEngine.GetKnownMediaPaths(game, "Video", manifest, new SteamDetails(), fixture),
                first, second, unnumbered, nestedCounter, custom, theme, tracked);
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void VideoInventoryNumericTitle()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string exact = InventoryFile(fixture, "Videos/Area-51.mp4");
            string numbered = InventoryFile(fixture, "Videos/Area-51-01.mp4");
            string numberedTwo = InventoryFile(fixture, "Videos/Area-51-02.webm");
            InventoryFile(fixture, "Videos/Area.mp4");
            InventoryFile(fixture, "Videos/Area-01.mp4");
            InventoryFile(fixture, "Videos/Area-5101.mp4");
            var (game, _) = InventoryGame("Videos/Area-51-03.mp4");
            EqualInventory(ImportEngine.GetKnownMediaPaths(game, "Video", new GameImportManifest(), new SteamDetails(), fixture), exact, numbered, numberedTwo);
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void VideoInventoryLegacyRecords()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string numeric = InventoryFile(fixture, "Old/numeric.mp4");
            string browse = InventoryFile(fixture, "Old/browse.mp4");
            string store = InventoryFile(fixture, "Old/store.mp4");
            string image = InventoryFile(fixture, "Old/front.png");
            var (game, _) = InventoryGame();
            var manifest = new GameImportManifest
            {
                Assets = new List<AssetRecord>
                {
                    new() { Key = "trailer-12345", Path = numeric },
                    new() { Key = "trailer-browse-6789", Path = browse },
                    new() { Key = "trailer-store-1", Path = store },
                    new() { Key = "portrait-front", Path = image }
                }
            };
            EqualInventory(ImportEngine.GetKnownMediaPaths(game, "Video", manifest, new SteamDetails(), fixture), numeric, browse, store);
            EqualInventory(ImportEngine.GetKnownMediaPaths(game, "Box - Front", manifest, new SteamDetails(), fixture), image);
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void VideoInventoryMissingFiles()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string tracked = InventoryFile(fixture, "Old/current.mp4");
            var (game, proxy) = InventoryGame();
            game.VideoPath = "Offline/explicit.mp4";
            game.ThemeVideoPath = "Offline/theme.mp4";
            proxy.Methods["GetVideoPath"] = _ => "Absent/discovered.mp4";
            proxy.Methods["GetThemeVideoPath"] = _ => "Absent/discovered-theme.mp4";
            var manifest = new GameImportManifest
            {
                Assets = new List<AssetRecord>
                {
                    new() { Key = "trailer-1", Path = "Old/current.mp4" },
                    new() { Key = "trailer-2", Path = tracked.ToUpperInvariant() },
                    new() { Key = "trailer-3", Path = "Old/absent.mp4" }
                }
            };
            EqualInventory(ImportEngine.GetKnownMediaPaths(game, "video", manifest, new SteamDetails(), fixture), tracked);
            Equal("Offline/explicit.mp4", game.VideoPath, "inventory changed offline explicit assignment");
            Equal("Offline/theme.mp4", game.ThemeVideoPath, "inventory changed offline theme assignment");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }
}
