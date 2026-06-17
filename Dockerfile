# --- build ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer-cached on csproj changes only).
COPY src/SonarCsharpMcp/SonarCsharpMcp.csproj src/SonarCsharpMcp/
RUN dotnet restore src/SonarCsharpMcp/SonarCsharpMcp.csproj

COPY src/ src/
RUN dotnet publish src/SonarCsharpMcp/SonarCsharpMcp.csproj -c Release -o /app --no-restore

# --- runtime -------------------------------------------------------------
# In-process Roslyn means no SDK is needed at runtime — just the .NET runtime.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV SONARQUBE_URL=https://sonarcloud.io \
    SONAR_ANALYZER_DIR=/app/analyzers \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# stdio MCP server: JSON-RPC over stdin/stdout.
ENTRYPOINT ["dotnet", "SonarCsharpMcp.dll"]
