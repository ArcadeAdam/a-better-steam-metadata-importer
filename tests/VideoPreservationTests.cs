using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SteamMetadataImporter;
using Unbroken.LaunchBox.Plugins.Data;

internal static partial class Program
{
    private static void RegisterVideoPreservationTests()
    {
        Test("Explicit video assignments survive unavailable files", ExplicitVideoPreserved);
        Test("An existing discovered video is preserved with a blank raw field", DiscoveredVideoPreserved);
        Test("Theme video discovery uses the theme path", ThemeVideoPreserved);
        Test("Our new discovered trailers do not block assigning a missing path", ImportedVideoIgnored);
        Test("User assignments and newly discovered user files win over importer paths", UserVideoWins);
        Test("An unknown release date never fabricates Released status", UnknownReleaseStatus);
    }

    private static (IGame Game, GameProxy Proxy) VideoGame()
    {
        IGame game = System.Reflection.DispatchProxy.Create<IGame, GameProxy>();
        return (game, (GameProxy)(object)game);
    }

    private static void ExplicitVideoPreserved()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            var (game, _) = VideoGame();
            game.VideoPath = "Videos\\Offline user trailer.mp4";
            game.ThemeVideoPath = "Videos\\Offline user theme.mp4";
            Check(ImportEngine.HasExistingVideo(game, false, fixture), "explicit offline video lost");
            Check(ImportEngine.HasExistingVideo(game, true, fixture), "explicit offline theme video lost");
            // No discovery methods are configured: the explicit assignments must be sufficient.
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void DiscoveredVideoPreserved()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string relative = "Existing user trailer.mp4";
            File.WriteAllText(Path.Combine(fixture, relative), "Existing media presence fixture");
            var (game, proxy) = VideoGame();
            game.VideoPath = "  ";
            proxy.Methods["GetVideoPath"] = args => { Equal(false, (bool)args![0]!, "video discovery fallback argument"); return relative; };
            Check(ImportEngine.HasExistingVideo(game, false, fixture), "discovered relative video lost");
            proxy.Methods["GetVideoPath"] = _ => "Absent file.mp4";
            Check(!ImportEngine.HasExistingVideo(game, false, fixture), "missing discovered file counted as existing");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void ThemeVideoPreserved()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string path = Path.Combine(fixture, "Existing theme.mp4");
            File.WriteAllText(path, "Existing theme media presence fixture");
            var (game, proxy) = VideoGame();
            proxy.Methods["GetThemeVideoPath"] = _ => path;
            Check(ImportEngine.HasExistingVideo(game, true, fixture), "theme discovery ignored");
            // GetVideoPath deliberately remains unconfigured to detect accidental normal-video fallback.
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void ImportedVideoIgnored()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string path = Path.Combine(fixture, "New imported trailer.mp4");
            File.WriteAllText(path, "Importer-created media presence fixture");
            var (game, proxy) = VideoGame();
            proxy.Methods["GetVideoPath"] = _ => Path.GetFileName(path);
            proxy.Methods["GetThemeVideoPath"] = _ => path;
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            Check(!ImportEngine.HasExistingVideo(game, false, fixture, owned), "new own video blocked missing-path assignment");
            Check(!ImportEngine.HasExistingVideo(game, true, fixture, owned), "new own theme blocked missing-path assignment");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void UserVideoWins()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string ownedPath = Path.Combine(fixture, "Importer trailer.mp4");
            string userPath = Path.Combine(fixture, "New user trailer.mp4");
            File.WriteAllText(ownedPath, "Importer media fixture");
            File.WriteAllText(userPath, "User media fixture");
            var (game, proxy) = VideoGame();
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ownedPath };
            proxy.Methods["GetVideoPath"] = _ => userPath;
            proxy.Methods["GetThemeVideoPath"] = _ => userPath;
            Check(ImportEngine.HasExistingVideo(game, false, fixture, owned), "new user video mistaken for our download");
            Check(ImportEngine.HasExistingVideo(game, true, fixture, owned), "new user theme mistaken for our download");
            // The user may explicitly choose even the newly imported file after preview.
            game.VideoPath = ownedPath;
            game.ThemeVideoPath = ownedPath;
            Check(ImportEngine.HasExistingVideo(game, false, fixture, owned), "explicit user video overridden by ignore set");
            Check(ImportEngine.HasExistingVideo(game, true, fixture, owned), "explicit user theme overridden by ignore set");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static void UnknownReleaseStatus()
    {
        var details = Details();
        details.ComingSoon = false;
        details.ReleaseDate = null;
        Check(!MetadataPlan.Build(new Dictionary<string, object?>(), details).Any(c => c.Field == "ReleaseType"), "unknown date fabricated Released status");
    }
}
