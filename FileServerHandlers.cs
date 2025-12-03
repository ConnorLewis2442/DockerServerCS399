using System.Reflection.Metadata;
using Azure.Storage.Blobs.Models;
using AzureFileServer.Azure;
using AzureFileServer.Utils;
using AzureFileServer.Auth; // new
using Microsoft.Extensions.Primitives;
using AzureFileServer.Auth;

namespace AzureFileServer.FileServer;

public class FileServerHandlers
{
    private readonly IConfiguration _configuration;
    private readonly Logger _logger;
    private readonly CosmosDbWrapper _cosmosDbWrapper;
    private readonly AuthService _authService;

    public FileServerHandlers(IConfiguration configuration, AuthService authService)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

        string serviceName = configuration["Logging:ServiceName"];
        _logger = new Logger(serviceName);

        _cosmosDbWrapper = new CosmosDbWrapper(configuration);
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
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

    // **New helper to enforce auth**
    private void Authenticate(HttpRequest request)
    {
        if (!request.Query.TryGetValue("username", out var username) ||
            !request.Query.TryGetValue("password", out var password) ||
            !_authService.ValidateUser(username, password))
        {
            throw new UserErrorException("Invalid or missing credentials");
        }
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
        Authenticate(context.Request); // auth first

        using (var log = _logger.StartMethod(nameof(UploadFileDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;
                IFormFile fileContent = request.Form.Files.FirstOrDefault();
                if (fileContent == null)
                    throw new UserErrorException("No file content found");

                FileMetadata m = new FileMetadata
                {
                    userid = request.Query["username"], // enforce auth username
                    filename = fileContent.FileName,
                    contenttype = fileContent.ContentType,
                    contentlength = fileContent.Length,
                    delivered = false,
                    read = false,
                    timestamp = DateTime.UtcNow
                };

                await _cosmosDbWrapper.AddItemAsync(m, m.userid);

                var blobStorage = new BlobStorageWrapper(_configuration);
                using var fileStream = fileContent.OpenReadStream();
                await blobStorage.WriteBlob(m.userid, m.filename, fileStream);
            }
            catch (UserErrorException e)
            {
                log.LogUserError(e.Message);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync(e.Message);
            }
            catch (Exception e)
            {
                log.HandleException(e);
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"ERROR: {e.Message}\n{e.StackTrace}");
            }
        }
    }

    public async Task DownloadFileDelegate(HttpContext context)
    {
        Authenticate(context.Request);

        using(var log = _logger.StartMethod(nameof(DownloadFileDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                FileMetadata m = new FileMetadata
                {
                    userid = request.Query["username"],
                    filename = GetParameterFromList("filename", request, log)
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
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"ERROR: {e.Message}");
            }
        }
    }

    public async Task ListFilesDelegate(HttpContext context)
    {
        Authenticate(context.Request);

        using(var log = _logger.StartMethod(nameof(ListFilesDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;
                string userid = request.Query["username"];

                string query = $"SELECT * FROM c WHERE c.userid = @userid";
                var files = await _cosmosDbWrapper.GetItemsAsync<FileMetadata>(query.Replace("@userid", $"'{userid}'"));

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(files));
            }
            catch(Exception e)
            {
                log.HandleException(e);
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"ERROR: {e.Message}");
            }
        }
    }

    public async Task DeleteFileDelegate(HttpContext context)
    {
        Authenticate(context.Request);

        using(var log = _logger.StartMethod(nameof(DeleteFileDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;
                string userid = request.Query["username"];
                string filename = GetParameterFromList("filename", request, log);

                FileMetadata m = new FileMetadata { userid = userid, filename = filename };

                // delete Cosmos DB metadata
                await _cosmosDbWrapper.DeleteItemAsync(m.id, m.userid);
                log.SetAttribute("cosmosdb.deleted", m.id);

                // delete blob
                var blobStorage = new BlobStorageWrapper(_configuration);
                bool deleted = await blobStorage.DeleteBlob(m.userid, m.filename);
                log.SetAttribute("blob.deleted", deleted);

                await context.Response.WriteAsync($"Deleted file '{m.filename}' for user '{m.userid}'");
            }
            catch(Exception e)
            {
                log.HandleException(e);
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"ERROR: {e.Message}");
            }
        }
    }
}
