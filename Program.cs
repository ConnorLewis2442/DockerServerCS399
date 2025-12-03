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

        IConfiguration configuration = builder.Configuration;

        string serviceName = configuration["Logging:ServiceName"];
        string serviceVersion = configuration["Logging:ServiceVersion"];

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

        // Blob storage and AuthService
        var blobStorage = new BlobStorageWrapper(configuration);
        var authService = new AuthService(blobStorage);

        // FileServerHandlers
        var fileServer = new FileServerHandlers(configuration, authService);

        WebApplication app = builder.Build();

        // File server endpoints
        app.MapGet("/healthcheck", fileServer.HealthCheckDelegate);
        app.MapGet("/downloadfile", fileServer.DownloadFileDelegate);
        app.MapGet("/listfiles", fileServer.ListFilesDelegate);
        app.MapGet("/deletefile", fileServer.DeleteFileDelegate);
        app.MapDelete("/deletefile", fileServer.DeleteFileDelegate);
        app.MapPost("/uploadfile", fileServer.UploadFileDelegate);

        // Notification service
        var notifService = new NotificationService(fileServer.CosmosDb, configuration);
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

            var output = messages.Select(m =>
            {
                string contentString;
                if (m.metadata.contenttype.StartsWith("text/"))
                    contentString = System.Text.Encoding.UTF8.GetString(m.content);
                else
                    contentString = Convert.ToBase64String(m.content);

                return new { metadata = m.metadata, content = contentString };
            });

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(output));
        });

        // Registration endpoint
        app.MapPost("/register", async (HttpContext context) =>
        {
            try
            {
                var requestBody = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);

                if (requestBody == null || !requestBody.ContainsKey("username") || !requestBody.ContainsKey("password"))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Missing username or password in request body");
                    return;
                }

                string username = requestBody["username"];
                string password = requestBody["password"];

                await authService.RegisterUserAsync(username, password);

                context.Response.StatusCode = 201;
                await context.Response.WriteAsync($"User '{username}' registered successfully.");
            }
            catch (Exception e)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync($"Error: {e.Message}");
            }
        });

        // List users endpoint
        app.MapGet("/users", async (HttpContext context) =>
        {
            var users = await authService.GetUsersAsync();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(users));
        });

        app.MapPost("/login", async (HttpContext context) =>
        {
            try
            {
                var requestBody = await System.Text.Json.JsonSerializer
                    .DeserializeAsync<Dictionary<string, string>>(context.Request.Body);
        
                if (requestBody == null || !requestBody.ContainsKey("username") || !requestBody.ContainsKey("password"))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Missing username or password in request body");
                    return;
                }
        
                string username = requestBody["username"];
                string password = requestBody["password"];
        
                bool valid = await authService.ValidateUserAsync(username, password);
        
                if (!valid)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Invalid username or password");
                    return;
                }
        
                // Success: respond with 200 OK
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync($"User '{username}' logged in successfully.");
            }
            catch (Exception e)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync($"Error: {e.Message}");
            }
        });


        app.Run();
    }
}
