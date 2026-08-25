using System.Diagnostics;
using ndnx;

namespace Tests;

public class InstallScriptTests
{
    [Fact]
    public void Install_scripts_are_in_the_repo_root()
    {
        var root = FindRepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "install.sh")), "install.sh");
        Assert.True(File.Exists(Path.Combine(root, "install.ps1")), "install.ps1");
    }

    [Fact]
    public void Scripts_cover_the_six_release_rids()
    {
        var root = FindRepoRoot();
        var sh = File.ReadAllText(Path.Combine(root, "install.sh"));
        var ps = File.ReadAllText(Path.Combine(root, "install.ps1"));

        Assert.Contains("linux-${arch}", sh);
        Assert.Contains("osx-${arch}", sh);
        Assert.Contains("win-${arch}", sh);
        Assert.Contains("win-$archName", ps);
        Assert.Contains("osx-$archName", ps);
        Assert.Contains("linux-$archName", ps);

        foreach (var rid in new[] { "linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64" })
        {
            var archive = NativePacker.ArchiveFileName(rid, "1.2.3");
            Assert.Contains(NativePacker.IsWindowsRid(rid) ? "zip" : "tar.gz", archive);
        }
    }

    [Fact]
    public void Powershell_install_script_installs_from_a_local_archive()
    {
        var root = FindRepoRoot();
        var script = Path.Combine(root, "install.ps1");
        using var dir = new TempDir();

        var payload = "installed-by-script"u8.ToArray();
        File.WriteAllBytes(Path.Combine(dir.Publish, "ndnx.exe"), payload);
        var packed = NativePacker.Pack(dir.Publish, "win-x64", dir.Output, "1.0.0");

        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "pwsh" : "pwsh",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // pwsh may not be on PATH; fall back to Windows PowerShell.
        if (OperatingSystem.IsWindows() && GetFullPath("pwsh") is null)
            start.FileName = "powershell";

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--archive");
        start.ArgumentList.Add(packed.ArchivePath);
        start.ArgumentList.Add("--prefix");
        start.ArgumentList.Add(dir.Prefix);
        start.ArgumentList.Add("--rid");
        start.ArgumentList.Add("win-x64");
        start.ArgumentList.Add("--skip-path");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start PowerShell");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"install.ps1 failed ({process.ExitCode}).{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");

        var dest = Path.Combine(dir.Prefix, "ndnx.exe");
        Assert.True(File.Exists(dest), stdout);
        Assert.Equal(payload, File.ReadAllBytes(dest));
        Assert.Contains("installed", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Powershell_installer_persists_user_path_and_broadcasts_environment_change()
    {
        var ps = File.ReadAllText(Path.Combine(FindRepoRoot(), "install.ps1"));
        Assert.Contains("SetEnvironmentVariable('Path', $updated, 'User')", ps);
        Assert.Contains("SendMessageTimeout", ps);
        Assert.Contains("'Environment'", ps);
        Assert.Contains("0x1a", ps);
        Assert.Contains("Add-NdnxToUserPath", ps);
    }

    [Fact]
    public void Shell_installer_writes_a_guarded_path_block_into_zshrc()
    {
        var bash = FindBash();
        Assert.True(bash is not null, "bash is required to verify install.sh PATH persist");

        var root = FindRepoRoot();
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Publish, "ndnx"), "unix-ndnx"u8.ToArray());
        var packed = NativePacker.Pack(dir.Publish, "linux-x64", dir.Output, "1.0.0");

        RunInstallSh(bash, root, dir, packed.ArchivePath, skipPath: false);
        RunInstallSh(bash, root, dir, packed.ArchivePath, skipPath: false);

        var zshrc = File.ReadAllText(Path.Combine(dir.Home, ".zshrc"));
        Assert.Contains("# >>> ndnx path >>>", zshrc);
        Assert.Contains("# <<< ndnx path <<<", zshrc);
        Assert.Contains(ToBashPath(bash, dir.Prefix), zshrc);
        Assert.Equal(1, CountOccurrences(zshrc, "# >>> ndnx path >>>"));
        Assert.True(File.Exists(Path.Combine(dir.Prefix, "ndnx")));
    }

    [Fact]
    public void Shell_installer_skip_path_does_not_write_rc()
    {
        var bash = FindBash();
        Assert.True(bash is not null, "bash is required to verify install.sh PATH persist");

        var root = FindRepoRoot();
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Publish, "ndnx"), "unix-ndnx"u8.ToArray());
        var packed = NativePacker.Pack(dir.Publish, "linux-x64", dir.Output, "1.0.0");

        RunInstallSh(bash, root, dir, packed.ArchivePath, skipPath: true);

        Assert.False(File.Exists(Path.Combine(dir.Home, ".zshrc")));
        Assert.True(File.Exists(Path.Combine(dir.Prefix, "ndnx")));
    }

    [Fact]
    public void Install_scripts_resolve_ci_channel_without_a_v_prefix()
    {
        var root = FindRepoRoot();
        var sh = File.ReadAllText(Path.Combine(root, "install.sh"));
        var ps = File.ReadAllText(Path.Combine(root, "install.ps1"));

        Assert.Contains("ci)", sh);
        Assert.Contains("tag=ci", sh);
        Assert.Contains("version=ci", sh);
        Assert.Contains("$Version -eq 'ci'", ps);
        Assert.Contains("$tag = 'ci'", ps);
        Assert.Contains("$resolved = 'ci'", ps);
    }

    [Fact]
    public void Workflow_does_not_publish_nuget_or_sleet()
    {
        var yml = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "publish.yml"));
        var build = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "build.yml"));
        var ci = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "ci-release.yml"));
        Assert.DoesNotContain("dotnet nuget push", yml);
        Assert.DoesNotContain("sleet push", yml);
        Assert.DoesNotContain("NUGET_API_KEY", yml);
        Assert.DoesNotContain("SLEET_CONNECTION", yml);
        Assert.DoesNotContain("sleet push", build);
        Assert.DoesNotContain("sleet push", ci);
        Assert.Contains("install.sh", yml);
        Assert.Contains("install.ps1", yml);
    }

    static void RunInstallSh(string bash, string repoRoot, TempDir dir, string archive, bool skipPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = bash,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ToBashPath(bash, Path.Combine(repoRoot, "install.sh")));
        start.Environment["HOME"] = ToBashPath(bash, dir.Home);
        start.Environment["SHELL"] = "/bin/zsh";
        start.Environment["NDNX_ARCHIVE"] = ToBashPath(bash, archive);
        start.Environment["NDNX_PREFIX"] = ToBashPath(bash, dir.Prefix);
        start.Environment["NDNX_RID"] = "linux-x64";
        start.Environment["NDNX_SKIP_PATH"] = skipPath ? "1" : "0";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start bash");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"install.sh failed ({process.ExitCode}).{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        if (!skipPath)
            Assert.Contains("PATH configured", stdout);
    }

    static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = 0; (i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0; i += value.Length)
            count++;
        return count;
    }

    static string? FindBash()
    {
        var gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (File.Exists(gitBash))
            return gitBash;
        return GetFullPath("bash");
    }

    static string ToBashPath(string bash, string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
            return full.Replace('\\', '/');

        var root = Path.GetPathRoot(full) ?? @"C:\";
        var drive = char.ToLowerInvariant(root[0]);
        var rest = full[root.Length..].Replace('\\', '/');
        var gitBash = bash.Contains("Git", StringComparison.OrdinalIgnoreCase);
        return gitBash ? $"/{drive}/{rest}" : $"/mnt/{drive}/{rest}";
    }

    static string? GetFullPath(string name)
    {
        if (File.Exists(name))
            return Path.GetFullPath(name);

        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
            return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
                return candidate + ".exe";
        }

        return null;
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

    sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ndnx-install-tests", Guid.NewGuid().ToString("n"));
        public string Publish => Path.Combine(Root, "publish");
        public string Output => Path.Combine(Root, "out");
        public string Prefix => Path.Combine(Root, "prefix");
        public string Home => Path.Combine(Root, "home");

        public TempDir()
        {
            Directory.CreateDirectory(Publish);
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(Prefix);
            Directory.CreateDirectory(Home);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
