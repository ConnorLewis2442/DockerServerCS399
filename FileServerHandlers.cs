using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;

public class FileServerHandlers
{
    private readonly Container messages;

    public FileServerHandlers(Container messages)
    {
        this.messages = messages;
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

            body.TryGetValue("messageText", out var messageText);

            sender = sender.Trim().ToLower();
            receiverId = receiverId.Trim().ToLower();

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
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex}");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Internal Server Error");
        }
    }

    // ----------------------
    // Get undelivered messages (clean output)
    // ----------------------
    public async Task GetUndeliveredDelegate(HttpContext context, string receiver)
    {
        receiver = receiver.Trim().ToLower();

        var q = new QueryDefinition("SELECT * FROM c WHERE c.receiverId = @r AND c.isDelivered = false")
            .WithParameter("@r", receiver);

        var iterator = messages.GetItemQueryIterator<ChatMessage>(q);
        List<ChatMessage> results = new();

        while (iterator.HasMoreResults)
        {
            var batch = await iterator.ReadNextAsync();
            results.AddRange(batch);
        }

        // Mark fetched messages as delivered
        foreach (var msg in results)
        {
            msg.isDelivered = true;
            await messages.UpsertItemAsync(msg, new PartitionKey(msg.receiverId));
        }

        // Clean output
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
    // Optional: Get message history with another user
    // ----------------------
    public async Task GetMessageHistoryDelegate(HttpContext context, string user1, string user2)
    {
        user1 = user1.Trim().ToLower();
        user2 = user2.Trim().ToLower();

        var q = new QueryDefinition(
            "SELECT * FROM c WHERE (c.senderId = @u1 AND c.receiverId = @u2) OR (c.senderId = @u2 AND c.receiverId = @u1)")
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
    public string id { get; set; } = Guid.NewGuid().ToString();
    public string senderId { get; set; }
    public string receiverId { get; set; }
    public string messageText { get; set; }
    public long timestamp { get; set; }
    public bool isDelivered { get; set; }
    public bool isRead { get; set; }
}
