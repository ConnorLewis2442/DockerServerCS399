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
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);
            if (body == null || !body.ContainsKey("receiverId"))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing receiverId in JSON.");
                return;
            }
            receiverId = body["receiverId"];
            messageText = body.ContainsKey("messageText") ? body["messageText"] : "";
        }
        else
        {
            receiverId = context.Request.Form["receiverId"];
            messageText = context.Request.Form["messageText"];
        }

        // PartitionKey must match receiverId
        var msg = new ChatMessage
        {
            senderId = sender,
            receiverId = receiverId,
            messageText = messageText,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

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
