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
    public void Ci_release_workflow_publishes_a_rolling_prerelease()
    {
        var yml = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "ci-release.yml"));

        Assert.Contains("workflow_run", yml);
        Assert.Contains("workflows: [build]", yml);
        Assert.Contains("branches: [main]", yml);
        Assert.Contains("--prerelease", yml);
        Assert.Contains("--latest=false", yml);
        Assert.Contains("release create ci", yml);
        Assert.Contains("select(.tag_name == \"ci\")", yml);
        Assert.Contains("git/refs/tags/ci", yml);
        Assert.Contains("--target \"${NDNX_SHA}\"", yml);
        Assert.Contains("until gh release create ci", yml);
        Assert.Contains("cancel-in-progress: false", yml);
        Assert.DoesNotContain("gh release view ci >/dev/null", yml);
        Assert.Contains("name: native-aot-${{ matrix.rid }}", yml);
        Assert.Contains("dotnet publish", yml);
        Assert.Contains("PublishAot", yml);
        Assert.Contains("src/nativepack", yml);
        Assert.DoesNotContain("dotnet nuget push", yml);
        Assert.DoesNotContain("sleet push", yml);
        Assert.DoesNotContain("osx-", yml);
        Assert.DoesNotContain("macos-", yml);
        Assert.DoesNotContain("arm64", yml);

        foreach (var (os, rid) in ExpectedMatrix.Where(entry => entry.Rid is "linux-x64" or "win-x64"))
        {
            Assert.Contains($"os: {os}", yml);
            Assert.Contains($"rid: {rid}", yml);
        }
    }

    [Fact]
    public void Release_workflow_builds_the_six_rid_matrix_and_attaches_archives()
    {
        var yml = File.ReadAllText(FindPublishWorkflow());

        Assert.Contains("release:", yml);
        Assert.Contains("prereleased", yml);
        Assert.Contains("released", yml);
        Assert.Contains("github.event.release.tag_name != 'ci'", yml);
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
