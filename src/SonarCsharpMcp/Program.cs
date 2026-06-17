using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SonarCsharpMcp;

var builder = Host.CreateApplicationBuilder(args);

// The stdio transport speaks JSON-RPC over stdout; every log line MUST go to
// stderr or it corrupts the protocol stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<AnalysisEngine>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
