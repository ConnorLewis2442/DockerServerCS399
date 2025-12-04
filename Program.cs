using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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

        // Dictionary to map session token -> username
        var sessions = new Dictionary<string, string>();

        // Helper: check if user is logged in via token
        async Task<string?> GetLoggedInUser(HttpContext context)
        {
            if (!context.Request.Headers.TryGetValue("Authorization", out var token))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Missing Authorization token");
                return null;
            }

            if (!sessions.ContainsKey(token))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Invalid or expired token");
                return null;
            }

            return sessions[token];
        }

        // ---------------- Messaging endpoints ----------------
        app.MapPost("/sendmessage", async (HttpContext context) =>
        {
            var senderId = await GetLoggedInUser(context);
            if (senderId == null) return;

            await fileServer.SendMessageDelegate(context, senderId);
        });

        app.MapGet("/listmessages", async (HttpContext context) =>
        {
            var userId = await GetLoggedInUser(context);
            if (userId == null) return;

            await fileServer.ListMessagesDelegate(context, userId);
        });

        app.MapGet("/undelivered", async (HttpContext context) =>
        {
            var userId = await GetLoggedInUser(context);
            if (userId == null) return;

            await fileServer.GetUndeliveredMessagesDelegate(context, userId);
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

            // Add to logged-in users and generate token
            loggedInUsers.Add(username);
            string token = Guid.NewGuid().ToString();
            sessions[token] = username;

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync(token); // client will use this token in Authorization header
        });

        app.MapPost("/logout", async (HttpContext context) =>
        {
            var username = await GetLoggedInUser(context);
            if (username == null) return;

            loggedInUsers.Remove(username);
            string token = context.Request.Headers["Authorization"];
            sessions.Remove(token);

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync($"User '{username}' logged out successfully.");
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
