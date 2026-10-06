using System;
using System.Collections.Generic;

namespace SteamMetadataImporter;

public sealed class SteamDetails
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Developer { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Genres { get; set; } = "";
    public string PlayMode { get; set; } = "";
    public string Series { get; set; } = "";
    public DateTime? ReleaseDate { get; set; }
    public bool ComingSoon { get; set; }
    public int? MaxPlayers { get; set; }
    public List<SteamAsset> Assets { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string StoreUrl => $"https://store.steampowered.com/app/{AppId}/";
}

public sealed class SteamAsset
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    // A LaunchBox image type; null denotes a video. Additional variants use Steam Banner/Steam Poster.
    public string? ImageType { get; set; }
    public bool IsVideo { get; set; }
    public bool Highlight { get; set; }
    public List<string> Sources { get; set; } = new();
}

public sealed class ImportOptions
{
    public bool Metadata { get; set; } = true;
    public bool Artwork { get; set; } = true;
    public bool Videos { get; set; } = true;
    public bool ThemeVideo { get; set; } = true;
    public Dictionary<string, int?> MediaTypeLimits { get; set; } = MediaLimits.CreateDefaults();
    public int? GetMediaLimit(string mediaType) => MediaTypeLimits.TryGetValue(mediaType, out int? value) ? value : MediaLimits.DefaultLimit;
}

public sealed class DownloadResult
{
    public bool Success { get; set; }
    public string? FilePath { get; set; }
    public string? Source { get; set; }
    public string? Error { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class AssetRecord
{
    public string Key { get; set; } = "";
    public string? MediaType { get; set; }
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class GameImportManifest
{
    public string GameId { get; set; } = "";
    public int AppId { get; set; }
    public string SteamName { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public List<AssetRecord> Assets { get; set; } = new();
}
