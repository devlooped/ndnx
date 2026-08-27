using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Tests;

/// <summary>
/// Packs a tiny framework-dependent tool and an executable-runner tool into a local folder feed.
/// Also ships multi-RID wrapper + RID-package nupkgs (the .NET 10 tool layout).
/// </summary>
public sealed class HelloToolFeed : IDisposable
{
    public const string Phrase = "Hello from fixture!";
    public const string AnyPhrase = "Hello from any RID!";
    public const string PackageId = "hello-tool";
    public const string ExePackageId = "hello-exe";
    public const string RidWrapperId = "hello-rid";
    public const string AnyWrapperId = "hello-any";
    public const string NoMatchWrapperId = "hello-nomatch";
    public const string UnusedRid = "unused-rid";
    public const string PackageVersion = "1.0.0";

    public static string HostRid { get; } = RuntimeInformation.RuntimeIdentifier;
    public static string RidImplId => $"{RidWrapperId}.{HostRid}";
    public static string AnyImplId => $"{AnyWrapperId}.any";

    static readonly object Gate = new();

    public string Root { get; private set; }
    public string FeedDirectory { get; private set; }

    public HelloToolFeed()
    {
        Root = Path.Combine(Path.GetTempPath(), "ndx-hello-tool-feed");
        FeedDirectory = Path.Combine(Root, "feed");
        lock (Gate)
            Build();
    }

    void Build()
    {
        Directory.CreateDirectory(FeedDirectory);

        var stamp = Path.Combine(Root, "stamp.txt");
        if (File.Exists(stamp) && AllFeedPackagesExist())
            return;

        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);

        Directory.CreateDirectory(FeedDirectory);
        var projectDir = Path.Combine(Root, "src");
        Directory.CreateDirectory(projectDir);
        WriteProject(projectDir);
        PackFrameworkDependent(projectDir);
        PublishAndPackExecutable(projectDir);
        PackMultiRidFixtures();
        File.WriteAllText(stamp, DateTime.UtcNow.ToString("O"));
    }

    bool AllFeedPackagesExist() =>
        File.Exists(FeedNupkg(PackageId)) &&
        File.Exists(FeedNupkg(ExePackageId)) &&
        File.Exists(FeedNupkg(RidWrapperId)) &&
        File.Exists(FeedNupkg(RidImplId)) &&
        File.Exists(FeedNupkg(AnyWrapperId)) &&
        File.Exists(FeedNupkg(AnyImplId)) &&
        File.Exists(FeedNupkg(NoMatchWrapperId));

    string FeedNupkg(string packageId) => Path.Combine(FeedDirectory, $"{packageId}.{PackageVersion}.nupkg");

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
                <Authors>ndx</Authors>
                <Description>ndx test fixture</Description>
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
                <authors>ndx</authors>
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

    void PackMultiRidFixtures()
    {
        var helloExtract = Path.Combine(Root, "hello-extract");
        ZipFile.ExtractToDirectory(FeedNupkg(PackageId), helloExtract);
        var helloTools = FindToolsDirectory(helloExtract)
            ?? throw new InvalidOperationException("hello-tool nupkg is missing a tools/ directory.");

        WriteWrapperNupkg(
            RidWrapperId,
            "hello-rid",
            (HostRid, RidImplId),
            (UnusedRid, $"{RidWrapperId}.{UnusedRid}"));
        WriteRidImplementationNupkg(RidImplId, "hello-rid", helloTools);

        var anyPublish = PublishAnyTool();
        WriteWrapperNupkg(
            AnyWrapperId,
            "hello-any",
            (UnusedRid, $"{AnyWrapperId}.{UnusedRid}"),
            ("any", AnyImplId));
        WriteRidImplementationNupkg(AnyImplId, "hello-any", anyPublish);

        WriteWrapperNupkg(
            NoMatchWrapperId,
            "hello-nomatch",
            (UnusedRid, $"{NoMatchWrapperId}.{UnusedRid}"));
    }

    string PublishAnyTool()
    {
        var projectDir = Path.Combine(Root, "src-any");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "hello-any.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDir, "Program.cs"),
            $$"""
            int code = 0;
            if (args is [{ } first, ..] && int.TryParse(first, out var parsed))
                code = parsed;

            Console.WriteLine("{{AnyPhrase}}");
            foreach (var arg in args)
                Console.WriteLine($"arg:{arg}");
            return code;
            """);

        var publishDir = Path.Combine(Root, "publish-any");
        RunDotnet(["publish", "-c", "Release", "-o", publishDir, "--nologo"], projectDir);
        return publishDir;
    }

    void WriteWrapperNupkg(string packageId, string commandName, params (string Rid, string Id)[] ridPackages)
    {
        WriteNupkg(packageId, "DotnetTool", staging =>
        {
            var tools = Path.Combine(staging, "tools", "any", "any");
            Directory.CreateDirectory(tools);

            var maps = string.Join(Environment.NewLine, ridPackages.Select(p =>
                $"""        <RuntimeIdentifierPackage RuntimeIdentifier="{p.Rid}" Id="{p.Id}" />"""));

            File.WriteAllText(Path.Combine(tools, "DotnetToolSettings.xml"),
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <DotNetCliTool Version="2">
                  <Commands>
                    <Command Name="{commandName}" />
                  </Commands>
                  <RuntimeIdentifierPackages>
                {maps}
                  </RuntimeIdentifierPackages>
                </DotNetCliTool>
                """);
        });
    }

    void WriteRidImplementationNupkg(string packageId, string commandName, string payloadDirectory)
    {
        WriteNupkg(packageId, "DotnetToolRidPackage", staging =>
        {
            var tools = Path.Combine(staging, "tools", "net10.0", "any");
            Directory.CreateDirectory(tools);
            foreach (var file in Directory.GetFiles(payloadDirectory))
                File.Copy(file, Path.Combine(tools, Path.GetFileName(file)), overwrite: true);

            var entryPoint = Directory.GetFiles(tools, "*.dll")
                .Select(Path.GetFileName)
                .FirstOrDefault(name => name is not null &&
                    !name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No tool dll in {payloadDirectory}.");

            File.WriteAllText(Path.Combine(tools, "DotnetToolSettings.xml"),
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <DotNetCliTool Version="1">
                  <Commands>
                    <Command Name="{commandName}" EntryPoint="{entryPoint}" Runner="dotnet" />
                  </Commands>
                </DotNetCliTool>
                """);
        });
    }

    void WriteNupkg(string packageId, string packageType, Action<string> populate)
    {
        var staging = Path.Combine(Root, "nupkg-" + packageId);
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        populate(staging);
        File.WriteAllText(Path.Combine(staging, $"{packageId}.nuspec"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{packageId}</id>
                <version>{PackageVersion}</version>
                <authors>ndx</authors>
                <description>{packageId} fixture</description>
                <packageTypes>
                  <packageType name="{packageType}" />
                </packageTypes>
              </metadata>
            </package>
            """);

        var nupkg = FeedNupkg(packageId);
        if (File.Exists(nupkg))
            File.Delete(nupkg);
        ZipFile.CreateFromDirectory(staging, nupkg);
    }

    static string? FindToolsDirectory(string extractedPackage)
    {
        var tools = Path.Combine(extractedPackage, "tools");
        if (!Directory.Exists(tools))
            return null;

        return Directory.GetDirectories(tools, "*", SearchOption.AllDirectories)
            .OrderByDescending(dir => Directory.GetFiles(dir, "*.dll").Length)
            .FirstOrDefault(dir => Directory.GetFiles(dir, "*.dll").Length > 0);
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

        var (exit, stdout, stderr) = ProcessCapture.Run(start, timeoutMs: 180_000);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {string.Join(' ', args)} failed ({exit}).{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
    }
}
