using Microsoft.AspNetCore.Http.Json;
using Microsoft.Azure.Cosmos;
using System.Text.Json;
using AzureFileServer.FileServer;

var builder = WebApplication.CreateBuilder(args);

// Allow large JSON
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

var app = builder.Build();

// In-memory session tokens
Dictionary<string, string> sessions = new();

// Cosmos setup
string connectionString = builder.Configuration["CosmosDb:ConnectionString"];
CosmosClient client = new CosmosClient(connectionString);
Database db = await client.CreateDatabaseIfNotExistsAsync("MessagingDB");
Container users = await db.CreateContainerIfNotExistsAsync("Users", "/id");
Container messages = await db.CreateContainerIfNotExistsAsync("Messages", "/receiverId");

// FileServerHandlers
var fileServer = new FileServerHandlers(users, messages, sessions);

// LOGIN
app.MapPost("/login", async ctx =>
{
    var login = await JsonSerializer.DeserializeAsync<LoginRequest>(ctx.Request.Body);
    if (login == null)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Invalid JSON.");
        return;
    }

    var q = new QueryDefinition("SELECT * FROM c WHERE c.id = @id AND c.password = @pw")
        .WithParameter("@id", login.username)
        .WithParameter("@pw", login.password);

    var result = users.GetItemQueryIterator<User>(q);
    var userList = await result.ReadNextAsync();
    if (!userList.Any())
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsync("Invalid credentials");
        return;
    }

    // Make token
    string token = Guid.NewGuid().ToString();
    sessions[token] = login.username;

    await ctx.Response.WriteAsync(token);
});

// SEND MESSAGE
app.MapPost("/sendmessage", async (HttpContext context) =>
{
    // ------------------ OPTION: Disable token check for testing ------------------
    string sender = "bob"; // Default sender for testing
    // ------------------ To use real token, uncomment this:
    /*
    if (!context.Request.Headers.TryGetValue("Authorization", out var token))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Missing token");
        return;
    }

    if (!sessions.TryGetValue(token, out sender))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Invalid token");
        return;
    }
    */
    await fileServer.SendMessageDelegate(context, sender);
});

// GET UNDELIVERED
app.MapGet("/undelivered", async (HttpContext context) =>
{
    string receiver = "alice"; // For testing
    await fileServer.GetUndeliveredDelegate(context, receiver);
});

app.Run();


// ==== MODELS ====
public record LoginRequest(string username, string password);

public class User
{
    public string id { get; set; }
    public string password { get; set; }
}

public class ChatMessage
{
    public string id { get; set; } = Guid.NewGuid().ToString();
    public string senderId { get; set; }
    public string receiverId { get; set; }
    public string messageText { get; set; }
    public long timestamp { get; set; }
}
