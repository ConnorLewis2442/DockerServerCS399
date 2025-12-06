using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using Azure.Storage.Blobs;
using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using User = AzureFileServer.Auth.User; // resolve ambiguity

public class FileServerHandlers
{
    private readonly Container messages;
    private readonly BlobContainerClient blobContainer;

    public FileServerHandlers(Container messages, BlobContainerClient blobContainer)
    {
        this.messages = messages;
        this.blobContainer = blobContainer;
    }

    // ----------------------
    // Helper: Check if a user exists
    // ----------------------
    private async Task<bool> UserExists(string username)
    {
        var blobClient = blobContainer.GetBlobClient("users.json");
        var download = await blobClient.DownloadContentAsync();
        var usersJson = download.Value.Content.ToString();
        var users = JsonSerializer.Deserialize<List<User>>(usersJson);
        return users != null && users.Any(u => u.Username.Equals(username, System.StringComparison.OrdinalIgnoreCase));
    }

    // ----------------------
    // Send a message
    // ----------------------
    public async Task SendMessageDelegate(HttpContext context, string sender)
    {
        try
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body);
            var bodyString = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            if (string.IsNullOrWhiteSpace(bodyString))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Empty request body");
                return;
            }

            var body = JsonSerializer.Deserialize<Dictionary<string, string>>(bodyString);
            if (body == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Invalid JSON");
                return;
            }

            if (!body.TryGetValue("receiverId", out var receiverId) || string.IsNullOrWhiteSpace(receiverId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing receiverId in JSON.");
                return;
            }

            // Check if receiver exists
            receiverId = receiverId.Trim().ToLower();
            if (!await UserExists(receiverId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync($"User '{receiverId}' does not exist. Check spelling and try again.");
                return;
            }

            body.TryGetValue("messageText", out var messageText);
            sender = sender.Trim().ToLower();

            var msg = new ChatMessage
            {
                senderId = sender,
                receiverId = receiverId,
                messageText = messageText,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                isDelivered = false,
                isRead = false
            };

            await messages.CreateItemAsync(msg, new PartitionKey(receiverId));
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("Message sent.");
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"ERROR: {ex}");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Internal Server Error");
        }
    }

    // ----------------------
    // Get undelivered messages
    // ----------------------
   public async Task GetUndeliveredDelegate(HttpContext context, string receiver, string username)
{
    receiver = receiver.Trim().ToLower();
    username = username.Trim().ToLower();

    // Make sure the logged-in user can only see their own messages
    if (receiver != username) 
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("You can only view your own messages");
        return;
    }

    var q = new QueryDefinition("SELECT * FROM c WHERE c.receiverId = @r AND c.isDelivered = false")
        .WithParameter("@r", receiver);

    var iterator = messages.GetItemQueryIterator<ChatMessage>(q);
    List<ChatMessage> results = new();

    while (iterator.HasMoreResults)
    {
        var batch = await iterator.ReadNextAsync();
        results.AddRange(batch);
    }

    foreach (var msg in results)
    {
        msg.isDelivered = true;
        await messages.UpsertItemAsync(msg, new PartitionKey(msg.receiverId));
    }

    var output = results.Select(m => new
    {
        senderId = m.senderId,
        messageText = m.messageText,
        isDelivered = m.isDelivered
    }).ToList();

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonSerializer.Serialize(output));
}


    // ----------------------
    // Get message history
    // ----------------------
    public async Task GetMessageHistoryDelegate(HttpContext context, string user1, string user2)
    {
        user1 = user1.Trim().ToLower();
        user2 = user2.Trim().ToLower();

        if (!await UserExists(user2))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync($"User '{user2}' does not exist. Check spelling and try again.");
            return;
        }

        var q = new QueryDefinition(
            "SELECT * FROM c WHERE (c.senderId = @u1 AND c.receiverId = @u2) OR (c.senderId = @u2 AND c.receiverId = @u1) ORDER BY c.timestamp ASC")
            .WithParameter("@u1", user1)
            .WithParameter("@u2", user2);

        var iterator = messages.GetItemQueryIterator<ChatMessage>(q);
        List<ChatMessage> results = new();

        while (iterator.HasMoreResults)
        {
            var batch = await iterator.ReadNextAsync();
            results.AddRange(batch);
        }

        var output = results.Select(m => new
        {
            senderId = m.senderId,
            messageText = m.messageText,
            isDelivered = m.isDelivered
        }).ToList();

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(output));
    }
}

// ----------------------
// Chat message model
// ----------------------
public class ChatMessage
{
    public string id { get; set; } = System.Guid.NewGuid().ToString();
    public string senderId { get; set; }
    public string receiverId { get; set; }
    public string messageText { get; set; }
    public long timestamp { get; set; }
    public bool isDelivered { get; set; }
    public bool isRead { get; set; }
}
