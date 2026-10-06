using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SteamMetadataImporter;
using Unbroken.LaunchBox.Plugins.Data;

internal static partial class Program
{
    private static readonly List<(string Name, Func<Task> Run)> Tests = new();
    private static int Passed;
    private static int Failed;

    private static async Task<int> Main(string[] args)
    {
        Test("Steam IDs resolve from supported URLs and launcher commands", IdentityFormats);
        Test("Invalid IDs and lookalike hosts are rejected", InvalidIdentity);
        Test("Internet shortcuts resolve without changing their launch paths", ShortcutIdentity);
        Test("Metadata fills empty fields and preserves curated values", MetadataPreservation);
        Test("Upcoming releases leave unknown dates and release status alone", UpcomingMetadata);
        Test("Edits made after preview win at apply time", MetadataApplyRace);
        RegisterVideoPreservationTests();
        RegisterMediaLimitsTests();
        RegisterMediaInventoryTests();
        RegisterImportEngineLimitTests();
        RegisterClientTests();
        RegisterDownloadTests();
        foreach (var test in Tests)
        {
            try { await test.Run(); Passed++; Console.WriteLine("PASS " + test.Name); }
            catch (Exception ex) { Failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex); }
        }
        if (args.Contains("--live", StringComparer.OrdinalIgnoreCase) || args.Contains("--live-media", StringComparer.OrdinalIgnoreCase))
        {
            try { await LiveCheck(args); Passed++; Console.WriteLine("PASS live Steam discovery"); }
            catch (Exception ex) { Failed++; Console.WriteLine("FAIL live Steam discovery: " + ex); }
        }
        Console.WriteLine($"{Passed} passed, {Failed} failed.");
        return Failed == 0 ? 0 : 1;
    }

    private static void Test(string name, Action action) => Tests.Add((name, () => { action(); return Task.CompletedTask; }));
    private static void Test(string name, Func<Task> action) => Tests.Add((name, action));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Equal<T>(T expected, T actual, string message) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected '{expected}', got '{actual}'");

    private static void IdentityFormats()
    {
        foreach (string input in new[] { "4685520", "steam://rungameid/4685520", "steam://run/4685520", "https://store.steampowered.com/app/4685520/Railbreak_DX/", "https://steamdb.info/app/4685520/", "https://steamcommunity.com/app/4685520", "-applaunch 4685520 -fullscreen", "  \"steam://rungameid/4685520\"  " })
            Equal<int?>(4685520, SteamIdentity.Parse(input), input);
    }

    private static void InvalidIdentity()
    {
        foreach (string? input in new[] { null, "", "0", "-1", "2147483648", "steam://rungameid/0", "steam://rungameid/nope", "https://store.steampowered.com.evil.invalid/app/4685520/", "https://evil.invalid/app/4685520/", "https://user@store.steampowered.com/app/4685520/", "file:///app/4685520", "https://store.steampowered.com/app/4685520x" })
            Equal<int?>(null, SteamIdentity.Parse(input), input ?? "null");
    }

    private static void ShortcutIdentity()
    {
        string fixture = NewFixtureDirectory();
        try
        {
            string path = Path.Combine(fixture, "Railbreak DX.url");
            string content = "[InternetShortcut]\r\nURL=steam://rungameid/4685520\r\nIconIndex=0\r\n";
            File.WriteAllText(path, content);
            Equal<int?>(4685520, SteamIdentity.Resolve(path, null, fixture), "absolute .url");
            Equal<int?>(4685520, SteamIdentity.Resolve("Railbreak DX.url", null, fixture), "relative .url");
            Equal(content, File.ReadAllText(path), "shortcut bytes remain unchanged");
            File.WriteAllText(path, "[InternetShortcut]\nURL=https://evil.invalid/app/4685520/");
            Equal<int?>(null, SteamIdentity.Resolve(path, null, fixture), "non-Steam shortcut");
            File.WriteAllText(path, new string(' ', 65537) + "\nURL=steam://rungameid/4685520");
            Equal<int?>(null, SteamIdentity.Resolve(path, null, fixture), "oversized shortcut");
        }
        finally { DeleteFixtureDirectory(fixture); }
    }

    private static SteamDetails Details() => new()
    {
        AppId = 4685520, Name = "Store title", Description = "Store description", Developer = "Store developer",
        Publisher = "Store publisher", Genres = "Action; Arcade", PlayMode = "Single Player", Series = "Railbreak",
        ReleaseDate = new DateTime(2026, 9, 15), MaxPlayers = 2
    };

    private static void MetadataPreservation()
    {
        var current = new Dictionary<string, object?> {
            ["Title"] = "Curated title", ["Notes"] = "My notes", ["Developer"] = "  ", ["Publisher"] = null,
            ["Source"] = "Manual collection", ["MaxPlayers"] = 0, ["ReleaseDate"] = new DateTime(2024, 1, 2),
            ["ApplicationPath"] = "keep.url", ["CommandLine"] = "--keep", ["Emulator"] = "keep emulator"
        };
        var changes = MetadataPlan.Build(current, Details()).ToDictionary(c => c.Field);
        Check(!changes.ContainsKey("Title") && !changes.ContainsKey("Notes") && !changes.ContainsKey("Source") && !changes.ContainsKey("ReleaseDate"), "curated metadata offered for overwrite");
        Equal("Store developer", (string)changes["Developer"].After, "blank developer filled");
        Equal(2, (int)changes["MaxPlayers"].After, "unknown player count filled");
        Check(!changes.Keys.Any(k => k is "ApplicationPath" or "CommandLine" or "Emulator"), "launch settings offered for change");
    }

    private static void UpcomingMetadata()
    {
        SteamDetails details = Details();
        details.ComingSoon = true;
        details.ReleaseDate = null;
        details.MaxPlayers = null;
        var changes = MetadataPlan.Build(new Dictionary<string, object?>(), details);
        Check(!changes.Any(c => c.Field is "ReleaseDate" or "ReleaseType" or "MaxPlayers"), "unknown/upcoming fields should remain unknown");
    }

    private static void MetadataApplyRace()
    {
        IGame game = DispatchProxy.Create<IGame, GameProxy>();
        var proxy = (GameProxy)(object)game;
        proxy.Values["Title"] = "Local title";
        proxy.Values["ApplicationPath"] = "keep.url";
        proxy.Values["CommandLine"] = "--keep";
        proxy.Values["DateModified"] = new DateTime(2020, 1, 1);
        var changes = MetadataPlan.Build(game, Details());
        game.Notes = "User edited after preview";
        var applied = MetadataPlan.ApplyMissing(game, changes);
        Equal("User edited after preview", game.Notes, "concurrent Notes edit");
        Equal("Local title", game.Title, "title preserved");
        Equal("keep.url", game.ApplicationPath, "launch path preserved");
        Equal("--keep", game.CommandLine, "launch command preserved");
        Equal("Store developer", game.Developer, "missing developer applied");
        Check(applied.Count > 0 && !applied.Contains("Notes"), "applied field report");
        var changedDate = game.DateModified;
        Equal(0, MetadataPlan.ApplyMissing(game, changes).Count, "second apply makes no changes");
        Equal(changedDate, game.DateModified, "no-op apply preserves date");
    }

    private static string NewFixtureDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "SteamMetadataImporter.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteFixtureDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        string expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "SteamMetadataImporter.Tests-";
        if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture cleanup path");
        Directory.Delete(full, true);
    }
}

public class GameProxy : DispatchProxy
{
    public Dictionary<string, object?> Values { get; } = new();
    public Dictionary<string, Func<object?[]?, object?>> Methods { get; } = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        if (method.Name.StartsWith("set_", StringComparison.Ordinal)) { Values[method.Name.Substring(4)] = args![0]; return null; }
        if (method.Name.StartsWith("get_", StringComparison.Ordinal))
        {
            if (Values.TryGetValue(method.Name.Substring(4), out var value)) return value;
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
        if (Methods.TryGetValue(method.Name, out var handler)) return handler(args);
        throw new NotSupportedException(method.Name);
    }
}
