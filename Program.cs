using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Telemetry.Trace;
using AzureFileServer.FileServer;
using AzureFileServer.Azure;
using AzureFileServer.Notification;
using AzureFileServer.Auth;

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

        // Initialize AuthService with path to users.json (in project root or deployed path)
        string usersFilePath = Path.Combine(AppContext.BaseDirectory, "users.json");
        var authService = new AuthService(usersFilePath);

        // Initialize FileServerHandlers with configuration and AuthService
        var fileServer = new FileServerHandlers(configuration, authService);

        WebApplication app = builder.Build();

        // Map endpoints
        app.MapGet("/healthcheck", fileServer.HealthCheckDelegate);
        app.MapGet("/downloadfile", fileServer.DownloadFileDelegate);
        app.MapGet("/listfiles", fileServer.ListFilesDelegate);
        app.MapGet("/deletefile", fileServer.DeleteFileDelegate);
        app.MapDelete("/deletefile", fileServer.DeleteFileDelegate);
        app.MapPost("/uploadfile", fileServer.UploadFileDelegate);

        // Initialize NotificationService with the existing CosmosDbWrapper
        var notifService = new AzureFileServer.Notification.NotificationService(fileServer.CosmosDb, configuration);

        app.MapGet("/undelivered", async (HttpContext context) =>
        {
            var request = context.Request;
            if (!request.Query.TryGetValue("userid", out var userId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing userid parameter");
                return;
            }

            var messages = await notifService.PushUndeliveredMessagesWithContent(userId);

            // Convert to JSON-friendly object
            var output = messages.Select(m =>
            {
                string contentString;
                if (m.metadata.contenttype.StartsWith("text/"))
                {
                    contentString = System.Text.Encoding.UTF8.GetString(m.content);
                }
                else
                {
                    contentString = Convert.ToBase64String(m.content);
                }

                return new
                {
                    metadata = m.metadata,
                    content = contentString
                };
            });

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(output));
        });

        // Start the server
        app.Run();
    }
}
