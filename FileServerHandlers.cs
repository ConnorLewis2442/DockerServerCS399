using System.Reflection.Metadata;
using Azure.Storage.Blobs.Models;
using AzureFileServer.Azure;
using AzureFileServer.Utils;
using Microsoft.Extensions.Primitives;
using AzureFileServer.Auth;

namespace AzureFileServer.FileServer;

public class FileServerHandlers
{
    private readonly IConfiguration _configuration;
    private readonly Logger _logger;
    private readonly CosmosDbWrapper _cosmosDbWrapper;
    private readonly AuthService _authService;

    // Expose CosmosDbWrapper for NotificationService
    public CosmosDbWrapper CosmosDb => _cosmosDbWrapper;

    // Track logged-in users (shared across all handlers)
    private readonly HashSet<string> _loggedInUsers;

    public FileServerHandlers(IConfiguration configuration, AuthService authService, HashSet<string> loggedInUsers)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _authService = authService;
        _loggedInUsers = loggedInUsers;

        string serviceName = configuration["Logging:ServiceName"];
        _logger = new Logger(serviceName);

        _cosmosDbWrapper = new CosmosDbWrapper(configuration);
    }

    // Helper to check if user is logged in
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

    private static string GetParameterFromList(string parameterName, HttpRequest request, MethodLogger log)
    {
        if (request.Query.TryGetValue(parameterName, out StringValues items))
        {
            if (items.Count > 1)
                throw new UserErrorException($"Multiple {parameterName} found");

            log.SetAttribute($"request.{parameterName}", items[0]);
        }
        else
        {
            throw new UserErrorException($"No {parameterName} found");
        }

        return items[0];
    }

    // ---------------- Existing file endpoints ----------------
    public async Task UploadFileDelegate(HttpContext context)
    {
        using var log = _logger.StartMethod(nameof(UploadFileDelegate), context);
        try
        {
            string userid = GetParameterFromList("userid", context.Request, log);
            if (!await EnsureLoggedIn(context, userid)) return;

            IFormFile fileContent = context.Request.Form.Files.FirstOrDefault();
            if (fileContent == null) throw new UserErrorException("No file content found");

            FileMetadata m = new FileMetadata
            {
                SenderId = userid,
                ReceiverId = userid, // For file upload without messaging
                Filename = fileContent.FileName,
                ContentType = fileContent.ContentType,
                ContentLength = fileContent.Length,
                Delivered = true,
                Read = false,
                Timestamp = DateTime.UtcNow
            };

            await _cosmosDbWrapper.AddItemAsync(m, m.ReceiverId);

            var blobStorage = new BlobStorageWrapper(_configuration);
            using var fileStream = fileContent.OpenReadStream();
            await blobStorage.WriteBlob(m.ReceiverId, m.Filename, fileStream);
        }
        catch (Exception e)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"ERROR: {e.Message}\n{e.StackTrace}");
        }
    }

    public async Task DownloadFileDelegate(HttpContext context)
    {
        using var log = _logger.StartMethod(nameof(DownloadFileDelegate), context);
        try
        {
            string userid = GetParameterFromList("userid", context.Request, log);
            if (!await EnsureLoggedIn(context, userid)) return;

            string filename = GetParameterFromList("filename", context.Request, log);

            FileMetadata metaData = await _cosmosDbWrapper.GetItemAsync<FileMetadata>($"{userid}-{filename}", userid);
            if (metaData == null) throw new UserErrorException("No file content found");

            var blobStorage = new BlobStorageWrapper(_configuration);

            context.Response.ContentType = metaData.ContentType;
            context.Response.ContentLength = metaData.ContentLength;

            await blobStorage.DownloadBlob(userid, filename, context.Response.Body);

            if (!metaData.Read)
            {
                metaData.Read = true;
                await _cosmosDbWrapper.UpdateItemAsync(metaData.id, userid, metaData);
            }
        }
        catch (Exception e)
        {
            log.HandleException(e);
        }
    }

    // ---------------- New messaging endpoints ----------------

    // Send a message (text or optional file)
    public async Task SendMessageDelegate(HttpContext context)
    {
        using var log = _logger.StartMethod(nameof(SendMessageDelegate), context);
        try
        {
            string senderId = GetParameterFromList("senderId", context.Request, log);
            string receiverId = GetParameterFromList("receiverId", context.Request, log);

            if (!await EnsureLoggedIn(context, senderId)) return;

            string messageText = context.Request.Form["messageText"];
            IFormFile fileContent = context.Request.Form.Files.FirstOrDefault();

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
        }
        catch (Exception e)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"ERROR: {e.Message}\n{e.StackTrace}");
        }
    }

    // List all messages between two users
    public async Task ListMessagesDelegate(HttpContext context)
    {
        using var log = _logger.StartMethod(nameof(ListMessagesDelegate), context);
        try
        {
            string userId = GetParameterFromList("userId", context.Request, log);
            string conversationWith = GetParameterFromList("conversationWith", context.Request, log);

            if (!await EnsureLoggedIn(context, userId)) return;

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

    // Fetch undelivered messages for a user
    public async Task GetUndeliveredMessagesDelegate(HttpContext context)
    {
        using var log = _logger.StartMethod(nameof(GetUndeliveredMessagesDelegate), context);
        try
        {
            string userId = GetParameterFromList("userId", context.Request, log);
            if (!await EnsureLoggedIn(context, userId)) return;

            string query = $"SELECT * FROM c WHERE c.ReceiverId = '{userId}' AND c.Delivered = false ORDER BY c.Timestamp ASC";
            var messages = await _cosmosDbWrapper.GetItemsAsync<FileMetadata>(query);

            // Mark as delivered
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
