# Third-party notices

This project's own source is MIT-licensed (see `LICENSE`). It depends on, and the
built Docker image bundles, the following third-party components under their own licenses.

| Component | License | Notes |
|-----------|---------|-------|
| [SonarAnalyzer.CSharp](https://www.nuget.org/packages/SonarAnalyzer.CSharp) | **LGPL-3.0-only** | The C# analysis engine. Loaded as a separate, unmodified assembly at runtime. The Docker image redistributes this binary; the LGPL permits this. |
| [Microsoft.CodeAnalysis.CSharp (Roslyn)](https://github.com/dotnet/roslyn) | MIT | Compiler/analyzer host APIs. |
| [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) | MIT | MCP server SDK. |
| [.NET runtime / base images](https://github.com/dotnet/runtime) | MIT | Microsoft .NET. |

**LGPL note:** SonarAnalyzer.CSharp is used unmodified and loaded as a standalone
assembly via the Roslyn analyzer API. No SonarSource code is statically linked or
modified. To use a different version, rebuild with another `SonarAnalyzer.CSharp`
package version in `src/SonarCsharpMcp/SonarCsharpMcp.csproj`.

This tool is not affiliated with or endorsed by SonarSource.
