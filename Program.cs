using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Telemetry.Trace;
using AzureFileServer.FileServer;
using AzureFileServer.Azure;
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

        // OpenTelemetry tracing
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

        var blobStorage = new BlobStorageWrapper(configuration);
        var authService = new AuthService(blobStorage);

        var loggedInUsers = new HashSet<string>();
        var fileServer = new FileServerHandlers(configuration, authService, loggedInUsers);

        WebApplication app = builder.Build();

        // Middleware to check login
        async Task<bool> EnsureLoggedIn(HttpContext context, string userid)
        {
            if (!loggedInUsers.Contains(userid))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("User not logged in");
                return false;
            }
            return true;
        }

        // ---------------- Messaging endpoints ----------------
        app.MapPost("/sendmessage", fileServer.SendMessageDelegate);
        app.MapGet("/listmessages", fileServer.ListMessagesDelegate);
        app.MapGet("/undelivered", fileServer.GetUndeliveredMessagesDelegate);

        // ---------------- Authentication endpoints ----------------
        app.MapPost("/register", async (HttpContext context) =>
        {
            try
            {
                var body = await System.Text.Json.JsonSerializer
                    .DeserializeAsync<Dictionary<string, string>>(context.Request.Body);

                if (body == null || !body.ContainsKey("username") || !body.ContainsKey("password"))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Missing username or password in request body");
                    return;
                }

                await authService.RegisterUserAsync(body["username"], body["password"]);
                context.Response.StatusCode = 201;
                await context.Response.WriteAsync($"User '{body["username"]}' registered successfully.");
            }
            catch (Exception e)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync($"Error: {e.Message}");
            }
        });

        app.MapPost("/login", async (HttpContext context) =>
        {
            try
            {
                var body = await System.Text.Json.JsonSerializer
                    .DeserializeAsync<Dictionary<string, string>>(context.Request.Body);

                if (body == null || !body.ContainsKey("username") || !body.ContainsKey("password"))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Missing username or password in request body");
                    return;
                }

                bool valid = await authService.ValidateUserAsync(body["username"], body["password"]);

                if (!valid)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Invalid username or password");
                    return;
                }

                loggedInUsers.Add(body["username"]);
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync($"User '{body["username"]}' logged in successfully.");
            }
            catch (Exception e)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync($"Error: {e.Message}");
            }
        });

        app.MapPost("/logout", async (HttpContext context) =>
        {
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);
            if (body == null || !body.ContainsKey("username"))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing username in request body");
                return;
            }

            loggedInUsers.Remove(body["username"]);
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync($"User '{body["username"]}' logged out successfully.");
        });

        // ---------------- Debugging ----------------
        app.MapGet("/users", async (HttpContext context) =>
        {
            var users = await authService.GetUsersAsync();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(users));
        });

        app.Run();
    }
}
