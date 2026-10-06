using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamMetadataImporter;

public static class MediaLimits
{
    public const int DefaultLimit = 2;
    public static readonly string[] Types =
    {
        "Box - Front", "Steam Poster", "Clear Logo", "Banner", "Steam Banner",
        "Fanart - Background", "Screenshot - Gameplay", "Video"
    };

    private static readonly Dictionary<string, string> KnownTypes = Types.ToDictionary(t => t, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".avi", ".wmv", ".webm", ".mov", ".mpg", ".mpeg", ".flv", ".ts", ".m2ts", ".ogv"
    };

    public static string ForAsset(SteamAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.IsVideo ? "Video" : CanonicalType(asset.ImageType) ?? throw new InvalidDataException("An image asset must specify its LaunchBox media type.");
    }

    public static string? ForRecord(AssetRecord record, SteamDetails details)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(details);
        if (!string.IsNullOrWhiteSpace(record.MediaType)) return CanonicalType(record.MediaType);
        SteamAsset? current = details.Assets.FirstOrDefault(a => string.Equals(a.Key, record.Key, StringComparison.OrdinalIgnoreCase));
        string? currentType = current == null ? null : current.IsVideo ? "Video" : CanonicalType(current.ImageType);
        if (currentType != null) return currentType;
        string key = record.Key ?? "";
        // Store/browse fallbacks use labels or prefixed indexes when Steam omits
        // numeric IDs. These still identify a trailer from an older manifest.
        if (key.StartsWith("trailer-", StringComparison.OrdinalIgnoreCase) && key.Length > "trailer-".Length) return "Video";
        if (Regex.IsMatch(key, @"^screenshot-\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Screenshot - Gameplay";
        return key.ToLowerInvariant() switch
        {
            "portrait-front" => "Box - Front",
            "portrait-steam" or "store-hero-capsule" => "Steam Poster",
            "clear-logo" => "Clear Logo",
            "header" => "Banner",
            "store-main-capsule" or "store-small-capsule" or "community-icon" => "Steam Banner",
            "page-background" or "library-hero" => "Fanart - Background",
            _ => null
        };
    }

    public static Dictionary<string, int?> CreateDefaults() => KnownTypes.Values.ToDictionary(t => t, _ => (int?)DefaultLimit, StringComparer.OrdinalIgnoreCase);

    public static bool TryParse(string? input, out int? limit)
    {
        limit = null;
        if (string.IsNullOrWhiteSpace(input)) return true;
        if (!int.TryParse(input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed > 9999) return false;
        limit = parsed;
        return true;
    }

    public static Dictionary<string, int?> Load(string file)
    {
        string path = Path.GetFullPath(file);
        var limits = CreateDefaults();
        if (!File.Exists(path)) return limits;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Media limits must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!KnownTypes.TryGetValue(property.Name, out string? type)) continue;
            if (!seen.Add(type)) throw new InvalidDataException("Duplicate media limit: " + type);
            int? limit;
            if (property.Value.ValueKind == JsonValueKind.Null) limit = null;
            else if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int number) && number >= 0 && number <= 9999) limit = number;
            else throw new InvalidDataException("The limit for " + type + " must be null or an integer from 0 to 9999.");
            limits[type] = limit;
        }
        return limits;
    }

    public static void Save(string file, IReadOnlyDictionary<string, int?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var limits = CreateDefaults();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (!KnownTypes.TryGetValue(pair.Key, out string? type)) continue;
            if (!seen.Add(type)) throw new ArgumentException("Duplicate media limit: " + type, nameof(values));
            ValidateLimit(pair.Value);
            limits[type] = pair.Value;
        }
        string path = Path.GetFullPath(file);
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, limits, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static HashSet<string> NormalizeExisting(IEnumerable<string?> paths, string root)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string fullRoot = Path.GetFullPath(root);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                string full = Path.GetFullPath(path, fullRoot);
                if (File.Exists(full)) existing.Add(full);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }
        return existing;
    }

    public static bool HasRoom(int? limit, IEnumerable<string> existing)
    {
        ValidateLimit(limit);
        ArgumentNullException.ThrowIfNull(existing);
        return !limit.HasValue || existing.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(limit.Value).Count() < limit.Value;
    }

    public static bool IsVideoSibling(string candidate, string proposedBasePath)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(proposedBasePath)) return false;
        try
        {
            string fullCandidate = Path.GetFullPath(candidate);
            string fullBase = Path.GetFullPath(proposedBasePath);
            if (!VideoExtensions.Contains(Path.GetExtension(fullCandidate)) || !string.Equals(Path.GetDirectoryName(fullCandidate), Path.GetDirectoryName(fullBase), StringComparison.OrdinalIgnoreCase)) return false;
            // LaunchBox GetNextVideoFilePath supplies a final numeric image index.
            // Remove only that index, preserving numeric endings in the actual game title.
            string stem = Regex.Replace(Path.GetFileNameWithoutExtension(fullBase), @"-\d+$", "", RegexOptions.CultureInvariant);
            if (stem.Length == 0) return false;
            string candidateStem = Path.GetFileNameWithoutExtension(fullCandidate);
            // Older importer versions appended another numeric segment when a supplied
            // destination was occupied, so those files also consume this game's quota.
            return Regex.IsMatch(candidateStem, "^" + Regex.Escape(stem) + @"(?:-\d+)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    private static string? CanonicalType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        string trimmed = type.Trim();
        return KnownTypes.TryGetValue(trimmed, out string? known) ? known : trimmed;
    }

    private static void ValidateLimit(int? limit)
    {
        if (limit.HasValue && (limit.Value < 0 || limit.Value > 9999)) throw new ArgumentOutOfRangeException(nameof(limit), "Media limits must be null or an integer from 0 to 9999.");
    }
}
