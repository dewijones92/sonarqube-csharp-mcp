using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace SonarCsharpMcp;

/// <summary>
/// Runs SonarAnalyzer.CSharp in-process via the Roslyn analyzer API, activating exactly the
/// rules from the project's SonarCloud C# quality profile (fetched live — nothing hardcoded).
/// Using GetAnalyzerDiagnosticsAsync (rather than `dotnet build`) means analyzers still report
/// on snippets that reference types defined elsewhere, exactly like SonarLint in an IDE.
/// </summary>
public sealed class AnalysisEngine
{
    private readonly ILogger<AnalysisEngine> _log;
    private readonly HttpClient _http = new();

    private readonly string _baseUrl;
    private readonly string _org;
    private readonly string? _defaultProjectKey;
    private readonly string _analyzerDir;

    private readonly ConcurrentDictionary<string, ProfileConfig> _profileCache = new();
    private readonly SemaphoreSlim _profileLock = new(1, 1);
    private readonly Lazy<ImmutableArray<DiagnosticAnalyzer>> _analyzers;
    private static readonly ImmutableArray<MetadataReference> BclReferences = LoadBclReferences();

    // Assembly-attribute rules that always fire on the synthetic single-file assembly
    // (they pass in CI because the real project sets these attributes). Excluded to avoid noise.
    private static readonly ImmutableHashSet<string> AssemblyScopedExclusions =
        ImmutableHashSet.Create("S3904", "S3990", "S3992", "S3993", "S3994");

    public AnalysisEngine(ILogger<AnalysisEngine> log)
    {
        _log = log;
        _baseUrl = (Environment.GetEnvironmentVariable("SONARQUBE_URL") ?? "https://sonarcloud.io").TrimEnd('/');
        _org = Environment.GetEnvironmentVariable("SONARQUBE_ORG") ?? "";
        _defaultProjectKey = Environment.GetEnvironmentVariable("SONARQUBE_PROJECT_KEY");
        _analyzerDir = Environment.GetEnvironmentVariable("SONAR_ANALYZER_DIR")
                       ?? Path.Combine(AppContext.BaseDirectory, "analyzers");

        var token = Environment.GetEnvironmentVariable("SONARQUBE_TOKEN") ?? "";
        if (!string.IsNullOrEmpty(token))
        {
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes(token + ":"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        _analyzers = new Lazy<ImmutableArray<DiagnosticAnalyzer>>(LoadAnalyzers);
    }

    private sealed record RuleMeta(string DiagnosticId, string RuleKey, string Severity,
        string? CleanCodeAttribute, string? Impacts, IReadOnlyDictionary<string, string> Params);

    private sealed record ProfileConfig(string ProfileName, string ProfileKey,
        IReadOnlyDictionary<string, RuleMeta> Rules, string SonarLintXml);

    public async Task<string> AnalyzeAsync(string fileContent, string? projectKey,
        string? codeSnippet, CancellationToken ct)
    {
        var key = projectKey ?? _defaultProjectKey;
        if (string.IsNullOrWhiteSpace(key))
            return Error("No project key provided and SONARQUBE_PROJECT_KEY is not set.");

        ProfileConfig profile;
        try
        {
            profile = await EnsureProfileAsync(key!, ct);
        }
        catch (Exception ex)
        {
            return Error("Could not load the C# quality profile: " + ex.Message);
        }

        int snippetStart = -1, snippetEnd = -1;
        if (!string.IsNullOrWhiteSpace(codeSnippet))
        {
            (snippetStart, snippetEnd) = FindSnippet(fileContent, codeSnippet!);
            if (snippetStart == -1)
                return Error("Could not find the provided code snippet in the file content. " +
                             "Ensure it matches exactly (including whitespace).");
        }

        List<Issue> issues;
        try
        {
            issues = await RunRoslynAsync(fileContent, profile, snippetStart, snippetEnd, ct);
        }
        catch (Exception ex)
        {
            return Error("Error while analyzing the code: " + ex.Message);
        }

        var result = new JsonObject
        {
            ["issues"] = new JsonArray(issues.Select(i => (JsonNode)new JsonObject
            {
                ["ruleKey"] = i.RuleKey,
                ["primaryMessage"] = i.Message,
                ["severity"] = i.Severity,
                ["cleanCodeAttribute"] = i.CleanCodeAttribute,
                ["impacts"] = i.Impacts,
                ["hasQuickFixes"] = false,
                ["textRange"] = new JsonObject { ["startLine"] = i.StartLine, ["endLine"] = i.EndLine },
            }).ToArray()),
            ["issueCount"] = issues.Count,
            ["profile"] = new JsonObject
            {
                ["name"] = profile.ProfileName,
                ["key"] = profile.ProfileKey,
                ["activeRulesFromNuGet"] = profile.Rules.Count,
            },
            ["note"] = "Single-file analysis via SonarAnalyzer.CSharp (Roslyn). Whole-project, " +
                       "data-flow and security taint rules will not appear here.",
        };
        return result.ToJsonString();
    }

    // ---- Roslyn analysis -------------------------------------------------

    private async Task<List<Issue>> RunRoslynAsync(string fileContent, ProfileConfig profile,
        int snippetStart, int snippetEnd, CancellationToken ct)
    {
        var tree = CSharpSyntaxTree.ParseText(fileContent,
            new CSharpParseOptions(LanguageVersion.Latest), path: "Snippet.cs", cancellationToken: ct);

        // Activate exactly the profile's rules (many are disabled-by-default in the analyzer).
        var specific = profile.Rules.Keys.ToImmutableDictionary(id => id, _ => ReportDiagnostic.Warn);
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithSpecificDiagnosticOptions(specific);

        var compilation = CSharpCompilation.Create("snippet", new[] { tree }, BclReferences, options);

        var additionalFiles = ImmutableArray.Create<AdditionalText>(
            new InMemoryAdditionalText("SonarLint.xml", profile.SonarLintXml));
        var analyzerOptions = new AnalyzerOptions(additionalFiles);

        var withAnalyzers = compilation.WithAnalyzers(_analyzers.Value, analyzerOptions);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(ct);

        var seen = new HashSet<string>();
        var issues = new List<Issue>();
        foreach (var d in diagnostics)
        {
            if (!profile.Rules.TryGetValue(d.Id, out var meta))
                continue; // keep only rules in the live profile (csharpsquid Sxxxx)
            if (AssemblyScopedExclusions.Contains(d.Id))
                continue; // assembly-attribute rules that always fire on a synthetic snippet assembly

            var span = d.Location.GetLineSpan();
            var startLine = span.StartLinePosition.Line + 1;
            var endLine = span.EndLinePosition.Line + 1;

            if (snippetStart != -1 && !(startLine <= snippetEnd && endLine >= snippetStart))
                continue;

            if (!seen.Add($"{d.Id}:{startLine}:{span.StartLinePosition.Character}"))
                continue;

            issues.Add(new Issue(meta.RuleKey, d.GetMessage(), meta.Severity,
                meta.CleanCodeAttribute, meta.Impacts, startLine, endLine));
        }
        return issues.OrderBy(i => i.StartLine).ThenBy(i => i.RuleKey).ToList();
    }

    private ImmutableArray<DiagnosticAnalyzer> LoadAnalyzers()
    {
        if (!Directory.Exists(_analyzerDir))
            throw new DirectoryNotFoundException($"Analyzer directory not found: {_analyzerDir}");

        var loader = new AnalyzerAssemblyLoader();
        var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        foreach (var dll in Directory.GetFiles(_analyzerDir, "*.dll"))
        {
            try
            {
                var reference = new AnalyzerFileReference(dll, loader);
                analyzers.AddRange(reference.GetAnalyzers(LanguageNames.CSharp));
            }
            catch
            {
                // Not every DLL in the folder is an analyzer assembly; skip non-analyzer ones.
            }
        }
        _log.LogInformation("Loaded {Count} C# analyzers from {Dir}", analyzers.Count, _analyzerDir);
        return analyzers.ToImmutable();
    }

    private static ImmutableArray<MetadataReference> LoadBclReferences()
    {
        var refs = ImmutableArray.CreateBuilder<MetadataReference>();
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in tpa)
        {
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { /* skip */ }
        }
        if (refs.Count == 0)
            refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return refs.ToImmutable();
    }

    // ---- DRY profile fetch ----------------------------------------------

    private async Task<ProfileConfig> EnsureProfileAsync(string projectKey, CancellationToken ct)
    {
        if (_profileCache.TryGetValue(projectKey, out var cached))
            return cached;

        await _profileLock.WaitAsync(ct);
        try
        {
            if (_profileCache.TryGetValue(projectKey, out cached))
                return cached;
            var profile = await FetchProfileAsync(projectKey, ct);
            _profileCache[projectKey] = profile;
            return profile;
        }
        finally
        {
            _profileLock.Release();
        }
    }

    private async Task<ProfileConfig> FetchProfileAsync(string projectKey, CancellationToken ct)
    {
        // SonarCloud requires &organization; self-hosted SonarQube Server has no organizations,
        // so only include it when configured.
        var org = string.IsNullOrEmpty(_org) ? "" : $"organization={Uri.EscapeDataString(_org)}&";

        var qpUrl = $"{_baseUrl}/api/qualityprofiles/search?{org}project={Uri.EscapeDataString(projectKey)}";
        var qpDoc = JsonNode.Parse(await _http.GetStringAsync(qpUrl, ct))!;
        var csProfile = qpDoc["profiles"]!.AsArray()
            .FirstOrDefault(p => (string?)p!["language"] == "cs")
            ?? throw new InvalidOperationException("No C# quality profile assigned to the project.");
        var profileKey = (string)csProfile!["key"]!;
        var profileName = (string)csProfile!["name"]!;

        var rules = new Dictionary<string, RuleMeta>();
        var page = 1;
        int total;
        do
        {
            var rUrl = $"{_baseUrl}/api/rules/search?{org}activation=true&qprofile={profileKey}" +
                       $"&languages=cs&ps=500&p={page}&f=severity,name,cleanCodeAttribute,impacts,params,actives";
            var rDoc = JsonNode.Parse(await _http.GetStringAsync(rUrl, ct))!;
            total = (int)rDoc["total"]!;
            var actives = rDoc["actives"]?.AsObject();
            foreach (var r in rDoc["rules"]!.AsArray())
            {
                var ruleKey = (string)r!["key"]!;
                var parts = ruleKey.Split(':', 2);
                if (parts.Length != 2 || parts[0] != "csharpsquid")
                    continue;
                var diagId = parts[1];

                // Effective parameter values come from the activation; fall back to rule defaults.
                var prms = new Dictionary<string, string>();
                foreach (var p in r["params"]?.AsArray() ?? new JsonArray())
                {
                    var pk = (string?)p!["key"];
                    var pv = (string?)p["defaultValue"];
                    if (pk != null && pv != null) prms[pk] = pv;
                }
                if (actives?[ruleKey] is JsonArray act && act.Count > 0)
                {
                    foreach (var p in act[0]!["params"]?.AsArray() ?? new JsonArray())
                    {
                        var pk = (string?)p!["key"];
                        var pv = (string?)p["value"];
                        if (pk != null && pv != null) prms[pk] = pv;
                    }
                }

                var impacts = r["impacts"] is JsonArray ia && ia.Count > 0 ? ia.ToJsonString() : null;
                rules[diagId] = new RuleMeta(diagId, ruleKey, (string?)r["severity"] ?? "MAJOR",
                    (string?)r["cleanCodeAttribute"], impacts, prms);
            }
            page++;
        } while ((page - 1) * 500 < total);

        _log.LogInformation("Loaded C# profile '{Name}' ({Key}) with {Count} csharpsquid rules",
            profileName, profileKey, rules.Count);
        return new ProfileConfig(profileName, profileKey, rules, BuildSonarLintXml(rules.Values));
    }

    private static string BuildSonarLintXml(IEnumerable<RuleMeta> rules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<AnalysisInput>");
        sb.AppendLine("  <Settings/>");
        sb.AppendLine("  <Rules>");
        foreach (var rule in rules.Where(r => r.Params.Count > 0))
        {
            sb.AppendLine("    <Rule>");
            sb.AppendLine($"      <Key>{rule.DiagnosticId}</Key>");
            sb.AppendLine("      <Parameters>");
            foreach (var (k, v) in rule.Params)
            {
                sb.AppendLine("        <Parameter>");
                sb.AppendLine($"          <Key>{System.Security.SecurityElement.Escape(k)}</Key>");
                sb.AppendLine($"          <Value>{System.Security.SecurityElement.Escape(v)}</Value>");
                sb.AppendLine("        </Parameter>");
            }
            sb.AppendLine("      </Parameters>");
            sb.AppendLine("    </Rule>");
        }
        sb.AppendLine("  </Rules>");
        sb.AppendLine("</AnalysisInput>");
        return sb.ToString();
    }

    private static (int, int) FindSnippet(string fileContent, string snippet)
    {
        var fileLines = fileContent.Replace("\r\n", "\n").Split('\n');
        var snippetLines = snippet.Replace("\r\n", "\n").Split('\n');
        if (snippetLines.Length == 0 || fileLines.Length < snippetLines.Length)
            return (-1, -1);
        for (var i = 0; i <= fileLines.Length - snippetLines.Length; i++)
        {
            var match = true;
            for (var j = 0; j < snippetLines.Length; j++)
                if (fileLines[i + j] != snippetLines[j]) { match = false; break; }
            if (match) return (i + 1, i + snippetLines.Length);
        }
        return (-1, -1);
    }

    private static string Error(string message) =>
        new JsonObject { ["error"] = message, ["issues"] = new JsonArray(), ["issueCount"] = 0 }.ToJsonString();

    private sealed record Issue(string RuleKey, string Message, string Severity,
        string? CleanCodeAttribute, string? Impacts, int StartLine, int EndLine);

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        private readonly SourceText _text = SourceText.From(content, Encoding.UTF8);
        public override string Path { get; } = path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private sealed class AnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }
}
