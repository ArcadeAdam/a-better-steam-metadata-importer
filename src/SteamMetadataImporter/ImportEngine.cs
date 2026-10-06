using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

[assembly: InternalsVisibleTo("SteamMetadataImporter.Tests")]

namespace SteamMetadataImporter;

public sealed class ImportEngine
{
    private readonly string root;
    private readonly string dataDirectory;
    private readonly Dispatcher dispatcher;
    private readonly Func<MediaDownloader> downloaderFactory;
    private readonly Action persistChanges;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };

    public ImportEngine(string launchBoxRoot, Dispatcher uiDispatcher)
        : this(launchBoxRoot, uiDispatcher, () => new MediaDownloader(launchBoxRoot), () =>
        {
            PluginHelper.DataManager.Save(true);
            PluginHelper.LaunchBoxMainViewModel?.RefreshData();
        }) { }

    internal ImportEngine(string launchBoxRoot, Dispatcher uiDispatcher, Func<MediaDownloader> downloaderFactory, Action persistChanges)
    {
        root = Path.GetFullPath(launchBoxRoot);
        dataDirectory = Path.Combine(root, "Plugins", "SteamMetadataImporter", "Data");
        dispatcher = uiDispatcher;
        this.downloaderFactory = downloaderFactory;
        this.persistChanges = persistChanges;
    }

    public async Task<string> ImportAsync(IGame game, SteamDetails details, ImportOptions options, IProgress<string> progress, CancellationToken token)
    {
        string id = SafePart(game.Id);
        Directory.CreateDirectory(dataDirectory);
        string manifestPath = Path.Combine(dataDirectory, "Manifests", id + ".json");
        var manifest = ReadManifest(manifestPath, game.Id, details.AppId);
        manifest.SteamName = details.Name;
        var messages = new List<string>();
        string staging = Path.Combine(dataDirectory, "Staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        int added = 0, reused = 0, failed = 0, fields = 0, limited = 0;
        string? firstTrailer = null;
        var allAssets = details.Assets.Where(a => a.IsVideo ? options.Videos || options.ThemeVideo : options.Artwork).ToList();
        allAssets = allAssets.OrderBy(a => a.IsVideo).ThenByDescending(a => a.Highlight).ToList();
        // Snapshot before any mutation. This is user-readable and includes the Steam source identity.
        var before = await dispatcher.InvokeAsync(() => Snapshot(game));
        bool preserveVideo = await dispatcher.InvokeAsync(() => HasExistingVideo(game, false, root));
        bool preserveTheme = await dispatcher.InvokeAsync(() => HasExistingVideo(game, true, root));
        string backup = Path.Combine(dataDirectory, "Backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + id + ".json");
        WriteJson(backup, new { GameId = game.Id, SteamAppId = details.AppId, SteamName = details.Name, Values = before });
        try
        {
            using var downloader = downloaderFactory();
            int position = 0;
            foreach (var asset in allAssets)
            {
                token.ThrowIfCancellationRequested();
                position++;
                string mediaType = MediaLimits.ForAsset(asset);
                int? limit = options.GetMediaLimit(mediaType);
                if (limit < 0 || limit > 9999) throw new InvalidOperationException("Media limits must be from 0 to 9999, or unlimited.");
                if (limit == 0) { limited++; continue; }
                progress.Report($"{game.Title}: {position}/{allAssets.Count} — {asset.Label}");
                try
                {
                    AssetRecord? prior = null;
                    foreach (var candidate in manifest.Assets.Where(a => a.Key == asset.Key).Reverse())
                    {
                        if (File.Exists(candidate.Path) && HashEquals(await HashAsync(candidate.Path, token), candidate.Sha256))
                        { prior = candidate; break; }
                    }
                    if (prior != null)
                    {
                        prior.MediaType = mediaType;
                        reused++;
                        if (asset.IsVideo && firstTrailer == null) firstTrailer = prior.Path;
                        continue;
                    }
                    var knownPaths = await dispatcher.InvokeAsync(() => GetKnownMediaPaths(game, mediaType, manifest, details, root));
                    if (!MediaLimits.HasRoom(limit, knownPaths))
                    {
                        limited++;
                        progress.Report($"Limit reached: {mediaType} ({knownPaths.Count}/{limit}); skipping {asset.Label}.");
                        continue;
                    }
                    DownloadResult result = await downloader.DownloadAsync(asset, staging, progress, token);
                    if (!result.Success || result.FilePath == null)
                    {
                        failed++;
                        messages.Add(asset.Label + ": " + (result.Error ?? "No downloadable source"));
                        progress.Report("Unavailable: " + messages[^1]);
                        continue;
                    }
                    string hash = result.Sha256 ?? await HashAsync(result.FilePath, token);
                    string? existing = null;
                    // Re-read after downloading so user-added files and stale LaunchBox caches
                    // cannot let another file slip past the per-game limit.
                    knownPaths = await dispatcher.InvokeAsync(() => GetKnownMediaPaths(game, mediaType, manifest, details, root));
                    foreach (string full in knownPaths)
                    {
                        if (new FileInfo(full).Length == new FileInfo(result.FilePath).Length && HashEquals(await HashAsync(full, token), hash))
                        { existing = full; break; }
                    }
                    string destination;
                    if (existing != null) { destination = existing; reused++; }
                    else
                    {
                        if (!MediaLimits.HasRoom(limit, knownPaths))
                        {
                            limited++;
                            progress.Report($"Limit reached: {mediaType}; keeping the existing files.");
                            continue;
                        }
                        string extension = Path.GetExtension(result.FilePath);
                        string proposed = await dispatcher.InvokeAsync(() => asset.IsVideo
                            ? game.GetNextVideoFilePath("", extension)
                            : game.GetNextAvailableImageFilePath(extension, asset.ImageType!, string.IsNullOrWhiteSpace(game.Region) ? "" : game.Region));
                        if (string.IsNullOrWhiteSpace(proposed)) throw new IOException("LaunchBox did not provide a media destination.");
                        destination = FullPath(proposed);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        // API image names honor platform folders, title sanitization, and configured regions.
                        // Videos may return a base path already in use; take a sibling without ever replacing it.
                        string baseName = Path.GetFileNameWithoutExtension(destination);
                        string folder = Path.GetDirectoryName(destination)!;
                        int suffix = 1;
                        while (File.Exists(destination)) destination = Path.Combine(folder, baseName + "-" + (++suffix).ToString("00") + extension);
                        string pending = destination + "." + Guid.NewGuid().ToString("N") + ".importing";
                        try
                        {
                            using (var input = File.OpenRead(result.FilePath))
                            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                await input.CopyToAsync(output, token);
                            if (!HashEquals(await HashAsync(pending, token), hash)) throw new IOException("The saved file failed its integrity check.");
                            token.ThrowIfCancellationRequested();
                            File.Move(pending, destination, false);
                        }
                        finally { if (File.Exists(pending)) File.Delete(pending); }
                        added++;
                    }
                    // Retain other paths for this key: a user-edited older file still
                    // occupies capacity even before LaunchBox refreshes its image cache.
                    manifest.Assets.RemoveAll(x => x.Key == asset.Key && string.Equals(x.Path, destination, StringComparison.OrdinalIgnoreCase));
                    manifest.Assets.Add(new AssetRecord { Key = asset.Key, MediaType = mediaType, Path = destination, Sha256 = hash, Source = result.Source ?? "" });
                    manifest.UpdatedUtc = DateTime.UtcNow;
                    WriteJson(manifestPath, manifest);
                    if (asset.IsVideo && firstTrailer == null) firstTrailer = destination;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    failed++;
                    messages.Add(asset.Label + ": " + error.Message);
                    progress.Report("Skipped: " + messages[^1]);
                }
            }
            token.ThrowIfCancellationRequested();
            await dispatcher.InvokeAsync(() =>
            {
                if (options.Metadata) fields = MetadataPlan.ApplyMissing(game, MetadataPlan.Build(game, details)).Count;
                if (firstTrailer != null)
                {
                    string relative = Path.GetRelativePath(root, firstTrailer);
                    var ownedPaths = manifest.Assets.Where(a => a.Key.StartsWith("trailer-", StringComparison.Ordinal)).Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (options.Videos && !preserveVideo && !HasExistingVideo(game, false, root, ownedPaths)) { game.VideoPath = relative; fields++; }
                    if (options.ThemeVideo && !preserveTheme && !HasExistingVideo(game, true, root, ownedPaths)) { game.ThemeVideoPath = relative; fields++; }
                }
                if (fields > 0) game.DateModified = DateTime.Now;
                persistChanges();
            });
            string summary = $"{fields} fields filled; {added} files added; {reused} reused; {limited} skipped by limits; {failed} unavailable.";
            WriteJson(Path.Combine(dataDirectory, "Logs", Path.GetFileName(backup)), new { game.Id, details.AppId, Summary = summary, Warnings = details.Warnings, Errors = messages, Backup = backup });
            return summary;
        }
        finally
        {
            manifest.UpdatedUtc = DateTime.UtcNow;
            WriteJson(manifestPath, manifest);
            // Delete only our unique staging directory, never a media folder or a user-selected path.
            if (Directory.Exists(staging)) try { Directory.Delete(staging, true); } catch (IOException) { }
        }
    }

    public static string FindLaunchBoxRoot()
    {
        foreach (var start in new[] { Path.GetDirectoryName(typeof(ImportEngine).Assembly.Location), AppContext.BaseDirectory })
        {
            var cursor = start == null ? null : new DirectoryInfo(start);
            for (int i = 0; i < 5 && cursor != null; i++, cursor = cursor.Parent)
                if (Directory.Exists(Path.Combine(cursor.FullName, "Data", "Platforms")) && Directory.Exists(Path.Combine(cursor.FullName, "Core"))) return cursor.FullName;
        }
        throw new InvalidOperationException("The plugin could not locate its LaunchBox installation.");
    }

    public static HashSet<string> GetKnownMediaPaths(IGame game, string mediaType, GameImportManifest manifest, SteamDetails details, string launchBoxRoot)
    {
        var paths = new List<string?>();
        paths.AddRange(manifest.Assets.Where(a => string.Equals(MediaLimits.ForRecord(a, details), mediaType, StringComparison.OrdinalIgnoreCase)).Select(a => a.Path));
        if (!string.Equals(mediaType, "Video", StringComparison.OrdinalIgnoreCase))
            paths.AddRange(game.GetAllImagesWithDetails(mediaType).Select(i => i.FilePath));
        else
        {
            paths.AddRange(new[] { game.VideoPath, game.ThemeVideoPath, game.GetVideoPath(false), game.GetThemeVideoPath() });
            string proposed = game.GetNextVideoFilePath("", ".mp4");
            if (!string.IsNullOrWhiteSpace(proposed))
            {
                string full = Path.GetFullPath(proposed, launchBoxRoot);
                string folder = Path.GetDirectoryName(full)!;
                if (Directory.Exists(folder))
                    paths.AddRange(Directory.EnumerateFiles(folder).Where(p => MediaLimits.IsVideoSibling(p, full)));
            }
        }
        return MediaLimits.NormalizeExisting(paths, launchBoxRoot);
    }

    public static bool HasExistingVideo(IGame game, bool theme, string launchBoxRoot, ISet<string>? ignoreDiscoveredPaths = null)
    {
        // An explicit assignment is preserved even if its file is offline or temporarily unavailable.
        if (!string.IsNullOrWhiteSpace(theme ? game.ThemeVideoPath : game.VideoPath)) return true;
        string found = theme ? game.GetThemeVideoPath() : game.GetVideoPath(false);
        if (string.IsNullOrWhiteSpace(found)) return false;
        string full = Path.GetFullPath(found, launchBoxRoot);
        return File.Exists(full) && !(ignoreDiscoveredPaths?.Contains(full) ?? false);
    }

    private GameImportManifest ReadManifest(string path, string gameId, int appId)
    {
        if (File.Exists(path))
        {
            var saved = JsonSerializer.Deserialize<GameImportManifest>(File.ReadAllText(path));
            if (saved?.GameId == gameId && saved.AppId == appId) return saved;
        }
        return new GameImportManifest { GameId = gameId, AppId = appId };
    }

    private static Dictionary<string, object?> Snapshot(IGame game)
    {
        var names = MetadataPlan.Fields.Concat(new[] { "ApplicationPath", "CommandLine", "VideoPath", "ThemeVideoPath", "LaunchBoxDbId", "DateModified" });
        return names.ToDictionary(n => n, n => typeof(IGame).GetProperty(n)!.GetValue(game));
    }
    private void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, json));
        File.Move(temp, path, true);
    }
    private string FullPath(string path) => Path.GetFullPath(path, root);
    private static bool HashEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string SafePart(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).Substring(0, 24);
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }
}
