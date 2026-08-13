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

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ServiceIndex))]
[JsonSerializable(typeof(FlatContainerIndex))]
sealed partial class NuGetJsonContext : JsonSerializerContext;
