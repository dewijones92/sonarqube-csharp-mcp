# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this is

An MCP (stdio) server exposing one tool, **`analyze_csharp`**, that analyzes C# code with
the genuine `SonarAnalyzer.CSharp` engine against a project's **live SonarCloud / SonarQube
quality profile**. It exists to fill the one gap in SonarSource's official `mcp/sonarqube`
server, which cannot analyze C# (its `sonarlint-core` engine has no standalone C# analyzer).
Run it **alongside** the official server, not instead of it.

## The two design decisions that matter

1. **In-process Roslyn, not `dotnet build`.** Analysis runs via the Roslyn analyzer API
   (`Compilation.WithAnalyzers(...).GetAnalyzerDiagnosticsAsync()`), loading the SonarAnalyzer
   DLL directly. This is deliberate: `dotnet build`/`csc` **suppresses all analyzer output when
   the compilation has errors**, and snippets routinely reference types defined in other files.
   The Roslyn API reports analyzer diagnostics anyway — like SonarLint in an IDE. Do **not**
   "simplify" this back to shelling out to `dotnet build`.

2. **DRY ruleset — fetched live, never hardcoded.** The active rules + parameters come from the
   project's quality profile via the SonarQube/SonarCloud web API
   (`/api/qualityprofiles/search`, `/api/rules/search`), reusing the same `SONARQUBE_TOKEN` /
   `SONARQUBE_ORG` as the official server. Don't introduce a hardcoded rule list.

## Layout

```
src/SonarCsharpMcp/
  Program.cs           DI + MCP stdio host (logs MUST go to stderr, not stdout)
  AnalysisTools.cs     The [McpServerTool] analyze_csharp entry point
  AnalysisEngine.cs    Profile fetch + Roslyn analysis + result shaping (the core)
Dockerfile             SDK build stage -> runtime image (no SDK needed at runtime)
scripts/build-multiarch.sh   buildx amd64+arm64 (pushes to a registry)
.github/workflows/     CI: multi-arch build + publish to GHCR
```

## Build, run, test

```bash
# Build the server
dotnet build src/SonarCsharpMcp -c Release

# Build the image
docker build -t sonarqube-csharp-mcp:latest .

# Smoke-test over stdio (initialize -> tools/call). Needs SONARQUBE_TOKEN + (SONARQUBE_ORG for SonarCloud).
SONARQUBE_TOKEN=... SONARQUBE_ORG=... python3 test-harness.py \
  src/SonarCsharpMcp/bin/Release/net10.0/SonarCsharpMcp.dll <some-file>.cs
```

## Gotchas

- **stdout is the JSON-RPC channel.** Any stray `Console.WriteLine` or log to stdout corrupts
  the protocol. Logging is routed to stderr in `Program.cs` — keep it that way.
- **SonarCloud requires `&organization`; SonarQube Server has no organizations.** The code omits
  the param when `SONARQUBE_ORG` is unset — preserve that branch when editing URL building.
- **Single-file analysis only.** Whole-project, data-flow, and `roslyn.sonaranalyzer.security.*`
  taint rules will not fire. A few assembly-attribute rules are deliberately excluded as
  synthetic-assembly noise (see `AssemblyScopedExclusions`).
- **Per-language rule differences are real.** Sonar's C# and Java implementations of the same
  rule number can differ (e.g. C# S1172 exempts public methods; Java's does not). Trust the C#
  engine here, not cross-language analogies.
- `SonarAnalyzer.CSharp` is **LGPL-3.0** and used unmodified — see `NOTICE.md`. Don't statically
  link or modify it.
