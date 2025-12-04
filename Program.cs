using Microsoft.AspNetCore.Http.Json;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Allow large JSON
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

var app = builder.Build();

// Cosmos setup
string connectionString = builder.Configuration["CosmosDb:ConnectionString"];
CosmosClient client = new CosmosClient(connectionString);
Database db = await client.CreateDatabaseIfNotExistsAsync("MessagingDB");
Container users = await db.CreateContainerIfNotExistsAsync("Users", "/id");
Container messages = await db.CreateContainerIfNotExistsAsync("Messages", "/receiverId");

// FileServerHandlers
var fileServer = new FileServerHandlers(messages);

// ============================
// SEND MESSAGE (no token required)
// ============================
app.MapPost("/sendmessage", async (HttpContext context) =>
{
    // Read form data (works with Windows CMD curl -F)
    var form = await context.Request.ReadFormAsync();
    string sender = form["senderId"];
    string receiver = form["receiverId"];
    string messageText = form["messageText"];

    if (string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(receiver))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Missing senderId or receiverId");
        return;
    }

    await fileServer.SendMessageDelegate(context, sender, receiver, messageText);
});

// ============================
// GET UNDELIVERED
// ============================
app.MapGet("/undelivered", async (HttpContext context) =>
{
    string receiver = context.Request.Query["receiverId"];
    if (string.IsNullOrEmpty(receiver))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Missing receiverId");
        return;
    }

    await fileServer.GetUndeliveredDelegate(context, receiver);
});

app.Run();

// ==== MODELS ====
public class ChatMessage
{
    public string id { get; set; } = Guid.NewGuid().ToString();
    public string senderId { get; set; }
    public string receiverId { get; set; }
    public string messageText { get; set; }
    public long timestamp { get; set; }
}

// ============================
// FILE SERVER HANDLERS
// ============================
public class FileServerHandlers
{
    private readonly Container messages;

    public FileServerHandlers(Container messages)
    {
        this.messages = messages;
    }

    public async Task SendMessageDelegate(HttpContext context, string sender, string receiver, string messageText)
    {
        var msg = new ChatMessage
        {
            senderId = sender,
            receiverId = receiver,
            messageText = messageText,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Use receiverId as partition key
        await messages.CreateItemAsync(msg, new PartitionKey(receiver));

        await context.Response.WriteAsync("Message sent successfully.");
    }

    public async Task GetUndeliveredDelegate(HttpContext context, string receiver)
    {
        var q = new QueryDefinition("SELECT * FROM c WHERE c.receiverId = @r")
            .WithParameter("@r", receiver);

        var iterator = messages.GetItemQueryIterator<ChatMessage>(q);
        List<ChatMessage> results = new();

        while (iterator.HasMoreResults)
        {
            var batch = await iterator.ReadNextAsync();
            results.AddRange(batch);
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(results));
    }
}
