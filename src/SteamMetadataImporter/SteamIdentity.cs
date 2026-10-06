using System;
using System.IO;
using System.Text.RegularExpressions;

namespace SteamMetadataImporter;

public static class SteamIdentity
{
    public static int? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim().Trim('"');
        if (Regex.IsMatch(input, @"^[1-9]\d{0,9}$") && int.TryParse(input, out int numeric)) return numeric;
        var command = Regex.Match(input, @"(?:^|\s)-applaunch\s+([1-9]\d*)\b", RegexOptions.IgnoreCase);
        if (command.Success && int.TryParse(command.Groups[1].Value, out int cmd)) return cmd;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        string? id = null;
        if (uri.Scheme.Equals("steam", StringComparison.OrdinalIgnoreCase) &&
            (uri.Host.Equals("rungameid", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("run", StringComparison.OrdinalIgnoreCase)))
            id = uri.AbsolutePath.Trim('/').Split('/')[0];
        else if ((uri.Scheme == "https" || uri.Scheme == "http") &&
            (uri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("steamdb.info", StringComparison.OrdinalIgnoreCase)))
        {
            var match = Regex.Match(uri.AbsolutePath, @"^/app/([1-9]\d*)(?:/|$)", RegexOptions.IgnoreCase);
            if (match.Success) id = match.Groups[1].Value;
        }
        return int.TryParse(id, out int result) && result > 0 ? result : null;
    }

    public static int? Resolve(string? applicationPath, string? commandLine, string launchBoxRoot)
    {
        var direct = Parse(applicationPath) ?? Parse(commandLine);
        if (direct.HasValue) return direct;
        if (string.IsNullOrWhiteSpace(applicationPath)) return null;
        try
        {
            string path = Path.GetFullPath(applicationPath.Trim('"'), launchBoxRoot);
            if (!path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || new FileInfo(path).Length > 65536) return null;
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return Parse(trimmed.Substring(4));
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) { }
        return null;
    }
}
