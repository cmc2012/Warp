using Warp.Diagnostics;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Warp.ComponentSyntax.Parsing;

/// <summary>Parses the project's sole source manifest: top-level manifest.yaml.</summary>
public sealed class ManifestParser
{
    public Manifest Parse(string text, string filePath, DiagnosticSink sink)
    {
        if (!Path.GetFileName(filePath).Equals("manifest.yaml", StringComparison.Ordinal) ||
            !Path.GetExtension(filePath).Equals(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            sink.Error("project manifest must be the top-level manifest.yaml file", new SourcePosition(filePath, 1, 1));
            return EmptyManifest();
        }
        return ParseYaml(text, filePath, sink);
    }

    public Manifest ParseYaml(string yamlText, string filePath, DiagnosticSink sink)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var root = deserializer.Deserialize<Dictionary<object, object?>>(yamlText) ?? new();

            string Str(string key) => root.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
            int Int(string key) => root.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var number) ? number : 0;

            var packageName = Str("package");
            var name = Str("name");
            var versionName = Str("versionName");
            var versionCode = Int("versionCode");
            var minPlatformVersion = root.TryGetValue("minPlatformVersion", out var minPlatform) && int.TryParse(minPlatform?.ToString(), out var parsedPlatform) ? parsedPlatform : (int?)null;
            var minApiLevel = root.TryGetValue("minAPILevel", out var minApi) && int.TryParse(minApi?.ToString(), out var parsedApi) ? parsedApi : 1;
            var icon = Str("icon");

            var deviceTypes = new List<string>();
            if (root.TryGetValue("deviceTypeList", out var deviceList) && deviceList is IEnumerable<object> values)
                foreach (var value in values) deviceTypes.Add(value?.ToString() ?? "");

            var features = new List<ManifestFeature>();
            if (root.TryGetValue("features", out var featureList) && featureList is IEnumerable<object> featureValues)
                foreach (var value in featureValues)
                    if (value is Dictionary<object, object?> feature && feature.TryGetValue("name", out var featureName))
                        features.Add(new ManifestFeature(featureName?.ToString() ?? ""));

            var config = new ManifestConfig("log", "device-width");
            if (root.TryGetValue("config", out var rawConfig) && rawConfig is Dictionary<object, object?> configMap)
            {
                var minifyIdentifiers = true;
                if (configMap.TryGetValue("minifyIdentifiers", out var rawMinifyIdentifiers) &&
                    !bool.TryParse(rawMinifyIdentifiers?.ToString(), out minifyIdentifiers))
                {
                    sink.Warning("manifest: config.minifyIdentifiers must be true or false; defaulting to true");
                    minifyIdentifiers = true;
                }
                var passes = new List<string>();
                if (configMap.TryGetValue("passes", out var rawPasses))
                {
                    if (rawPasses is not IEnumerable<object> passValues || rawPasses is string)
                    {
                        sink.Warning("manifest: config.passes must be a list of assembly paths; ignoring it");
                    }
                    else
                    {
                        foreach (var rawPass in passValues)
                        {
                            if (rawPass is null || string.IsNullOrWhiteSpace(rawPass.ToString()))
                            {
                                sink.Warning("manifest: config.passes entries must be non-empty assembly paths; ignoring an entry");
                                continue;
                            }
                            passes.Add(rawPass.ToString()!);
                        }
                    }
                }
                config = new ManifestConfig(
                    configMap.TryGetValue("logLevel", out var logLevel) ? logLevel?.ToString() ?? "log" : "log",
                    configMap.TryGetValue("designWidth", out var designWidth) ? designWidth?.ToString() ?? "device-width" : "device-width",
                    minifyIdentifiers,
                    passes);
            }

            var router = new ManifestRouter("pages/index", new Dictionary<string, ManifestPage>());
            if (root.TryGetValue("router", out var rawRouter) && rawRouter is Dictionary<object, object?> routerMap)
            {
                var entry = routerMap.TryGetValue("entry", out var rawEntry) ? rawEntry?.ToString() ?? "pages/index" : "pages/index";
                var pages = new Dictionary<string, ManifestPage>();
                if (routerMap.TryGetValue("pages", out var rawPages) && rawPages is Dictionary<object, object?> pageMap)
                    foreach (var (rawKey, rawPage) in pageMap)
                    {
                        var component = rawPage is Dictionary<object, object?> page && page.TryGetValue("component", out var rawComponent)
                            ? rawComponent?.ToString() ?? ""
                            : "";
                        pages[rawKey?.ToString() ?? ""] = new ManifestPage(component);
                    }
                router = new ManifestRouter(entry, pages);
            }

            if (packageName.Length == 0) sink.Error("manifest: package required");
            if (name.Length == 0) sink.Error("manifest: name required");
            if (icon.Length == 0) sink.Error("manifest: icon required");
            if (!root.ContainsKey("versionCode")) sink.Error("manifest: versionCode required");
            if (!root.ContainsKey("config")) sink.Error("manifest: config required");
            if (router.Pages.Count == 0) sink.Error("manifest: router.pages required");

            return new Manifest(packageName, name, versionName, versionCode, icon, deviceTypes, features, config, router, minPlatformVersion, minApiLevel);
        }
        catch (Exception exception)
        {
            sink.Fatal($"manifest YAML parse error: {exception.Message}", new SourcePosition(filePath, 1, 1));
            return EmptyManifest();
        }
    }

    private static Manifest EmptyManifest() => new("", "", "", 0, "", [], [], new ManifestConfig("", ""), new ManifestRouter("", new Dictionary<string, ManifestPage>()));
}
