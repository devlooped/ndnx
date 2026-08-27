namespace Tests;

public class SourceGuardTests
{
    [Fact]
    public void Ndx_sources_do_not_gate_on_child_aot()
    {
        var ndxDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ndx"));
        Assert.True(Directory.Exists(ndxDir), $"Could not find ndx sources at {ndxDir}");

        foreach (var file in Directory.GetFiles(ndxDir, "*.cs"))
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
