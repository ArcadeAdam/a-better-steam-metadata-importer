using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Unbroken.LaunchBox.Plugins.Data;

namespace SteamMetadataImporter;

public sealed record FieldChange(string Field, object? Before, object After);

public static class MetadataPlan
{
    public static readonly string[] Fields = { "Title", "Notes", "Developer", "Publisher", "GenresString", "PlayMode", "Series", "Source", "ReleaseDate", "ReleaseType", "MaxPlayers" };

    public static IReadOnlyList<FieldChange> Build(IGame game, SteamDetails details)
    {
        var current = Fields.ToDictionary(x => x, x => typeof(IGame).GetProperty(x)!.GetValue(game));
        return Build(current, details);
    }

    public static IReadOnlyList<FieldChange> Build(IReadOnlyDictionary<string, object?> current, SteamDetails details)
    {
        var offered = new Dictionary<string, object?> {
            ["Title"] = details.Name, ["Notes"] = details.Description, ["Developer"] = details.Developer,
            ["Publisher"] = details.Publisher, ["GenresString"] = details.Genres, ["PlayMode"] = details.PlayMode,
            ["Series"] = details.Series, ["Source"] = "Steam", ["ReleaseDate"] = details.ReleaseDate,
            ["ReleaseType"] = !details.ComingSoon && details.ReleaseDate.HasValue ? "Released" : null, ["MaxPlayers"] = details.MaxPlayers
        };
        return offered.Where(x => Missing(current.GetValueOrDefault(x.Key)) && !Missing(x.Value))
            .Select(x => new FieldChange(x.Key, current.GetValueOrDefault(x.Key), x.Value!)).ToList();
    }

    public static bool Missing(object? value) => value is null || value is string text && string.IsNullOrWhiteSpace(text) || value is int n && n <= 0;

    // Recheck on the UI thread immediately before writing: edits made after preview must win.
    public static List<string> ApplyMissing(IGame game, IReadOnlyList<FieldChange> changes)
    {
        var applied = new List<string>();
        foreach (var change in changes)
        {
            PropertyInfo property = typeof(IGame).GetProperty(change.Field) ?? throw new InvalidOperationException(change.Field);
            if (!Missing(property.GetValue(game))) continue;
            property.SetValue(game, change.After);
            applied.Add(change.Field);
        }
        if (applied.Count > 0) game.DateModified = DateTime.Now;
        return applied;
    }

    public static string Describe(FieldChange field)
    {
        string text = field.After is DateTime date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Convert.ToString(field.After, CultureInfo.InvariantCulture) ?? "";
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return field.Field + ": " + (text.Length > 190 ? text.Substring(0, 187) + "..." : text);
    }
}
