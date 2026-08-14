using System.Text.Json;

namespace ndnx;

/// <summary>
/// Picks a RID-specific tool package the way the SDK's
/// <c>GetBestMatchingRid</c> does: NuGet <c>RuntimeGraph.ExpandRuntime</c>
/// against the portable RID graph, then <c>any</c>.
/// Distro RIDs such as <c>ubuntu.24.04-x64</c> are not in that graph, so they
/// also expand the portable family RID (<c>linux-x64</c>, <c>win-x64</c>, …).
/// </summary>
public static class RidPackageResolver
{
    static readonly Lazy<IReadOnlyDictionary<string, string[]>> Graph = new(LoadGraph);

    public static string? Resolve(string hostRid, IReadOnlyDictionary<string, string> declaredPackages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostRid);
        ArgumentNullException.ThrowIfNull(declaredPackages);

        foreach (var candidate in Expand(hostRid))
        {
            foreach (var pair in declaredPackages)
            {
                if (string.Equals(pair.Key, candidate, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
        }

        return null;
    }

    public static IEnumerable<string> Expand(string hostRid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostRid);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rid in Walk(hostRid, seen))
            yield return rid;

        if (!Graph.Value.ContainsKey(hostRid) && TryPortableRid(hostRid, out var portable))
        {
            foreach (var rid in Walk(portable, seen))
                yield return rid;
        }

        if (seen.Add("any"))
            yield return "any";
    }

    static IEnumerable<string> Walk(string start, HashSet<string> seen)
    {
        if (!seen.Add(start))
            yield break;

        yield return start;

        var queue = new Queue<string>();
        queue.Enqueue(start);
        var graph = Graph.Value;
        while (queue.Count > 0)
        {
            if (!graph.TryGetValue(queue.Dequeue(), out var imported))
                continue;

            foreach (var next in imported)
            {
                if (!seen.Add(next))
                    continue;

                yield return next;
                queue.Enqueue(next);
            }
        }
    }

    internal static bool TryPortableRid(string hostRid, out string portable)
    {
        portable = "";
        var dash = hostRid.LastIndexOf('-');
        if (dash <= 0)
            return false;

        var arch = hostRid[(dash + 1)..];
        if (arch is not ("x64" or "x86" or "arm64" or "arm" or "armel" or "armv6"
            or "ppc64le" or "riscv64" or "s390x" or "loongarch64" or "mips64"))
        {
            return false;
        }

        var prefix = hostRid[..dash];
        var token = prefix;
        var dot = token.IndexOf('.');
        if (dot >= 0)
            token = token[..dot];
        var tokenDash = token.IndexOf('-');
        if (tokenDash >= 0)
            token = token[..tokenDash];

        var family = PortableFamily(token, prefix);
        if (family is null)
            return false;

        portable = family + "-" + arch;
        return !portable.Equals(hostRid, StringComparison.OrdinalIgnoreCase);
    }

    static string? PortableFamily(string token, string prefix)
    {
        if (token.StartsWith("win", StringComparison.OrdinalIgnoreCase))
            return "win";
        if (token.Equals("osx", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("macos", StringComparison.OrdinalIgnoreCase))
        {
            return "osx";
        }

        if (token.Equals("linux", StringComparison.OrdinalIgnoreCase))
        {
            return prefix.StartsWith("linux-musl", StringComparison.OrdinalIgnoreCase)
                ? "linux-musl"
                : "linux";
        }

        if (token.Equals("alpine", StringComparison.OrdinalIgnoreCase))
            return "linux-musl";

        return token.ToLowerInvariant() switch
        {
            "ubuntu" or "debian" or "fedora" or "rhel" or "centos" or "opensuse" or "sles"
                or "tizen" or "linuxmint" or "ol" or "amzn" or "rocky" or "alma"
                or "pop" or "elementary" or "kali" or "arch" or "gentoo" or "void"
                or "nixos" or "manjaro" => "linux",
            _ => null,
        };
    }

    static IReadOnlyDictionary<string, string[]> LoadGraph()
    {
        using var stream = typeof(RidPackageResolver).Assembly
            .GetManifestResourceStream("PortableRuntimeIdentifierGraph.json")
            ?? throw new InvalidOperationException("Missing embedded PortableRuntimeIdentifierGraph.json.");

        var file = JsonSerializer.Deserialize(stream, NuGetJsonContext.Default.RuntimeGraphFile)
            ?? throw new InvalidOperationException("Invalid PortableRuntimeIdentifierGraph.json.");

        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (file.Runtimes is null)
            return map;

        foreach (var (rid, node) in file.Runtimes)
            map[rid] = node.Import ?? [];

        return map;
    }
}
