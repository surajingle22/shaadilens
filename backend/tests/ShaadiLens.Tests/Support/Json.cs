using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShaadiLens.Tests.Support;

public static class Json
{
    /// <summary>Same settings as the API: camelCase + enums as strings.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
