using System.Text.Json.Serialization;
using Warp.Diagnostics;

namespace Warp.ComponentSyntax.Parsing;

public sealed record Manifest(
    [property: JsonPropertyName("package")] string Package,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("versionName")] string VersionName,
    [property: JsonPropertyName("versionCode")] int VersionCode,
    [property: JsonPropertyName("icon")] string Icon,
    [property: JsonPropertyName("deviceTypeList")] IReadOnlyList<string> DeviceTypeList,
    [property: JsonPropertyName("features")] IReadOnlyList<ManifestFeature> Features,
    [property: JsonPropertyName("config")] ManifestConfig Config,
    [property: JsonPropertyName("router")] ManifestRouter Router,
    [property: JsonPropertyName("minPlatformVersion")] int? MinPlatformVersion = null,
    [property: JsonPropertyName("minAPILevel")] int MinApiLevel = 1,
    [property: JsonIgnore] SourcePosition? Position = null);

public sealed record ManifestFeature([property: JsonPropertyName("name")] string FeatureName);
public sealed record ManifestConfig(
    [property: JsonPropertyName("logLevel")] string LogLevel,
    [property: JsonPropertyName("designWidth")] string DesignWidth,
    // This affects compiler passes only and is deliberately not part of the
    // device runtime manifest.
    [property: JsonIgnore] bool MinifyIdentifiers = true,
    // Paths to compiler-pass assemblies. These are build-machine inputs, not
    // device manifest data, so they must never be emitted into manifest.json.
    [property: JsonIgnore] IReadOnlyList<string>? Passes = null);
public sealed record ManifestRouter(
    [property: JsonPropertyName("entry")] string Entry,
    [property: JsonPropertyName("pages")] IReadOnlyDictionary<string, ManifestPage> Pages);
public sealed record ManifestPage([property: JsonPropertyName("component")] string Component);
