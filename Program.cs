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

        // Blob storage and AuthService
        var blobStorage = new BlobStorageWrapper(configuration);
        var authService = new AuthService(blobStorage);

        // FileServerHandlers
        var loggedInUsers = new HashSet<string>();
        var fileServer = new FileServerHandlers(configuration, authService,loggedInUsers);

        WebApplication app = builder.Build();

        // Middleware to check login for file endpoints
        async Task<bool> EnsureLoggedIn(HttpContext context, string userid)
        {
            if (!loggedInUsers.Contains(userid))
            {
                context.Response.StatusCode = 403; // Forbidden
                await context.Response.WriteAsync("User not logged in");
                return false;
            }
            return true;
        }

        // File server endpoints
        app.MapGet("/healthcheck", fileServer.HealthCheckDelegate);

        app.MapPost("/uploadfile", async (HttpContext context) =>
        {
            string userid = context.Request.Query["userid"];
            if (string.IsNullOrEmpty(userid) || !await EnsureLoggedIn(context, userid))
                return;

            await fileServer.UploadFileDelegate(context);
        });

        app.MapGet("/downloadfile", async (HttpContext context) =>
        {
            string userid = context.Request.Query["userid"];
            if (string.IsNullOrEmpty(userid) || !await EnsureLoggedIn(context, userid))
                return;

            await fileServer.DownloadFileDelegate(context);
        });

        app.MapGet("/listfiles", async (HttpContext context) =>
        {
            string userid = context.Request.Query["userid"];
            if (string.IsNullOrEmpty(userid) || !await EnsureLoggedIn(context, userid))
                return;

            await fileServer.ListFilesDelegate(context);
        });

        app.MapGet("/deletefile", async (HttpContext context) =>
        {
            string userid = context.Request.Query["userid"];
            if (string.IsNullOrEmpty(userid) || !await EnsureLoggedIn(context, userid))
                return;

            await fileServer.DeleteFileDelegate(context);
        });

        app.MapDelete("/deletefile", async (HttpContext context) =>
        {
            string userid = context.Request.Query["userid"];
            if (string.IsNullOrEmpty(userid) || !await EnsureLoggedIn(context, userid))
                return;

            await fileServer.DeleteFileDelegate(context);
        });

        // Notification service endpoint
        var notifService = new NotificationService(fileServer.CosmosDb, configuration);
        app.MapGet("/undelivered", async (HttpContext context) =>
        {
            string userid = context.Request.Query["userid"];
            if (string.IsNullOrEmpty(userid) || !await EnsureLoggedIn(context, userid))
                return;

            var messages = await notifService.PushUndeliveredMessagesWithContent(userid);
            var output = messages.Select(m =>
            {
                string contentString = m.metadata.contenttype.StartsWith("text/")
                    ? System.Text.Encoding.UTF8.GetString(m.content)
                    : Convert.ToBase64String(m.content);
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

        // Login endpoint
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

                // Mark user as logged in
                loggedInUsers.Add(username);

                context.Response.StatusCode = 200;
                await context.Response.WriteAsync($"User '{username}' logged in successfully.");
            }
            catch (Exception e)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync($"Error: {e.Message}");
            }
        });

        // Logout endpoint
        app.MapPost("/logout", async (HttpContext context) =>
        {
            var requestBody = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);

            if (requestBody == null || !requestBody.ContainsKey("username"))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing username in request body");
                return;
            }

            string username = requestBody["username"];
            loggedInUsers.Remove(username);

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync($"User '{username}' logged out successfully.");
        });

        // List users endpoint (for debugging)
        app.MapGet("/users", async (HttpContext context) =>
        {
            var users = await authService.GetUsersAsync();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(users));
        });

        app.Run();
    }
}
