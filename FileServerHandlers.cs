using System.Reflection.Metadata;
using Azure.Storage.Blobs.Models;
using AzureFileServer.Azure;
using AzureFileServer.Utils;
using Microsoft.Extensions.Primitives;
using AzureFileServer.Auth; // Added for AuthService

namespace AzureFileServer.FileServer;

// This is the core logic of the web server and hosts all of the HTTP
// handlers used by the web server regarding File Server functionality.
public class FileServerHandlers
{
    private readonly IConfiguration _configuration;
    private readonly Logger _logger;
    private readonly CosmosDbWrapper _cosmosDbWrapper;
    private readonly AuthService _authService;

    // Expose CosmosDbWrapper for NotificationService
    public CosmosDbWrapper CosmosDb => _cosmosDbWrapper;

    public FileServerHandlers(IConfiguration configuration, AuthService authService)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _authService = authService;

        string serviceName = configuration["Logging:ServiceName"];
        _logger = new Logger(serviceName);

        _cosmosDbWrapper = new CosmosDbWrapper(configuration);
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
                HttpRequest request = context.Request;
                IFormFile fileContent = request.Form.Files.FirstOrDefault();
                if (fileContent == null)
                    throw new UserErrorException("No file content found");

                FileMetadata m = new FileMetadata
                {
                    userid = GetParameterFromList("userid", request, log),
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
                HttpRequest request = context.Request;

                FileMetadata m = new FileMetadata
                {
                    userid = GetParameterFromList("userid", request, log),
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
            }
        }
    }

    public async Task ListFilesDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(ListFilesDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;
                FileMetadata m = new FileMetadata
                {
                    userid = GetParameterFromList("userid", request, log)
                };

                string query = $"SELECT * FROM c WHERE c.userid = @userid";
                var files = await _cosmosDbWrapper.GetItemsAsync<FileMetadata>(query.Replace("@userid", $"'{m.userid}'"));

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
                HttpRequest request = context.Request;

                FileMetadata m = new FileMetadata
                {
                    userid = GetParameterFromList("userid", request, log),
                    filename = GetParameterFromList("filename", request, log)
                };

                await _cosmosDbWrapper.DeleteItemAsync(m.id, m.userid);

                var blobStorage = new BlobStorageWrapper(_configuration);
                await blobStorage.DeleteBlob(m.userid, m.filename);

                await context.Response.WriteAsync($"Deleted file '{m.filename}' for user '{m.userid}'");
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }
}
