using Microsoft.AspNetCore.Http.Json;
using Microsoft.Azure.Cosmos;
using Azure.Storage.Blobs;
using System.Text.Json;
using System.Collections.Concurrent;
using AzureFileServer.Auth;
using AzureFileServer.Azure;

var builder = WebApplication.CreateBuilder(args);

// Allow large JSON payloads
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

var app = builder.Build();

// ----------------------
// Middleware to attach username from token
// ----------------------
app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
    {
        var token = authHeader.ToString().Replace("Bearer ", "").Trim();
        if (sessions.TryGetValue(token, out var username))
        {
            context.Items["Username"] = username; // store for handlers
        }
    }
    await next();
});


// ----------------------
// In-memory session store
// ----------------------
var sessions = new ConcurrentDictionary<string, string>(); // token -> username

// ----------------------
// Cosmos DB setup
// ----------------------
string cosmosConnection = Environment.GetEnvironmentVariable("cosmosdb-connection");
CosmosClient client = new CosmosClient(cosmosConnection);
Database db = await client.CreateDatabaseIfNotExistsAsync("MessagingDB");
Container messages = await db.CreateContainerIfNotExistsAsync(
    id: "Messaged",
    partitionKeyPath: "/receiverId",
    throughput: 400
);

// ----------------------
// Blob storage setup
// ----------------------
string blobConnection = Environment.GetEnvironmentVariable("blob-connection-string");
BlobContainerClient blobContainer = new BlobContainerClient(blobConnection, "users"); 
var fileServer = new FileServerHandlers(messages, blobContainer);

// Wrap configuration for AuthService
var blobWrapper = new BlobStorageWrapper(builder.Configuration);

// ----------------------
// REGISTER endpoint
// ----------------------
app.MapPost("/register", async context =>
{
    using var reader = new StreamReader(context.Request.Body);
    var bodyStr = await reader.ReadToEndAsync();
    var register = JsonSerializer.Deserialize<LoginRequest>(bodyStr);

    if (register == null || string.IsNullOrWhiteSpace(register.username) || string.IsNullOrWhiteSpace(register.password))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, message = "Invalid JSON" }));
        return;
    }

    try
    {
        var authService = new AuthService(blobWrapper);
        await authService.RegisterUserAsync(register.username.Trim(), register.password.Trim());

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = true, message = "User registered successfully" }));
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 400;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, message = ex.Message }));
    }
});

// ----------------------
// LOGIN endpoint
// ----------------------
app.MapPost("/login", async context =>
{
    using var reader = new StreamReader(context.Request.Body);
    var bodyStr = await reader.ReadToEndAsync();
    var login = JsonSerializer.Deserialize<LoginRequest>(bodyStr);

    if (login == null)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid JSON");
        return;
    }

    var authService = new AuthService(blobWrapper);
    if (!await authService.ValidateUserAsync(login.username, login.password))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Invalid credentials");
        return;
    }

    var token = Guid.NewGuid().ToString();
    sessions[token] = login.username;

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonSerializer.Serialize(new { token }));
});

// ----------------------
// SEND MESSAGE endpoint
// ----------------------
app.MapPost("/sendmessage", async (HttpContext context) =>
{
    if (!context.Items.TryGetValue("Username", out var userObj) || userObj == null)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized");
        return;
    }
    string username = userObj.ToString();
    await fileServer.SendMessageDelegate(context, username);
});



// ----------------------
// GET UNDELIVERED endpoint
// ----------------------
app.MapGet("/undelivered", async (HttpContext context) =>
{
    if (!context.Items.TryGetValue("Username", out var userObj) || userObj == null)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized");
        return;
    }
    string username = userObj.ToString();
    var receiverQuery = context.Request.Query["receiver"];
    if (string.IsNullOrEmpty(receiverQuery))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Missing 'receiver' query parameter");
        return;
    }
    await fileServer.GetUndeliveredDelegate(context, receiverQuery, username);
});


// ----------------------
// HISTORY endpoint
// ----------------------
app.MapGet("/history", async (HttpContext context) =>
{
    if (!context.Items.TryGetValue("Username", out var userObj) || userObj == null)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized");
        return;
    }
    string username = userObj.ToString();
    var withUser = context.Request.Query["with"].ToString()?.Trim().ToLower();
    if (string.IsNullOrEmpty(withUser))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Missing 'with' query parameter");
        return;
    }
    await fileServer.GetMessageHistoryDelegate(context, username, withUser);
});



// ----------------------
// TEST endpoint
// ----------------------
app.MapGet("/test", async context =>
{
    await context.Response.WriteAsync("This is the NEW version running");
});

app.Run();

// ----------------------
// Models
// ----------------------
public record LoginRequest(string username, string password);
