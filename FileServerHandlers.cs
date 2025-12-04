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

    // ----------------------
    // Send a message
    // ----------------------
    public async Task SendMessageDelegate(HttpContext context)
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

            // Deserialize JSON
            Dictionary<string, string>? body = JsonSerializer.Deserialize<Dictionary<string, string>>(bodyString);
            if (body == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Invalid JSON");
                return;
            }

            // Get sender
            if (!body.TryGetValue("senderId", out var sender) || string.IsNullOrWhiteSpace(sender))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing senderId in JSON.");
                return;
            }

            // Get receiver
            if (!body.TryGetValue("receiverId", out var receiverId) || string.IsNullOrWhiteSpace(receiverId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing receiverId in JSON.");
                return;
            }

            // Get message text (optional)
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

            Console.WriteLine($"DEBUG: Creating message from {sender} to {receiverId} with text: {messageText}");
            await messages.CreateItemAsync(msg, new PartitionKey(receiverId));
            Console.WriteLine("DEBUG: Message created successfully");

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
    // Get undelivered messages
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

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(results));
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
