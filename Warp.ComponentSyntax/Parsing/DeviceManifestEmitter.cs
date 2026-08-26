using System.Text.Json;
using System.Text.Json.Serialization;

namespace Warp.ComponentSyntax.Parsing;

/// <summary>Emits the JSON manifest required in the generated device package.</summary>
public static class DeviceManifestEmitter
{
    public static string Emit(Manifest manifest, bool writeIndented = false)
        => JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
}
