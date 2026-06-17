using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SonarCsharpMcp;

[McpServerToolType]
public static class AnalysisTools
{
    [McpServerTool(Name = "analyze_csharp")]
    [Description(
        "Analyze a C# file or code snippet with the project's SonarCloud C# quality profile " +
        "(the same 'Sonar way' ruleset shown in SonarCloud, fetched live) using the SonarAnalyzer.CSharp engine. " +
        "Returns code quality issues (csharpsquid rules) with rule key, message, severity and line range. " +
        "Optionally provide a codeSnippet to filter issues to that region. " +
        "Note: this is single-file analysis — whole-project rules and security taint rules (roslyn.sonaranalyzer.security.*) will not fire.")]
    public static Task<string> AnalyzeCsharp(
        AnalysisEngine engine,
        [Description("Complete C# file content to analyze.")]
        string fileContent,
        [Description("The SonarQube/SonarCloud project key (defaults to the SONARQUBE_PROJECT_KEY env var).")]
        string? projectKey = null,
        [Description("Optional code snippet; only issues overlapping this region are returned. Must match content within fileContent.")]
        string? codeSnippet = null,
        CancellationToken cancellationToken = default)
    {
        return engine.AnalyzeAsync(fileContent, projectKey, codeSnippet, cancellationToken);
    }
}
