using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

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
    string receiverId = "";
    string messageText = "";

    if (context.Request.HasJsonContentType())
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var bodyString = await reader.ReadToEndAsync();
            var body = JsonSerializer.Deserialize<Dictionary<string, string>>(bodyString);

            if (body == null || !body.TryGetValue("receiverId", out receiverId) || string.IsNullOrWhiteSpace(receiverId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing receiverId in JSON.");
                return;
            }

            body.TryGetValue("messageText", out messageText);
        }
        catch
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid JSON.");
            return;
        }
    }
    else
    {
        // FORM DATA (legacy)
        receiverId = context.Request.Form["receiverId"];
        messageText = context.Request.Form["messageText"];
    }

    // Trim to ensure PartitionKey matches
    receiverId = receiverId.Trim();

    var msg = new ChatMessage
    {
        senderId = sender,
        receiverId = receiverId,
        messageText = messageText,
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    // PartitionKey MUST match the exact receiverId
    await messages.CreateItemAsync(msg, new PartitionKey(receiverId));

    await context.Response.WriteAsync("Message sent.");
}


    await messages.CreateItemAsync(msg, new PartitionKey(receiverId));

    await context.Response.WriteAsync("Message sent.");
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
