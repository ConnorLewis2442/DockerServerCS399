using AzureFileServer.FileServer;   // for FileServerHandlers
using AzureFileServer.Auth;         // for AuthService
using AzureFileServer.Azure;        // for CosmosDbWrapper
using AzureFileServer.Notification; // for NotificationService
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Linq;


WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Configuration
IConfiguration configuration = builder.Configuration;

// AuthService setup
string usersFile = Path.Combine(AppContext.BaseDirectory, "users.json");
var authService = new AuthService(usersFile);

// FileServerHandlers setup
var fileServerHandlers = new FileServerHandlers(configuration, authService);

WebApplication app = builder.Build();

// Map routes
app.MapPost("/uploadfile", fileServerHandlers.UploadFileDelegate);
app.MapGet("/downloadfile", fileServerHandlers.DownloadFileDelegate);
app.MapGet("/listfiles", fileServerHandlers.ListFilesDelegate);
app.MapGet("/deletefile", fileServerHandlers.DeleteFileDelegate);
app.MapDelete("/deletefile", fileServerHandlers.DeleteFileDelegate);
app.MapGet("/healthcheck", fileServerHandlers.HealthCheckDelegate);

// NotificationService (no change)
var notifService = new AzureFileServer.Notification.NotificationService(new CosmosDbWrapper(configuration), configuration);
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
        string contentString = m.metadata.contenttype.StartsWith("text/")
            ? System.Text.Encoding.UTF8.GetString(m.content)
            : Convert.ToBase64String(m.content);

        return new
        {
            metadata = m.metadata,
            content = contentString
        };
    });

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(output));
});

app.Run();
