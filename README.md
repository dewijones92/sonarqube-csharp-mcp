# sonarqube-csharp-mcp

A small MCP (stdio) server that analyzes **C#** code with the genuine
`SonarAnalyzer.CSharp` engine, exposed as the tool **`analyze_csharp`**.

It exists because SonarSource's official `mcp/sonarqube` server cannot analyze C#
snippets: its `analyze_code_snippet` tool runs on `sonarlint-core`, whose standalone
analyzer set has **no C# plugin** (the C# analyzer is a Roslyn/MSBuild analyzer). This
server fills that one gap and is meant to run **alongside** the official server.

## Quickstart

You need **Docker** and a **SonarCloud/SonarQube token** (SonarCloud → *My Account →
Security → Generate Tokens*). No clone or build — the image is public on GHCR.

**1. Add the server to your MCP client** (token is the only required config):

```bash
# Claude Code
claude mcp add sonarqube-csharp -- \
  docker run --rm -i -e SONARQUBE_TOKEN=<your-token> \
  ghcr.io/dewijones92/sonarqube-csharp-mcp:latest
```

```jsonc
// Claude Desktop / Cursor / any MCP client
"sonarqube-csharp": {
  "command": "docker",
  "args": ["run","--rm","-i","-e","SONARQUBE_TOKEN",
           "ghcr.io/dewijones92/sonarqube-csharp-mcp:latest"],
  "env": { "SONARQUBE_TOKEN": "<your-token>" }
}
```

Self-hosted SonarQube Server: also pass `-e SONARQUBE_URL` / set `SONARQUBE_URL` to your
server (and skip the organization).

**2. Restart your client, then ask your agent**, e.g.:

> "Analyze this C# file with Sonar against org `myorg`, project `MyProject`" — and paste the code.

`organization` and `projectKey` are per-call, so one running server can target **any org
or project your token can access**. Set `SONARQUBE_ORG` / `SONARQUBE_PROJECT_KEY` in `env`
as defaults if you mostly use one. Need project keys? Use the official SonarQube MCP
server's `search_my_sonarqube_projects`.

See [Configure](#configure-claude-code-alongside-the-official-server) and
[Tool: `analyze_csharp`](#tool-analyze_csharp) below for full details.

## How it works

- Reads the project's **live C# quality profile** from SonarCloud (the same "Sonar way"
  ruleset shown under *Quality profiles* in the UI) via the web API — nothing about the
  ruleset is hardcoded (DRY). Rules + parameters are fetched with the same
  `SONARQUBE_TOKEN` / `SONARQUBE_ORG` used by the official server.
- Runs `SonarAnalyzer.CSharp` **in-process via the Roslyn analyzer API**
  (`GetAnalyzerDiagnosticsAsync`), not via `dotnet build`. This is the key trick: the
  Roslyn API reports analyzer diagnostics **even when the snippet references types
  defined in other files** (compile errors), exactly like SonarLint in an IDE. Shelling
  `dotnet build` suppresses all analyzer output when the compilation has errors.
- Returns issues in the same shape as the official tool (`ruleKey`, `primaryMessage`,
  `severity`, `cleanCodeAttribute`, `impacts`, `textRange`).

## Limitations (it does **not** behave identically to a full CI scan)

- **Single-file only.** Whole-project rules, data-flow/symbolic-execution rules, and the
  `roslyn.sonaranalyzer.security.*` taint rules (not shipped in the NuGet) will not fire.
- The `SonarAnalyzer.CSharp` NuGet version may drift slightly from SonarCloud's
  server-side analyzer.
- A few assembly-attribute rules (S3904/S3990/S3992/S3993/S3994) are deliberately
  excluded — they always fire on the synthetic single-file assembly but pass in CI.

## Build

```bash
docker build -t sonarqube-csharp-mcp:latest .
```

## Configure (Claude Code, alongside the official server)

**SonarCloud** (set `SONARQUBE_ORG`):

```jsonc
"sonarqube-csharp": {
  "type": "stdio",
  "command": "docker",
  "args": ["run", "--rm", "-i",
           "-e", "SONARQUBE_TOKEN", "-e", "SONARQUBE_ORG", "-e", "SONARQUBE_PROJECT_KEY",
           "sonarqube-csharp-mcp:latest"],
  "env": {
    "SONARQUBE_TOKEN": "<token>",
    "SONARQUBE_ORG": "<your-org>",
    "SONARQUBE_PROJECT_KEY": "<your-project-key>"
  }
}
```

**Self-hosted SonarQube Server** (no organization; set `SONARQUBE_URL`, omit `SONARQUBE_ORG`):

```jsonc
"sonarqube-csharp": {
  "type": "stdio",
  "command": "docker",
  "args": ["run", "--rm", "-i",
           "-e", "SONARQUBE_TOKEN", "-e", "SONARQUBE_URL", "-e", "SONARQUBE_PROJECT_KEY",
           "sonarqube-csharp-mcp:latest"],
  "env": {
    "SONARQUBE_TOKEN": "<token>",
    "SONARQUBE_URL": "https://sonarqube.example.com",
    "SONARQUBE_PROJECT_KEY": "<your-project-key>"
  }
}
```

## Tool: `analyze_csharp`

| Arg | Required | Description |
|-----|----------|-------------|
| `fileContent` | yes | Complete C# file content to analyze. |
| `projectKey` | no | Project whose quality profile to use. Defaults to `SONARQUBE_PROJECT_KEY`. |
| `organization` | no | SonarCloud org. Defaults to `SONARQUBE_ORG`. Override per call to analyze **any org/project your token can access**; omit for SonarQube Server. |
| `codeSnippet` | no | Only issues overlapping this region are returned. |

`projectKey` and `organization` are per-call, so a single running server can target any
project in any organization the token can reach — the `SONARQUBE_*` env vars are just
defaults. (Use the official SonarQube MCP server's `search_my_sonarqube_projects` to
discover project keys.)

## Environment

| Var | Default | Purpose |
|-----|---------|---------|
| `SONARQUBE_TOKEN` | — | API token, sent as the basic-auth username (works for SonarCloud and SonarQube Server). |
| `SONARQUBE_ORG` | — | Default SonarCloud organization key (overridable per call via the `organization` arg). **Needed for SonarCloud; leave unset for self-hosted SonarQube Server.** |
| `SONARQUBE_URL` | `https://sonarcloud.io` | API base. Point at your server for self-hosted SonarQube. |
| `SONARQUBE_PROJECT_KEY` | — | Default project key. |
| `SONAR_ANALYZER_DIR` | `/app/analyzers` | Where the analyzer DLLs live. |

## License

This project's source is MIT (see `LICENSE`). It bundles third-party components under
their own licenses — notably **SonarAnalyzer.CSharp (LGPL-3.0)**, used unmodified and loaded
as a standalone assembly. See `NOTICE.md`. Not affiliated with or endorsed by SonarSource.
