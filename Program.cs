using AzureFileServer.FileServer;
using AzureFileServer.Azure;
using AzureFileServer.Auth;
using Microsoft.Extensions.Logging;

namespace AzureFileServer;

class Program
{
    static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var configuration = builder.Configuration;

        // Setup logging
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        var loggerFactory = builder.Services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<FileServerHandlers>();

        var blobStorage = new BlobStorageWrapper(configuration);
        var authService = new AuthService(blobStorage);

        var loggedInUsers = new HashSet<string>();
        var fileServer = new FileServerHandlers(configuration, authService, loggedInUsers, logger);

        var sessions = new Dictionary<string, string>();

        var app = builder.Build();

        // ---------------- Messaging endpoints ----------------
        app.MapPost("/sendmessage", async (HttpContext context) =>
        {
            if (!context.Request.Headers.TryGetValue("Authorization", out var token))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Missing token");
                return;
            }

            if (!sessions.TryGetValue(token, out string username))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Invalid token");
                return;
            }

            await fileServer.SendMessageDelegate(context, username);
        });

        app.MapGet("/listmessages", async (HttpContext context) =>
        {
            if (!context.Request.Headers.TryGetValue("Authorization", out var token))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Missing token");
                return;
            }

            if (!sessions.TryGetValue(token, out string username))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Invalid token");
                return;
            }

            await fileServer.ListMessagesDelegate(context, username);
        });

        app.MapGet("/undelivered", async (HttpContext context) =>
        {
            if (!context.Request.Headers.TryGetValue("Authorization", out var token))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Missing token");
                return;
            }

            if (!sessions.TryGetValue(token, out string username))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Invalid token");
                return;
            }

            await fileServer.GetUndeliveredMessagesDelegate(context, username);
        });

        // ---------------- Authentication endpoints ----------------
        app.MapPost("/register", async (HttpContext context) =>
        {
            try
            {
                var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);

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
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);
            if (body == null || !body.ContainsKey("username") || !body.ContainsKey("password"))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing username or password");
                return;
            }

            string username = body["username"];
            string password = body["password"];

            bool valid = await authService.ValidateUserAsync(username, password);
            if (!valid)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Invalid username or password");
                return;
            }

            // Add to logged-in users
            loggedInUsers.Add(username);

            // Generate a session token
            string token = Guid.NewGuid().ToString();
            sessions[token] = username;

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync(token);
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

        app.MapGet("/users", async (HttpContext context) =>
        {
            var users = await authService.GetUsersAsync();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(users));
        });

        app.Run();
    }
}
