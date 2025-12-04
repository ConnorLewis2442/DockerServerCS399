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

        // Map session tokens to username
        var sessions = new Dictionary<string, string>();

        WebApplication app = builder.Build();

        // Middleware to authenticate using session token
        async Task<string?> Authenticate(HttpContext context)
        {
            if (!context.Request.Headers.TryGetValue("Authorization", out var tokenValues))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Missing Authorization header");
                return null;
            }

            string token = tokenValues.First();
            if (!sessions.TryGetValue(token, out var username))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Invalid session token");
                return null;
            }

            return username;
        }

        // ---------------- Messaging endpoints ----------------
        app.MapPost("/sendmessage", async (HttpContext context) =>
        {
            var sender = await Authenticate(context);
            if (sender == null) return;

            await fileServer.SendMessageDelegate(context, sender);
        });

        app.MapGet("/listmessages", async (HttpContext context) =>
        {
            var user = await Authenticate(context);
            if (user == null) return;

            await fileServer.ListMessagesDelegate(context, user);
        });

        app.MapGet("/undelivered", async (HttpContext context) =>
        {
            var user = await Authenticate(context);
            if (user == null) return;

            await fileServer.GetUndeliveredMessagesDelegate(context, user);
        });

        // ---------------- Authentication endpoints ----------------
        app.MapPost("/register", async (HttpContext context) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);
            if (body == null || !body.ContainsKey("username") || !body.ContainsKey("password"))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing username or password");
                return;
            }

            await authService.RegisterUserAsync(body["username"], body["password"]);
            context.Response.StatusCode = 201;
            await context.Response.WriteAsync($"User '{body["username"]}' registered successfully.");
        });

        app.MapPost("/login", async (HttpContext context) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);
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

            loggedInUsers.Add(username);
            string token = Guid.NewGuid().ToString();
            sessions[token] = username;

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync(token);
        });

        app.MapPost("/logout", async (HttpContext context) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);
            if (body == null || !body.ContainsKey("token"))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing token");
                return;
            }

            string token = body["token"];
            if (sessions.TryGetValue(token, out var username))
            {
                sessions.Remove(token);
                loggedInUsers.Remove(username);
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync($"User '{username}' logged out successfully.");
            }
            else
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Invalid token");
            }
        });

        app.Run();
    }
}
