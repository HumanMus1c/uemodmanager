using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using UEManager.Core.Catalog;
using UEManager.Core.Games;
using UEManager.Core.Updates;

namespace UEManager.Core.Mods;

/// <summary>
/// Resolves official and mod-declared installation, release, and repository URLs for prerequisites, and probes multiple
/// candidate URLs sequentially until a reachable destination is found.
/// </summary>
public static partial class ModPrerequisiteLinks
{
    private const long MaxMetadataFileBytes = 512 * 1024;
    private const int MaxTextLinesToScan = 160;
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonDocumentOptions JsonReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] DependencyJsonKeys =
    [
        "prerequisiteUrls",
        "prerequisites",
        "prerequisite",
        "dependencyUrls",
        "dependencies",
        "dependency",
        "requires",
        "plugins",
    ];

    private static readonly string[] UrlJsonKeys =
    [
        "url",
        "urls",
        "releaseUrl",
        "releasesUrl",
        "release",
        "releases",
        "downloadUrl",
        "download",
        "repo",
        "repository",
        "source",
        "github",
        "nexus",
        "page",
        "homepage",
    ];

    private static readonly string[] KindJsonKeys =
    [
        "kind",
        "id",
        "name",
        "loader",
        "prerequisite",
        "dependency",
    ];

    private static readonly IReadOnlyDictionary<ModPrerequisiteKind, string[]> OfficialFallbackUrls =
        new Dictionary<ModPrerequisiteKind, string[]>
        {
            [ModPrerequisiteKind.Ue4ss] =
            [
                "https://github.com/KintaroModding/ue4ss/releases/latest",
                "https://github.com/KintaroModding/ue4ss/releases",
                "https://github.com/KintaroModding/ue4ss",
                "https://github.com/UE4SS-RE/RE-UE4SS/releases/latest",
                "https://github.com/UE4SS-RE/RE-UE4SS/releases",
                "https://openmods.nexus/mods/kintaromodding/ue4ss",
            ],
            [ModPrerequisiteKind.ModLoader] =
            [
                "https://github.com/KintaroModding/phantombladezero_pbzmodloader/releases/latest",
                "https://github.com/KintaroModding/phantombladezero_pbzmodloader/releases",
                "https://github.com/KintaroModding/phantombladezero_pbzmodloader",
                "https://openmods.nexus/mods/kintaromodding/phantombladezero_pbzmodloader",
            ],
            [ModPrerequisiteKind.ModsMenu] =
            [
                "https://www.nexusmods.com/phantombladezero/mods/149",
            ],
            [ModPrerequisiteKind.CSharpLoader] =
            [
                "https://github.com/KintaroModding/b1csharploader/releases/latest",
                "https://github.com/KintaroModding/b1csharploader/releases",
                "https://github.com/KintaroModding/b1csharploader",
                "https://github.com/czastack/B1CSharpLoader/releases/latest",
                "https://github.com/czastack/B1CSharpLoader",
                "https://openmods.nexus/mods/kintaromodding/b1csharploader",
                "https://www.nexusmods.com/blackmythwukong/mods/1541",
                "https://www.nexusmods.com/blackmythwukong/mods/664",
            ],
        };

    private static readonly IReadOnlyDictionary<ModPrerequisiteKind, string[]> CatalogIds =
        new Dictionary<ModPrerequisiteKind, string[]>
        {
            [ModPrerequisiteKind.Ue4ss] = ["kintaromodding/ue4ss"],
            [ModPrerequisiteKind.ModLoader] = ["kintaromodding/phantombladezero_pbzmodloader"],
            [ModPrerequisiteKind.ModsMenu] = ["kintaromodding/pbzmodsmenu"],
            [ModPrerequisiteKind.CSharpLoader] = ["kintaromodding/b1csharploader", "czastack/b1csharploader"],
        };

    [GeneratedRegex(@"https?://[^\s<>""'`\)\]\}>]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlPattern();

    [GeneratedRegex(@"^/(?:[^/]+/)?mods/(\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NexusModPathPattern();

    /// <summary>Extracts prerequisite repository/release URLs declared inside a mod's library folder on disk.</summary>
    public static Dictionary<ModPrerequisiteKind, List<string>> ExtractDeclaredUrlsFromLibrary(string? libraryFolder)
    {
        if (string.IsNullOrWhiteSpace(libraryFolder) || !Directory.Exists(libraryFolder))
            return [];
        try
        {
            return ExtractDeclaredUrls(Directory.EnumerateFiles(libraryFolder, "*", SearchOption.AllDirectories));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not scan prerequisite declarations in '{libraryFolder}': {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Scans mod manifests, documentation files, and script header comments for declared prerequisite installation,
    /// release, or repository URLs.
    /// </summary>
    public static Dictionary<ModPrerequisiteKind, List<string>> ExtractDeclaredUrls(IEnumerable<string> filePaths)
    {
        var result = new Dictionary<ModPrerequisiteKind, List<string>>();
        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;
            try
            {
                var info = new FileInfo(path);
                if (info.Length is 0 or > MaxMetadataFileBytes)
                    continue;
                if (IsJsonMetadataFile(path))
                    ScanJsonFile(path, result);
                else if (IsTextMetadataFile(path))
                    ScanTextFile(path, result);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable mod files without blocking install or UI inspection.
            }
        }
        return result;
    }

    /// <summary>
    /// Builds an ordered, deduplicated list of candidate download/release/repository URLs for a missing prerequisite.
    /// Mod-declared URLs take precedence, followed by active catalog/manifest entries, game profile links, and verified
    /// official GitHub/OpenMods/Nexus release pages.
    /// </summary>
    public static IReadOnlyList<string> CandidateUrls(
        ModPrerequisiteKind kind,
        GameProfile profile,
        ModRecord? record = null,
        string? libraryFolder = null,
        IEnumerable<CatalogMod>? catalogMods = null,
        RecommendedMod? recommended = null)
    {
        var candidates = new List<string>();

        if (record?.PrerequisiteUrls is not null && record.PrerequisiteUrls.TryGetValue(kind, out var stored))
            AddAll(candidates, stored);

        var libraryUrls = ExtractDeclaredUrlsFromLibrary(libraryFolder);
        if (libraryUrls.TryGetValue(kind, out var scanned))
            AddAll(candidates, scanned);

        if (catalogMods is not null)
        {
            foreach (var mod in catalogMods.Where(m => MatchesCatalogMod(kind, m)))
            {
                AddValidUrl(candidates, mod.Release?.LatestUrl);
                AddValidUrl(candidates, mod.Source);
                AddValidUrl(candidates, mod.Page);
            }
        }

        foreach (var rec in EnumerateRecommended(recommended).Where(r => MatchesRepo(kind, r.Repo)))
        {
            var repoUrl = $"https://github.com/{rec.Repo.Trim('/')}";
            AddValidUrl(candidates, $"{repoUrl}/releases/latest");
            AddValidUrl(candidates, $"{repoUrl}/releases");
            AddValidUrl(candidates, repoUrl);
        }

        if (kind == ModPrerequisiteKind.Ue4ss)
            AddValidUrl(candidates, profile.Ue4ssDownloadUrl);

        if (kind == ModPrerequisiteKind.ModsMenu && profile.ModPageUrl is { Length: > 0 } modPageTemplate
            && profile.Id.Equals("phantom-blade-zero", StringComparison.OrdinalIgnoreCase))
        {
            AddValidUrl(candidates, modPageTemplate.Replace("{id}", "149", StringComparison.Ordinal));
        }

        if (kind == ModPrerequisiteKind.CSharpLoader && profile.ModPageUrl is { Length: > 0 } bmwTemplate
            && profile.Id.Equals("black-myth-wukong", StringComparison.OrdinalIgnoreCase))
        {
            AddValidUrl(candidates, bmwTemplate.Replace("{id}", "1541", StringComparison.Ordinal));
            AddValidUrl(candidates, bmwTemplate.Replace("{id}", "664", StringComparison.Ordinal));
        }

        if (OfficialFallbackUrls.TryGetValue(kind, out var fallbacks))
            AddAll(candidates, fallbacks);

        return candidates;
    }

    /// <summary>
    /// Checks each candidate URL sequentially until one is confirmed reachable, returning its URL or <see langword="null"/>
    /// when all candidates fail.
    /// </summary>
    public static async Task<string?> ResolveFirstReachableUrlAsync(
        HttpClient http,
        IReadOnlyList<string> candidates,
        GameProfile? profile = null,
        string? appVersion = null,
        TimeSpan? perRequestTimeout = null,
        CancellationToken cancellation = default)
    {
        var timeout = perRequestTimeout ?? DefaultProbeTimeout;
        foreach (var rawUrl in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!TryNormalizeHttpUrl(rawUrl, out var normalized) || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
                continue;

            if (TryExtractNexusModId(uri, out var modId)
                && !string.IsNullOrWhiteSpace(profile?.ModSiteApiUrl)
                && !string.IsNullOrWhiteSpace(profile.ModSiteGameId))
            {
                var found = await ModSiteVersions.FetchAsync(
                    http, profile.ModSiteApiUrl, profile.ModSiteGameId, [modId], appVersion ?? "1.0.0", timeout, cancellation);
                if (found is not null)
                {
                    if (found.ContainsKey(modId))
                    {
                        Log.Info($"Prerequisite link verified via mod site API: {normalized}");
                        return normalized;
                    }
                    Log.Warn($"Prerequisite link '{normalized}' returned no published page on the mod site API.");
                    continue;
                }
            }

            if (await ProbeHttpUrlAsync(http, normalized, timeout, cancellation))
            {
                Log.Info($"Prerequisite link reachable: {normalized}");
                return normalized;
            }
        }
        return null;
    }

    private static async Task<bool> ProbeHttpUrlAsync(HttpClient http, string url, TimeSpan timeout, CancellationToken cancellation)
    {
        var status = await SendProbeAsync(http, HttpMethod.Head, url, timeout, cancellation);
        if (status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden)
            status = await SendProbeAsync(http, HttpMethod.Get, url, timeout, cancellation);

        return status is { } code && (int)code >= 200 && (int)code < 400;
    }

    private static async Task<HttpStatusCode?> SendProbeAsync(
        HttpClient http, HttpMethod method, string url, TimeSpan timeout, CancellationToken cancellation)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UEManager", "1.0"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if ((int)response.StatusCode >= 400)
                Log.Warn($"Prerequisite probe '{url}' ({method}) answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            return response.StatusCode;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            Log.Warn($"Prerequisite probe '{url}' ({method}) failed: {ex.Message}");
            return null;
        }
    }

    private static bool IsJsonMetadataFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("mod.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dependencies.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("prerequisites.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".modsmenu.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextMetadataFile(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        return ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ini", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase)
            || name.Equals("main.js", StringComparison.OrdinalIgnoreCase)
            || name.Equals("main.ts", StringComparison.OrdinalIgnoreCase)
            || name.Equals("main.lua", StringComparison.OrdinalIgnoreCase)
            || name.Equals("config.lua", StringComparison.OrdinalIgnoreCase)
            || name.Equals("manifest.js", StringComparison.OrdinalIgnoreCase);
    }

    private static void ScanJsonFile(string path, Dictionary<ModPrerequisiteKind, List<string>> found)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), JsonReadOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!DependencyJsonKeys.Any(k => k.Equals(prop.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                ReadJsonDependencyValue(prop.Value, found);
            }
        }
        catch (JsonException)
        {
            // Malformed third-party metadata should never fail installation.
        }
    }

    private static void ReadJsonDependencyValue(JsonElement element, Dictionary<ModPrerequisiteKind, List<string>> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var matchedMapKey = false;
                foreach (var entry in element.EnumerateObject())
                {
                    if (TryParseKind(entry.Name) is { } kind)
                    {
                        matchedMapKey = true;
                        ExtractUrlsFromJsonElement(entry.Value, url => AddDeclaredUrl(found, kind, url));
                    }
                }
                if (!matchedMapKey)
                    ReadJsonDependencyEntryObject(element, found);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                        ReadJsonDependencyEntryObject(item, found);
                    else if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                        ScanLineForKindAndUrls(text, pendingKind: null, found);
                }
                break;

            case JsonValueKind.String when element.GetString() is { } text:
                ScanLineForKindAndUrls(text, pendingKind: null, found);
                break;
        }
    }

    private static void ReadJsonDependencyEntryObject(JsonElement obj, Dictionary<ModPrerequisiteKind, List<string>> found)
    {
        ModPrerequisiteKind? kind = null;
        foreach (var key in KindJsonKeys)
        {
            if (TryGetPropertyCaseInsensitive(obj, key, out var value)
                && value.ValueKind == JsonValueKind.String
                && TryParseKind(value.GetString()) is { } parsed)
            {
                kind = parsed;
                break;
            }
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (!UrlJsonKeys.Any(k => k.Equals(prop.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            ExtractUrlsFromJsonElement(prop.Value, url =>
            {
                var targetKind = kind ?? InferKindFromUrl(url);
                if (targetKind is { } k)
                    AddDeclaredUrl(found, k, url);
            });
        }
    }

    private static void ExtractUrlsFromJsonElement(JsonElement element, Action<string> onUrl)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String when element.GetString() is { } s:
                foreach (var url in ExtractUrls(s))
                    onUrl(url);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ExtractUrlsFromJsonElement(item, onUrl);
                break;

            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (UrlJsonKeys.Any(k => k.Equals(prop.Name, StringComparison.OrdinalIgnoreCase)))
                        ExtractUrlsFromJsonElement(prop.Value, onUrl);
                }
                break;
        }
    }

    private static void ScanTextFile(string path, Dictionary<ModPrerequisiteKind, List<string>> found)
    {
        using var reader = new StreamReader(path);
        var inDependencySection = false;
        ModPrerequisiteKind? sectionKind = null;
        var linesScanned = 0;

        while (linesScanned < MaxTextLinesToScan && reader.ReadLine() is { } line)
        {
            linesScanned++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                inDependencySection = false;
                sectionKind = null;
                continue;
            }

            var urls = ExtractUrls(trimmed);
            var lineKind = TryParseKind(trimmed);
            var hasDepKeyword = HasDependencyKeyword(trimmed);

            if (hasDepKeyword && urls.Count == 0)
            {
                inDependencySection = true;
                sectionKind = lineKind ?? sectionKind;
                continue;
            }

            if (urls.Count == 0)
            {
                if (inDependencySection && lineKind is not null)
                    sectionKind = lineKind;
                continue;
            }

            if (hasDepKeyword || inDependencySection || lineKind is not null && IsTaggedUrlLine(trimmed))
                ScanLineForKindAndUrls(trimmed, lineKind ?? sectionKind, found);
        }
    }

    private static void ScanLineForKindAndUrls(string line, ModPrerequisiteKind? pendingKind, Dictionary<ModPrerequisiteKind, List<string>> found)
    {
        var lineKind = TryParseKind(line) ?? pendingKind;
        foreach (var url in ExtractUrls(line))
        {
            var resolvedKind = lineKind ?? InferKindFromUrl(url);
            if (resolvedKind is { } kind)
                AddDeclaredUrl(found, kind, url);
        }
    }

    private static bool HasDependencyKeyword(string line) =>
        line.Contains("require", StringComparison.OrdinalIgnoreCase)
        || line.Contains("depend", StringComparison.OrdinalIgnoreCase)
        || line.Contains("prereq", StringComparison.OrdinalIgnoreCase)
        || line.Contains("前置", StringComparison.Ordinal)
        || line.Contains("依赖", StringComparison.Ordinal)
        || line.Contains("依賴", StringComparison.Ordinal);

    private static bool IsTaggedUrlLine(string line) =>
        line.StartsWith("//", StringComparison.Ordinal)
        || line.StartsWith("--", StringComparison.Ordinal)
        || line.StartsWith("#", StringComparison.Ordinal)
        || line.StartsWith("-", StringComparison.Ordinal)
        || line.StartsWith("*", StringComparison.Ordinal);

    private static List<string> ExtractUrls(string text)
    {
        var urls = new List<string>();
        foreach (Match match in HttpUrlPattern().Matches(text))
        {
            if (TryNormalizeHttpUrl(match.Value, out var normalized))
                urls.Add(normalized);
        }
        return urls;
    }

    private static ModPrerequisiteKind? TryParseKind(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (text.Contains("modsmenu", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mods menu", StringComparison.OrdinalIgnoreCase)
            || text.Contains("pbzmodsmenu", StringComparison.OrdinalIgnoreCase))
            return ModPrerequisiteKind.ModsMenu;
        if (text.Contains("pbzmodloader", StringComparison.OrdinalIgnoreCase)
            || text.Contains("modloader", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mod loader", StringComparison.OrdinalIgnoreCase))
            return ModPrerequisiteKind.ModLoader;
        if (text.Contains("csharploader", StringComparison.OrdinalIgnoreCase)
            || text.Contains("c# loader", StringComparison.OrdinalIgnoreCase)
            || text.Contains("csharp loader", StringComparison.OrdinalIgnoreCase)
            || text.Contains("b1csharploader", StringComparison.OrdinalIgnoreCase))
            return ModPrerequisiteKind.CSharpLoader;
        if (text.Contains("ue4ss", StringComparison.OrdinalIgnoreCase))
            return ModPrerequisiteKind.Ue4ss;
        return null;
    }

    private static ModPrerequisiteKind? InferKindFromUrl(string rawUrl)
    {
        if (rawUrl.Contains("/mods/149", StringComparison.OrdinalIgnoreCase)
            || rawUrl.Contains("pbzmodsmenu", StringComparison.OrdinalIgnoreCase))
            return ModPrerequisiteKind.ModsMenu;
        if (rawUrl.Contains("pbzmodloader", StringComparison.OrdinalIgnoreCase))
            return ModPrerequisiteKind.ModLoader;
        if (rawUrl.Contains("b1csharploader", StringComparison.OrdinalIgnoreCase)
            || rawUrl.Contains("csharploader", StringComparison.OrdinalIgnoreCase)
            || rawUrl.Contains("/mods/1541", StringComparison.OrdinalIgnoreCase)
            || rawUrl.Contains("/mods/664", StringComparison.OrdinalIgnoreCase))
            return ModPrerequisiteKind.CSharpLoader;
        if (rawUrl.Contains("ue4ss", StringComparison.OrdinalIgnoreCase))
            return ModPrerequisiteKind.Ue4ss;
        return null;
    }

    private static bool MatchesCatalogMod(ModPrerequisiteKind kind, CatalogMod mod) =>
        (CatalogIds.TryGetValue(kind, out var ids) && ids.Any(id => id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)))
        || TryParseKind(mod.Name) == kind
        || TryParseKind(mod.Source) == kind;

    private static bool MatchesRepo(ModPrerequisiteKind kind, string? repo) =>
        !string.IsNullOrWhiteSpace(repo)
        && ((CatalogIds.TryGetValue(kind, out var ids) && ids.Any(id => id.Equals(repo, StringComparison.OrdinalIgnoreCase)))
            || TryParseKind(repo) == kind);

    private static IEnumerable<RecommendedMod> EnumerateRecommended(RecommendedMod? root)
    {
        if (root is null)
            yield break;
        yield return root;
        if (root.Extra is not null)
            yield return root.Extra;
    }

    private static bool TryExtractNexusModId(Uri uri, out string modId)
    {
        modId = "";
        if (!uri.Host.EndsWith("nexusmods.com", StringComparison.OrdinalIgnoreCase))
            return false;
        var match = NexusModPathPattern().Match(uri.AbsolutePath);
        if (!match.Success)
            return false;
        modId = match.Groups[1].Value;
        return true;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static void AddDeclaredUrl(Dictionary<ModPrerequisiteKind, List<string>> map, ModPrerequisiteKind kind, string rawUrl)
    {
        if (!map.TryGetValue(kind, out var list))
        {
            list = [];
            map[kind] = list;
        }
        AddValidUrl(list, rawUrl);
    }

    private static void AddAll(List<string> list, IEnumerable<string> urls)
    {
        foreach (var url in urls)
            AddValidUrl(list, url);
    }

    private static void AddValidUrl(List<string> list, string? rawUrl)
    {
        if (!TryNormalizeHttpUrl(rawUrl, out var normalized))
            return;
        if (!list.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            list.Add(normalized);
    }

    private static bool TryNormalizeHttpUrl(string? rawUrl, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(rawUrl))
            return false;
        var cleaned = rawUrl.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '>', '\'', '"', '*');
        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
            return false;
        normalized = cleaned;
        return true;
    }
}
