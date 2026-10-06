using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SteamMetadataImporter;

internal static partial class Program
{
    private static void RegisterMediaLimitsTests()
    {
        Test("Media limits default independently to two for all eight types", MediaLimitDefaults);
        Test("Media limit text accepts blank, zero and bounded whole numbers", MediaLimitParsing);
        Test("Media limit settings round-trip and fill missing categories", MediaLimitPersistence);
        Test("Invalid media limit settings never overwrite saved preferences", MediaLimitInvalidSettings);
        Test("Asset records recover types from explicit, current and legacy data", MediaLimitTypeMapping);
        Test("Existing media inventory canonicalizes duplicates and ignores absent files", MediaLimitInventory);
        Test("Lowered and zero limits preserve existing files without more capacity", MediaLimitCapacity);
        Test("Video siblings require an exact game stem, numeric suffix and matching folder", MediaLimitVideoSiblings);
    }

    private static void MediaLimitDefaults()
    {
        var limits = MediaLimits.CreateDefaults();
        Equal(8, limits.Count, "supported media type count");
        foreach (string type in MediaLimits.Types) Equal<int?>(2, limits[type], type);
        Equal<int?>(2, limits["video"], "type lookup ignores case");
        limits["Video"] = null;
        Equal<int?>(2, MediaLimits.CreateDefaults()["Video"], "defaults are independent dictionaries");
    }

    private static void MediaLimitParsing()
    {
        foreach (string? input in new string?[] { null, "", " \t\r\n" })
        {
            Check(MediaLimits.TryParse(input, out int? limit), "blank rejected");
            Equal<int?>(null, limit, "blank means unlimited");
        }
        foreach (var pair in new[] { ("0", 0), ("2", 2), ("0002", 2), (" 9999 ", 9999) })
        {
            Check(MediaLimits.TryParse(pair.Item1, out int? limit), "valid limit rejected: " + pair.Item1);
            Equal<int?>(pair.Item2, limit, "parsed limit");
        }
        foreach (string input in new[] { "-1", "+2", "2.5", "2.0", "1e2", "1,000", "10000", "2147483648", "unlimited", "NaN" })
            Check(!MediaLimits.TryParse(input, out _), "invalid limit accepted: " + input);
    }

    private static void MediaLimitPersistence()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string file = Path.Combine(fixture, "settings", "media-limits.json");
            Equal<int?>(2, MediaLimits.Load(file)["Video"], "missing file defaults");
            var preferences = new Dictionary<string, int?> { ["video"] = 1, ["Banner"] = null, ["Screenshot - Gameplay"] = 0 };
            MediaLimits.Save(file, preferences);
            var loaded = MediaLimits.Load(file);
            Equal<int?>(1, loaded["Video"], "saved video limit");
            Equal<int?>(null, loaded["Banner"], "unlimited saved as null");
            Equal<int?>(0, loaded["Screenshot - Gameplay"], "disabled category preserved");
            Equal<int?>(2, loaded["Box - Front"], "missing category defaulted");
            File.WriteAllText(file, "{\"VIDEO\":3,\"future-category\":{\"new\":true}}");
            loaded = MediaLimits.Load(file);
            Equal<int?>(3, loaded["Video"], "case-insensitive known JSON category");
            Equal(8, loaded.Count, "unknown categories ignored");
            loaded["Video"] = 9999;
            MediaLimits.Save(file, loaded);
            Equal<int?>(9999, MediaLimits.Load(file)["Video"], "valid replacement persisted");
            Equal(1, Directory.GetFiles(Path.GetDirectoryName(file)!).Length, "atomic save leaves no temporary file");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void MediaLimitInvalidSettings()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string file = Path.Combine(fixture, "media-limits.json");
            foreach (string invalid in new[] { "[]", "null", "{", "{\"Video\":-1}", "{\"Video\":10000}", "{\"Video\":1.5}", "{\"Video\":\"2\"}", "{\"Video\":true}", "{\"Video\":1,\"video\":2}" })
            {
                File.WriteAllText(file, invalid);
                ExpectMediaLimitFailure(() => MediaLimits.Load(file));
                Equal(invalid, File.ReadAllText(file), "read failure preserves original settings");
            }
            MediaLimits.Save(file, MediaLimits.CreateDefaults());
            string saved = File.ReadAllText(file);
            foreach (int invalid in new[] { -1, 10000 })
            {
                var preferences = MediaLimits.CreateDefaults(); preferences["Video"] = invalid;
                ExpectMediaLimitFailure(() => MediaLimits.Save(file, preferences));
                Equal(saved, File.ReadAllText(file), "invalid save changed existing settings");
            }
            var duplicates = new Dictionary<string, int?> { ["Video"] = 1, ["video"] = 2 };
            ExpectMediaLimitFailure(() => MediaLimits.Save(file, duplicates));
            Equal(saved, File.ReadAllText(file), "duplicate setting changed existing settings");
            Equal(1, Directory.GetFiles(fixture).Length, "rejected saves leave no temporary files");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void MediaLimitTypeMapping()
    {
        var details = new SteamDetails
        {
            Assets = new List<SteamAsset>
            {
                new() { Key = "changed-key", ImageType = "Clear Logo" },
                new() { Key = "new-trailer", IsVideo = true, ImageType = "Banner" }
            }
        };
        Equal("Video", MediaLimits.ForAsset(details.Assets[1]), "video flag determines type");
        ExpectMediaLimitFailure(() => MediaLimits.ForAsset(new SteamAsset()));
        Equal("Clear Logo", MediaLimits.ForRecord(new AssetRecord { Key = "CHANGED-KEY" }, details), "current asset lookup");
        Equal("Steam Poster", MediaLimits.ForRecord(new AssetRecord { Key = "changed-key", MediaType = "steam poster" }, details), "explicit type wins");
        Equal("Future Image Type", MediaLimits.ForRecord(new AssetRecord { Key = "changed-key", MediaType = "Future Image Type" }, details), "explicit future type retained");
        var legacy = new Dictionary<string, string>
        {
            ["portrait-front"] = "Box - Front", ["portrait-steam"] = "Steam Poster", ["store-hero-capsule"] = "Steam Poster",
            ["clear-logo"] = "Clear Logo", ["header"] = "Banner", ["store-main-capsule"] = "Steam Banner",
            ["store-small-capsule"] = "Steam Banner", ["community-icon"] = "Steam Banner", ["page-background"] = "Fanart - Background",
            ["library-hero"] = "Fanart - Background", ["screenshot-0"] = "Screenshot - Gameplay", ["trailer-256999999"] = "Video",
            ["trailer-browse-6789"] = "Video", ["trailer-store-1"] = "Video", ["TRAILER-browse-Launch trailer"] = "Video"
        };
        foreach (var item in legacy) Equal(item.Value, MediaLimits.ForRecord(new AssetRecord { Key = item.Key }, details), "legacy type: " + item.Key);
        Equal<string?>(null, MediaLimits.ForRecord(new AssetRecord { Key = "unknown-old-record" }, details), "unknown record is not guessed");
        Equal<string?>(null, MediaLimits.ForRecord(new AssetRecord { Key = "trailer-" }, details), "empty trailer identifier is not guessed");
        Equal<string?>(null, MediaLimits.ForRecord(new AssetRecord { Key = "trailerish-1" }, details), "unrelated prefix is not guessed");
        Equal("Box - Front", MediaLimits.ForAsset(new SteamAsset { ImageType = "Box - Front" }), "portrait front is its own quota");
        Equal("Steam Poster", MediaLimits.ForAsset(new SteamAsset { ImageType = "Steam Poster" }), "same portrait poster is separate quota");
    }

    private static void MediaLimitInventory()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string media = Path.Combine(fixture, "media"); Directory.CreateDirectory(media);
            string file = Path.Combine(media, "Existing.mp4"); File.WriteAllText(file, "original media");
            var inventory = MediaLimits.NormalizeExisting(new string?[] { file, file.ToUpperInvariant(), "media/Existing.mp4", "media/./Existing.mp4", "media/missing.mp4", media, null, " ", "invalid\0path" }, fixture);
            Equal(1, inventory.Count, "API/manifest aliases deduplicated and absent files ignored");
            Check(inventory.Contains(file.ToUpperInvariant()), "inventory uses case-insensitive paths");
            File.WriteAllText(file, "user replacement of imported media");
            Equal(1, MediaLimits.NormalizeExisting(inventory, fixture).Count, "user-edited existing file still consumes capacity");
            Equal("user replacement of imported media", File.ReadAllText(file), "inventory preserves user bytes");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void MediaLimitCapacity()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string[] files = Enumerable.Range(1, 3).Select(n => Path.Combine(fixture, "image-" + n + ".png")).ToArray();
            foreach (string file in files) File.WriteAllText(file, "retained image");
            var existing = MediaLimits.NormalizeExisting(files, fixture);
            Check(!MediaLimits.HasRoom(2, existing), "lowered limit allows another file");
            Check(!MediaLimits.HasRoom(0, Array.Empty<string>()), "zero permits a new file");
            Check(MediaLimits.HasRoom(null, existing), "blank/unlimited blocks more files");
            Check(MediaLimits.HasRoom(2, new[] { files[0], files[0].ToUpperInvariant() }), "aliases double-count quota");
            Check(!MediaLimits.HasRoom(2, files.Take(2)), "full quota permits a third file");
            Check(MediaLimits.HasRoom(2, files.Take(1)), "one occupied slot should leave one free");
            ExpectMediaLimitFailure(() => MediaLimits.HasRoom(-1, existing));
            Equal(3, Directory.GetFiles(fixture).Length, "lowering cap removed an existing file");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void MediaLimitVideoSiblings()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string proposed = Path.Combine(fixture, "Game [DX]+-01.mp4");
            foreach (string name in new[] { "Game [DX]+.mp4", "Game [DX]+-01.webm", "Game [DX]+-2-03.MKV" })
                Check(MediaLimits.IsVideoSibling(Path.Combine(fixture, name), proposed), "valid sibling rejected: " + name);
            foreach (string name in new[] { "Game [DX]+2.mp4", "Game [DX]+-trailer.mp4", "Game [DX]+-.mp4", "Game [DX]+-2-other.mp4", "Game [DX]+-01.jpg", "Game XDX-01.mp4", "Other Game-01.mp4" })
                Check(!MediaLimits.IsVideoSibling(Path.Combine(fixture, name), proposed), "other game/nonvideo accepted: " + name);
            Check(!MediaLimits.IsVideoSibling(Path.Combine(fixture, "other", "Game [DX]+-01.mp4"), proposed), "different directory accepted");
            Check(!MediaLimits.IsVideoSibling("invalid\0path", proposed), "invalid candidate accepted");
            string numberedTitle = Path.Combine(fixture, "Game-01-02.mp4");
            foreach (string name in new[] { "Game-01.mp4", "Game-01-01.mp4", "Game-01-03.webm", "Game-01-01-02.mp4" })
                Check(MediaLimits.IsVideoSibling(Path.Combine(fixture, name), numberedTitle), "numeric-ending title sibling rejected: " + name);
            foreach (string name in new[] { "Game.mp4", "Game-02.mp4", "Game-02-01.mp4", "Game-01-sequel.mp4" })
                Check(!MediaLimits.IsVideoSibling(Path.Combine(fixture, name), numberedTitle), "numeric-ending title confused with another game: " + name);
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void ExpectMediaLimitFailure(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (ArgumentException) { rejected = true; }
        catch (InvalidDataException) { rejected = true; }
        catch (JsonException) { rejected = true; }
        Check(rejected, "invalid media limit operation did not fail");
    }
}
