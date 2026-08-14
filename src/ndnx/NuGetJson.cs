using System.Text.Json.Serialization;

namespace ndnx;

sealed class ServiceIndex
{
    public ServiceResource[]? Resources { get; set; }
}

sealed class ServiceResource
{
    [JsonPropertyName("@id")]
    public string? Id { get; set; }

    [JsonPropertyName("@type")]
    public string? Type { get; set; }
}

sealed class FlatContainerIndex
{
    public string[]? Versions { get; set; }
}

sealed class RuntimeGraphFile
{
    public Dictionary<string, RuntimeGraphNode>? Runtimes { get; set; }
}

sealed class RuntimeGraphNode
{
    [JsonPropertyName("#import")]
    public string[]? Import { get; set; }
}

sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ServiceIndex))]
[JsonSerializable(typeof(FlatContainerIndex))]
[JsonSerializable(typeof(RuntimeGraphFile))]
[JsonSerializable(typeof(GitHubRelease))]
sealed partial class NuGetJsonContext : JsonSerializerContext;
