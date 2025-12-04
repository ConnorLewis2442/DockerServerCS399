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

// Cosmos setup (existing container, don't create)
string cosmosConnection = Environment.GetEnvironmentVariable("cosmosdb-connection");
CosmosClient client = new CosmosClient(cosmosConnection);
Container messages = client.GetContainer("MessagingDB", "Messages");

// Blob setup for users
string blobConnection = Environment.GetEnvironmentVariable("blob-connection-string");
BlobContainerClient blobContainer = new BlobContainerClient(blobConnection, "users"); // use correct container name
var fileServer = new FileServerHandlers(messages);

// LOGIN endpoint (reads from users.json in blob)
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

    var blobClient = blobContainer.GetBlobClient("users.json"); // only the blob name
    var download = await blobClient.DownloadContentAsync();
    var usersJson = download.Value.Content.ToString();
    var users = JsonSerializer.Deserialize<List<User>>(usersJson);

    if (users == null || !users.Any(u => u.Username == login.username && u.Password == login.password))
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsync("Invalid credentials");
        return;
    }

    await ctx.Response.WriteAsync("Logged in");
});

// SEND MESSAGE endpoint
app.MapPost("/sendmessage", async (HttpContext context) =>
{
    await fileServer.SendMessageDelegate(context, "_");
});

// GET UNDELIVERED endpoint
app.MapGet("/undelivered", async (HttpContext context) =>
{
    string receiver = "alice"; // hardcoded for testing
    await fileServer.GetUndeliveredDelegate(context, receiver);
});

// TEST endpoint
app.MapGet("/test", async ctx =>
{
    await ctx.Response.WriteAsync("This is the NEW version running");
});

app.Run();

// Models
public record LoginRequest(string username, string password);
public class User
{
    public string Username { get; set; }
    public string Password { get; set; }
}
