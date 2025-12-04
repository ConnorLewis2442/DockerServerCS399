using Microsoft.AspNetCore.Http.Json;
using Microsoft.Azure.Cosmos;
using Azure.Storage.Blobs;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Allow large JSON
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

var app = builder.Build();

// ----------------------
// Cosmos DB setup
// ----------------------
string cosmosConnection = Environment.GetEnvironmentVariable("cosmosdb-connection");
CosmosClient client = new CosmosClient(cosmosConnection);

// Use existing DB and container
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
var fileServer = new FileServerHandlers(messages);

// ----------------------
// LOGIN endpoint
// ----------------------
app.MapPost("/login", async ctx =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var bodyStr = await reader.ReadToEndAsync();
    var login = JsonSerializer.Deserialize<LoginRequest>(bodyStr);

    if (login == null)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Invalid JSON");
        return;
    }

    var blobClient = blobContainer.GetBlobClient("users.json");
    var download = await blobClient.DownloadContentAsync();
    var usersJson = download.Value.Content.ToString();
    var users = JsonSerializer.Deserialize<List<User>>(usersJson);

    if (users == null || !users.Any(u => u.Username == login.username && u.Password == login.password))
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsync("Invalid credentials");
        return;
    }

    // Return the username as "proof of login"
    await ctx.Response.WriteAsync(login.username);
});

// ----------------------
// SEND MESSAGE endpoint
// ----------------------
app.MapPost("/sendmessage", async (HttpContext context) =>
{
    await fileServer.SendMessageDelegate(context, null); // senderId will come from JSON
});

// ----------------------
// GET UNDELIVERED endpoint
// ----------------------
app.MapGet("/undelivered", async (HttpContext context) =>
{
    var receiverQuery = context.Request.Query["receiver"];
    if (string.IsNullOrEmpty(receiverQuery))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Missing 'receiver' query parameter");
        return;
    }

    await fileServer.GetUndeliveredDelegate(context, receiverQuery);
});

// ----------------------
// TEST endpoint
// ----------------------
app.MapGet("/test", async ctx =>
{
    await ctx.Response.WriteAsync("This is the NEW version running");
});

app.Run();

// ----------------------
// Models
// ----------------------
public record LoginRequest(string username, string password);
public class User
{
    public string Username { get; set; }
    public string Password { get; set; }
}
