using System.Text.RegularExpressions;
using ndnx;

namespace Tests;

public class PublishWorkflowTests
{
    static readonly (string Os, string Rid)[] ExpectedMatrix =
    [
        ("ubuntu-latest", "linux-x64"),
        ("ubuntu-24.04-arm", "linux-arm64"),
        ("windows-latest", "win-x64"),
        ("windows-11-arm", "win-arm64"),
        ("macos-15-intel", "osx-x64"),
        ("macos-latest", "osx-arm64"),
    ];

    [Fact]
    public void Release_workflow_builds_the_six_rid_matrix_and_attaches_archives()
    {
        var yml = File.ReadAllText(FindPublishWorkflow());

        Assert.Contains("release:", yml);
        Assert.Contains("prereleased", yml);
        Assert.Contains("released", yml);
        Assert.Contains("name: native-aot-${{ matrix.rid }}", yml);
        Assert.Contains("dotnet publish", yml);
        Assert.Contains("PublishAot", yml);
        Assert.Contains("upload-artifact", yml);
        Assert.Contains("if-no-files-found: error", yml);
        Assert.Contains("gh release upload", yml);
        Assert.Contains("needs: native-aot", yml);
        Assert.Contains("src/nativepack", yml);
        Assert.DoesNotContain("dotnet nuget push", yml);
        Assert.DoesNotContain("sleet push", yml);

        foreach (var (os, rid) in ExpectedMatrix)
        {
            Assert.Contains($"os: {os}", yml);
            Assert.Contains($"rid: {rid}", yml);
        }
    }

    [Fact]
    public void Nativepack_sources_are_not_gitignored()
    {
        var repo = FindRepoRoot();
        var packer = Path.Combine(repo, "src", "nativepack", "NativePacker.cs");
        var project = Path.Combine(repo, "src", "nativepack", "nativepack.csproj");
        Assert.True(File.Exists(packer), packer);
        Assert.True(File.Exists(project), project);

        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("check-ignore");
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("src/nativepack/NativePacker.cs");
        start.ArgumentList.Add("src/nativepack/nativepack.csproj");
        start.ArgumentList.Add("src/nativepack/Program.cs");

        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("failed to start git");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode != 0, $"git check-ignore should not match nativepack, but matched:{Environment.NewLine}{stdout}");
        Assert.True(string.IsNullOrWhiteSpace(stdout), stdout);
    }

    [Fact]
    public void Winget_job_publishes_ndnx_zips_after_release_assets()
    {
        var yml = File.ReadAllText(FindPublishWorkflow());
        var job = ExtractJob(yml, "winget");

        Assert.Contains("vedantmgoyal9/winget-releaser", job);
        Assert.Contains("identifier: Devlooped.ndnx", job);
        Assert.DoesNotMatch(new Regex(@"token:\s*['""]?(gh[pousr]_|github_pat_)"), job);

        var token = Regex.Match(job, @"token:\s*\$\{\{\s*secrets\.(?<name>[A-Z0-9_]+)\s*\}\}");
        Assert.True(token.Success, "winget token must come from a repository secret");
        Assert.False(string.IsNullOrWhiteSpace(token.Groups["name"].Value));

        Assert.Matches(new Regex(@"needs:\s*publish\b"), job);
        Assert.Contains("github.event.action == 'released'", job);
        Assert.DoesNotContain("prereleased", job);

        var pattern = ExtractInstallersRegex(job);
        Assert.False(string.Equals(pattern, ".(exe|msi|msix|appx)(bundle){0,1}$", StringComparison.Ordinal),
            "installers-regex must not be the action default exe/msi/msix/appx pattern");

        var regex = new Regex(pattern);
        Assert.True(regex.IsMatch(NativePacker.ArchiveFileName("win-x64", "1.2.3")), pattern);
        Assert.True(regex.IsMatch(NativePacker.ArchiveFileName("win-arm64", "1.2.3")), pattern);
        Assert.True(regex.IsMatch("ndnx-1.2.3-win-x64.zip"), pattern);
        Assert.True(regex.IsMatch("ndnx-1.2.3-win-arm64.zip"), pattern);
        Assert.False(regex.IsMatch("ndnx-1.2.3-win-x64.exe"), pattern);
        Assert.False(regex.IsMatch("ndnx-1.2.3-win-x64.msi"), pattern);
        Assert.False(regex.IsMatch(NativePacker.ArchiveFileName("linux-x64", "1.2.3")), pattern);
        Assert.False(regex.IsMatch(NativePacker.ArchiveFileName("osx-arm64", "1.2.3")), pattern);
    }

    static string ExtractJob(string yml, string jobId)
    {
        // Jobs are indented 2 spaces; their body is 4+ spaces. Capture until the next 2-space key or EOF.
        var match = Regex.Match(
            yml,
            $@"(?ms)^  {Regex.Escape(jobId)}:\r?\n(?<body>(?:    .*(?:\r?\n|$))*)");
        Assert.True(match.Success, $"Could not find job '{jobId}' in publish.yml");
        return match.Value;
    }

    static string ExtractInstallersRegex(string job)
    {
        var match = Regex.Match(job, @"installers-regex:\s*(?:'([^']+)'|""([^""]+)""|(\S+))");
        Assert.True(match.Success, "installers-regex is required; the action default matches .exe/.msi, not ndnx zips");
        return match.Groups[1].Success ? match.Groups[1].Value
            : match.Groups[2].Success ? match.Groups[2].Value
            : match.Groups[3].Value;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ndnx.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root.");
    }

    static string FindPublishWorkflow()
        => Path.Combine(FindRepoRoot(), ".github", "workflows", "publish.yml");
}
