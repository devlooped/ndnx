namespace Tests;

public class SourceGuardTests
{
    [Fact]
    public void Ndnx_sources_do_not_gate_on_child_aot()
    {
        var ndnxDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ndnx"));
        Assert.True(Directory.Exists(ndnxDir), $"Could not find ndnx sources at {ndnxDir}");

        foreach (var file in Directory.GetFiles(ndnxDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("IsNativeAot", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PublishAot", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("must be AOT", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("not AOT", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RejectNonAot", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RequiresAot", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
