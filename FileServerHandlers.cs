using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;

public class FileServerHandlers
{
    private readonly Container messages;

    public FileServerHandlers(Container messages)
    {
        this.messages = messages;
    }

    // Hardcoded sender for testing
    public async Task SendMessageDelegate(HttpContext context, string _)
    {
        string sender = "alice"; // hardcoded for testing

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

        Dictionary<string, string>? body;
        try
        {
            body = JsonSerializer.Deserialize<Dictionary<string, string>>(bodyString);
        }
        catch
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid JSON");
            return;
        }

        if (body == null || !body.TryGetValue("receiverId", out var receiverId) || string.IsNullOrWhiteSpace(receiverId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing receiverId in JSON.");
            return;
        }

        body.TryGetValue("messageText", out var messageText);

        receiverId = receiverId.Trim().ToLower();
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

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(results));
    }
}

// Updated ChatMessage model
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
