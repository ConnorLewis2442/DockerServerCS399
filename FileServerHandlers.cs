using AzureFileServer.Azure;
using AzureFileServer.Auth;
using Microsoft.Extensions.Primitives;

namespace AzureFileServer.FileServer;

public class FileServerHandlers
{
    private readonly IConfiguration _configuration;
    private readonly Logger _logger;
    private readonly CosmosDbWrapper _cosmosDbWrapper;
    private readonly AuthService _authService;
    private readonly HashSet<string> _loggedInUsers;

    public CosmosDbWrapper CosmosDb => _cosmosDbWrapper;

    public FileServerHandlers(IConfiguration configuration, AuthService authService, HashSet<string> loggedInUsers)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _authService = authService;
        _loggedInUsers = loggedInUsers;

        string serviceName = configuration["Logging:ServiceName"];
        _logger = new Logger(serviceName);

        _cosmosDbWrapper = new CosmosDbWrapper(configuration);
    }

    private async Task<bool> EnsureLoggedIn(HttpContext context, string userid)
    {
        if (!_loggedInUsers.Contains(userid))
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
        using var log = _logger.StartMethod(nameof(SendMessageDelegate), context);
        try
        {
            if (!await EnsureLoggedIn(context, senderId)) return;

            string receiverId = GetParameter(context.Request, "receiverId");
            string messageText = context.Request.HasFormContentType ? context.Request.Form["messageText"].ToString() : string.Empty;
            IFormFile? fileContent = context.Request.HasFormContentType ? context.Request.Form.Files.FirstOrDefault() : null;

            FileMetadata m = new FileMetadata
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
                m.Filename = fileContent.FileName;
                m.ContentType = fileContent.ContentType;
                m.ContentLength = fileContent.Length;

                var blobStorage = new BlobStorageWrapper(_configuration);
                using var fileStream = fileContent.OpenReadStream();
                await blobStorage.WriteBlob(receiverId, m.Filename, fileStream);
            }

            await _cosmosDbWrapper.AddItemAsync(m, receiverId);

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("Message sent successfully");
        }
        catch (Exception e)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"ERROR: {e.Message}\n{e.StackTrace}");
        }
    }

    public async Task ListMessagesDelegate(HttpContext context, string userId)
    {
        using var log = _logger.StartMethod(nameof(ListMessagesDelegate), context);
        try
        {
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
            log.HandleException(e);
        }
    }

    public async Task GetUndeliveredMessagesDelegate(HttpContext context, string userId)
    {
        using var log = _logger.StartMethod(nameof(GetUndeliveredMessagesDelegate), context);
        try
        {
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
            log.HandleException(e);
        }
    }
}
