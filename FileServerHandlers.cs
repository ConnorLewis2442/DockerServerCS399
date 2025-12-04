using AzureFileServer.Azure;
using AzureFileServer.Auth;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging;

namespace AzureFileServer.FileServer;

public class UserErrorException : Exception
{
    public UserErrorException(string message) : base(message) { }
}

public class FileServerHandlers
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<FileServerHandlers> _logger;
    private readonly CosmosDbWrapper _cosmosDbWrapper;
    private readonly AuthService _authService;
    private readonly HashSet<string> _loggedInUsers;

    public CosmosDbWrapper CosmosDb => _cosmosDbWrapper;

    public FileServerHandlers(IConfiguration configuration, AuthService authService, HashSet<string> loggedInUsers, ILogger<FileServerHandlers> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _authService = authService;
        _loggedInUsers = loggedInUsers;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cosmosDbWrapper = new CosmosDbWrapper(configuration);
    }

    private async Task<bool> EnsureLoggedIn(HttpContext context, string userId)
    {
        if (!_loggedInUsers.Contains(userId))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("User not logged in");
            return false;
        }
        return true;
    }

    private static string GetParameter(HttpRequest request, string parameterName)
    {
        if (request.Query.TryGetValue(parameterName, out StringValues queryValues) && queryValues.Count > 0)
            return queryValues[0];

        if (request.HasFormContentType && request.Form.TryGetValue(parameterName, out var formValues) && formValues.Count > 0)
            return formValues[0];

        throw new UserErrorException($"No {parameterName} found in query or form");
    }

    // ---------------- Messaging ----------------
    public async Task SendMessageDelegate(HttpContext context, string senderId)
    {
        try
        {
            if (!await EnsureLoggedIn(context, senderId)) return;

            string receiverId = GetParameter(context.Request, "receiverId");
            string messageText = context.Request.HasFormContentType ? context.Request.Form["messageText"].ToString() : string.Empty;
            IFormFile? fileContent = context.Request.HasFormContentType ? context.Request.Form.Files.FirstOrDefault() : null;

            var message = new FileMetadata
            {
                SenderId = senderId,
                ReceiverId = receiverId,
                Timestamp = DateTime.UtcNow,
                Delivered = false,
                Read = false,
                MessageText = messageText ?? string.Empty
            };

            if (fileContent != null)
            {
                message.Filename = fileContent.FileName;
                message.ContentType = fileContent.ContentType;
                message.ContentLength = fileContent.Length;

                var blobStorage = new BlobStorageWrapper(_configuration);
                using var stream = fileContent.OpenReadStream();
                await blobStorage.WriteBlob(receiverId, message.Filename, stream);
            }

            await _cosmosDbWrapper.AddItemAsync(message, receiverId);

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("Message sent successfully");
            _logger.LogInformation("Message sent from {SenderId} to {ReceiverId}", senderId, receiverId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "SendMessageDelegate failed");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"ERROR: {e.Message}");
        }
    }

    public async Task ListMessagesDelegate(HttpContext context, string userId)
    {
        try
        {
            if (!await EnsureLoggedIn(context, userId)) return;

            string conversationWith = GetParameter(context.Request, "conversationWith");

            string query = $@"
                SELECT * FROM c 
                WHERE (c.SenderId = '{userId}' AND c.ReceiverId = '{conversationWith}')
                   OR (c.SenderId = '{conversationWith}' AND c.ReceiverId = '{userId}')
                ORDER BY c.Timestamp ASC";

            var messages = await _cosmosDbWrapper.GetItemsAsync<FileMetadata>(query);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(messages));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "ListMessagesDelegate failed");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"ERROR: {e.Message}");
        }
    }

    public async Task GetUndeliveredMessagesDelegate(HttpContext context, string userId)
    {
        try
        {
            if (!await EnsureLoggedIn(context, userId)) return;

            string query = $"SELECT * FROM c WHERE c.ReceiverId = '{userId}' AND c.Delivered = false ORDER BY c.Timestamp ASC";
            var messages = await _cosmosDbWrapper.GetItemsAsync<FileMetadata>(query);

            foreach (var msg in messages)
            {
                msg.Delivered = true;
                await _cosmosDbWrapper.UpdateItemAsync(msg.id, userId, msg);
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(messages));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "GetUndeliveredMessagesDelegate failed");
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"ERROR: {e.Message}");
        }
    }
}
