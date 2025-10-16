using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Telemetry.Trace;
using AzureFileServer.FileServer;

namespace AzureFileServer;

class Program
{
    static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Load configuration from appsettings.json
        IConfiguration configuration = builder.Configuration;

        string serviceName = configuration["Logging:ServiceName"];
        string serviceVersion = configuration["Logging:ServiceVersion"];

        // OpenTelemetry tracing setup
        builder.Services.AddOpenTelemetry().WithTracing(tcb =>
        {
            tcb
            .AddSource(serviceName)
            .SetResourceBuilder(
                ResourceBuilder.CreateDefault()
                    .AddService(serviceName: serviceName, serviceVersion: serviceVersion))
            .AddAspNetCoreInstrumentation()
            .AddJsonConsoleExporter();
        });

        // Initialize FileServerHandlers (which internally uses CosmosDbWrapper)
        FileServerHandlers instance = new FileServerHandlers(configuration);

        WebApplication app = builder.Build();

        // Map endpoints
        app.MapGet("/healthcheck", instance.HealthCheckDelegate);
        app.MapGet("/downloadfile", instance.DownloadFileDelegate);
        app.MapGet("/listfiles", instance.ListFilesDelegate);
        app.MapGet("/deletefile", instance.DeleteFileDelegate);
        app.MapDelete("/deletefile", instance.DeleteFileDelegate);
        app.MapPost("/uploadfile", instance.UploadFileDelegate);

        // Start the server
        app.Run();
    }
}