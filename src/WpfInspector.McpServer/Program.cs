using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WpfInspector.Core.Services;
using WpfInspector.McpServer.Session;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<IPeMetadataService, PeMetadataService>();
builder.Services.AddSingleton<IProcessInspectionService>(sp =>
    new ProcessInspectionService(sp.GetRequiredService<IPeMetadataService>()));
builder.Services.AddSingleton<IPdbAnalysisService, PortablePdbAnalysisService>();
builder.Services.AddSingleton<WpfSessionManager>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(Program).Assembly);

var app = builder.Build();
await app.RunAsync();
