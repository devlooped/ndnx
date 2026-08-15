namespace ndnx;

/// <summary>
/// Picks <c>DotnetToolSettings.xml</c> inside an extracted tool package.
/// One file wins. Several files: newest TFM the muxer can load, then host RID over <c>any</c>.
/// </summary>
public static class ToolSettingsLocator
{
    public static string? Choose(IReadOnlyList<string> settingsFiles, string hostRid, string? muxerPath)
    {
        if (settingsFiles.Count == 0)
            return null;
        if (settingsFiles.Count == 1)
            return settingsFiles[0];

        var hostTfm = DotnetMuxer.HighestNetCoreApp(muxerPath);
        var candidates = new List<(string Path, FrameworkMoniker? Tfm, string? Rid)>(settingsFiles.Count);
        foreach (var file in settingsFiles)
        {
            TryParseToolsPath(file, out var tfm, out var rid);
            candidates.Add((file, tfm, rid));
        }

        var compatible = new List<(string Path, FrameworkMoniker? Tfm, string? Rid)>();
        foreach (var candidate in candidates)
        {
            if (candidate.Tfm is null || hostTfm is { } host && candidate.Tfm <= host)
                compatible.Add(candidate);
        }

        if (compatible.Count == 0)
        {
            var required = candidates
                .Select(c => c.Tfm)
                .OfType<FrameworkMoniker>()
                .DefaultIfEmpty(new FrameworkMoniker(0, 0))
                .Min();

            if (muxerPath is null || !File.Exists(muxerPath))
            {
                throw new InvalidOperationException(
                    $"This tool requires {required.Display}. Could not find '{DotnetMuxer.FileName}' on PATH or DOTNET_ROOT.");
            }

            throw new InvalidOperationException(
                FrameworkDependentGuard.FormatIncompatible(
                    required,
                    muxerPath,
                    DotnetMuxer.HighestNetCoreAppVersion(muxerPath)));
        }

        return compatible
            .OrderByDescending(c => c.Tfm ?? new FrameworkMoniker(0, 0))
            .ThenByDescending(c => RidScore(c.Rid, hostRid))
            .First().Path;
    }

    public static bool TryParseToolsPath(string settingsPath, out FrameworkMoniker? tfm, out string? rid)
    {
        tfm = null;
        rid = null;
        var normalized = settingsPath.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 2; i++)
        {
            if (!parts[i].Equals("tools", StringComparison.OrdinalIgnoreCase))
                continue;

            var tfmFolder = parts[i + 1];
            rid = parts[i + 2];
            FrameworkMoniker.TryParseFolder(tfmFolder, out tfm);
            return true;
        }

        return false;
    }

    static int RidScore(string? rid, string hostRid)
    {
        if (string.IsNullOrEmpty(rid))
            return 0;
        if (rid.Equals(hostRid, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (rid.Equals("any", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 0;
    }
}
