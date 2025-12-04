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

    public async Task HealthCheckDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(HealthCheckDelegate), context))
        {
            try
            {
                await context.Response.WriteAsync("Alive");
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    public async Task UploadFileDelegate(HttpContext context)
    {
        using (var log = _logger.StartMethod(nameof(UploadFileDelegate), context))
        {
            try
            {
                string userid = GetParameterFromList("userid", context.Request, log);
                if (!await EnsureLoggedIn(context, userid))
                    return;

                IFormFile fileContent = context.Request.Form.Files.FirstOrDefault();
                if (fileContent == null)
                    throw new UserErrorException("No file content found");

                FileMetadata m = new FileMetadata
                {
                    userid = userid,
                    filename = fileContent.FileName,
                    contenttype = fileContent.ContentType,
                    contentlength = fileContent.Length,
                    delivered = false,
                    read = false,
                    timestamp = DateTime.UtcNow
                };

                await _cosmosDbWrapper.AddItemAsync(m, m.userid);

                var blobStorage = new BlobStorageWrapper(_configuration);
                using (var fileStream = fileContent.OpenReadStream())
                {
                    await blobStorage.WriteBlob(m.userid, m.filename, fileStream);
                }
            }
            catch (UserErrorException e)
            {
                Console.WriteLine($"[USER ERROR] {e.Message}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[ERROR] {e.Message}");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"ERROR: {e.Message}\n{e.StackTrace}");
            }
        }
    }

    public async Task DownloadFileDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(DownloadFileDelegate), context))
        {
            try
            {
                string userid = GetParameterFromList("userid", context.Request, log);
                if (!await EnsureLoggedIn(context, userid))
                    return;

                FileMetadata m = new FileMetadata
                {
                    userid = userid,
                    filename = GetParameterFromList("filename", context.Request, log)
                };

                FileMetadata metaData = await _cosmosDbWrapper.GetItemAsync<FileMetadata>(m.id, m.userid);
                if (metaData == null)
                    throw new UserErrorException("No file content found");

                var blobStorage = new BlobStorageWrapper(_configuration);

                context.Response.ContentType = metaData.contenttype;
                context.Response.ContentLength = metaData.contentlength;

                await blobStorage.DownloadBlob(m.userid, m.filename, context.Response.Body);

                if(!metaData.read)
                {
                    metaData.read = true;
                    await _cosmosDbWrapper.UpdateItemAsync(m.id, m.userid, metaData);
                }
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    public async Task ListFilesDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(ListFilesDelegate), context))
        {
            try
            {
                string userid = GetParameterFromList("userid", context.Request, log);
                if (!await EnsureLoggedIn(context, userid))
                    return;

                string query = $"SELECT * FROM c WHERE c.userid = @userid";
                var files = await _cosmosDbWrapper.GetItemsAsync<FileMetadata>(query.Replace("@userid", $"'{userid}'"));

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(files));
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    public async Task DeleteFileDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(DeleteFileDelegate), context))
        {
            try
            {
                string userid = GetParameterFromList("userid", context.Request, log);
                if (!await EnsureLoggedIn(context, userid))
                    return;

                string filename = GetParameterFromList("filename", context.Request, log);

                await _cosmosDbWrapper.DeleteItemAsync(filename, userid);

                var blobStorage = new BlobStorageWrapper(_configuration);
                await blobStorage.DeleteBlob(userid, filename);

                await context.Response.WriteAsync($"Deleted file '{filename}' for user '{userid}'");
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }
}
