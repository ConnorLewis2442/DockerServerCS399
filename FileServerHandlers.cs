using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;

public class FileServerHandlers
{
    private readonly Container users;
    private readonly Container messages;
    private readonly Dictionary<string, string> sessions;

    public FileServerHandlers(Container users, Container messages, Dictionary<string, string> sessions)
    {
        this.users = users;
        this.messages = messages;
        this.sessions = sessions;
    }

   public async Task SendMessageDelegate(HttpContext context, string sender)
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
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    await messages.CreateItemAsync(msg, new PartitionKey(receiverId));

    context.Response.StatusCode = 200;
    await context.Response.WriteAsync("Message sent.");
}



    public async Task GetUndeliveredDelegate(HttpContext context, string receiver)
    {
        receiver = receiver.Trim().ToLower();

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
