using System.Diagnostics;
using System.IO.Compression;

namespace Tests;

/// <summary>
/// Packs a tiny framework-dependent tool and an executable-runner tool into a local folder feed.
/// </summary>
public sealed class HelloToolFeed : IDisposable
{
    public const string Phrase = "Hello from fixture!";
    public const string PackageId = "hello-tool";
    public const string ExePackageId = "hello-exe";
    public const string PackageVersion = "1.0.0";

    public string Root { get; }
    public string FeedDirectory { get; }

    public HelloToolFeed()
    {
        Root = Path.Combine(Path.GetTempPath(), "ndnx-hello-tool-feed");
        FeedDirectory = Path.Combine(Root, "feed");
        Directory.CreateDirectory(FeedDirectory);

        var stamp = Path.Combine(Root, "stamp.txt");
        if (File.Exists(stamp) &&
            File.Exists(Path.Combine(FeedDirectory, $"{PackageId}.{PackageVersion}.nupkg")) &&
            File.Exists(Path.Combine(FeedDirectory, $"{ExePackageId}.{PackageVersion}.nupkg")))
        {
            return;
        }

        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);

        Directory.CreateDirectory(FeedDirectory);
        var projectDir = Path.Combine(Root, "src");
        Directory.CreateDirectory(projectDir);
        WriteProject(projectDir);
        PackFrameworkDependent(projectDir);
        PublishAndPackExecutable(projectDir);
        File.WriteAllText(stamp, DateTime.UtcNow.ToString("O"));
    }

    public void Dispose()
    {
        // Keep the packed feed for reuse across test runs; isolated caches are per-test.
    }

    static void WriteProject(string projectDir)
    {
        File.WriteAllText(Path.Combine(projectDir, "hello-tool.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <PackAsTool>true</PackAsTool>
                <ToolCommandName>hello-tool</ToolCommandName>
                <PackageId>hello-tool</PackageId>
                <Version>1.0.0</Version>
                <Authors>ndnx</Authors>
                <Description>ndnx test fixture</Description>
                <PackageOutputPath>../feed</PackageOutputPath>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(projectDir, "Program.cs"),
            """
            int code = 0;
            if (args is [{ } first, ..] && int.TryParse(first, out var parsed))
                code = parsed;

            Console.WriteLine("Hello from fixture!");
            foreach (var arg in args)
                Console.WriteLine($"arg:{arg}");
            return code;
            """);
    }

    void PackFrameworkDependent(string projectDir)
    {
        RunDotnet(["pack", "-c", "Release", "--nologo"], projectDir);
    }

    void PublishAndPackExecutable(string projectDir)
    {
        var publishDir = Path.Combine(Root, "publish");
        RunDotnet(
            ["publish", "-c", "Release", "-o", publishDir, "--nologo", "-p:PackAsTool=false", "-p:UseAppHost=true"],
            projectDir);

        var staging = Path.Combine(Root, "exe-nupkg");
        var tools = Path.Combine(staging, "tools", "net10.0", "any");
        Directory.CreateDirectory(tools);
        foreach (var file in Directory.GetFiles(publishDir))
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)), overwrite: true);

        var exeName = OperatingSystem.IsWindows() ? "hello-tool.exe" : "hello-tool";
        File.WriteAllText(Path.Combine(tools, "DotnetToolSettings.xml"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <DotNetCliTool Version="1">
              <Commands>
                <Command Name="hello-exe" EntryPoint="{exeName}" Runner="executable" />
              </Commands>
            </DotNetCliTool>
            """);

        File.WriteAllText(Path.Combine(staging, $"{ExePackageId}.nuspec"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{ExePackageId}</id>
                <version>{PackageVersion}</version>
                <authors>ndnx</authors>
                <description>executable fixture</description>
                <packageTypes>
                  <packageType name="DotnetTool" />
                </packageTypes>
              </metadata>
            </package>
            """);

        var nupkg = Path.Combine(FeedDirectory, $"{ExePackageId}.{PackageVersion}.nupkg");
        if (File.Exists(nupkg))
            File.Delete(nupkg);
        ZipFile.CreateFromDirectory(staging, nupkg);
    }

    static void RunDotnet(string[] args, string workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {string.Join(' ', args)} failed ({process.ExitCode}).{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
    }
}
