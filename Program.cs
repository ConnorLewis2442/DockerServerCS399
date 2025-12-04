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

        var app = builder.Build();

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
        app.MapPost("/sendmessage", async (HttpContext context) =>
        {
            var senderId = context.Request.Form["senderId"].ToString();
            if (string.IsNullOrEmpty(senderId) || !await EnsureLoggedIn(context, senderId))
                return;

            await fileServer.SendMessageDelegate(context, senderId);
        });

        app.MapGet("/listmessages", async (HttpContext context) =>
        {
            var userId = context.Request.Query["userId"].ToString();
            if (string.IsNullOrEmpty(userId) || !await EnsureLoggedIn(context, userId))
                return;

            await fileServer.ListMessagesDelegate(context, userId);
        });

        app.MapGet("/undelivered", async (HttpContext context) =>
        {
            var userId = context.Request.Query["userId"].ToString();
            if (string.IsNullOrEmpty(userId) || !await EnsureLoggedIn(context, userId))
                return;

            await fileServer.GetUndeliveredMessagesDelegate(context, userId);
        });

        // ---------------- Authentication endpoints ----------------
        var sessions = new Dictionary<string, string>();

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
