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

    public FileServerHandlers(IConfiguration configuration, AuthService authService)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));

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

    private async Task ValidateUser(HttpRequest request, MethodLogger log)
    {
        string username = GetParameterFromList("username", request, log);
        string password = GetParameterFromList("password", request, log);

        if (!await _authService.ValidateUserAsync(username, password))
            throw new UserErrorException("Invalid username or password");
    }

    // Example: modify UploadFileDelegate to require user auth
    public async Task UploadFileDelegate(HttpContext context)
    {
        using var log = _logger.StartMethod(nameof(UploadFileDelegate), context);
        try
        {
            await ValidateUser(context.Request, log);

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
            using var fileStream = fileContent.OpenReadStream();
            await blobStorage.WriteBlob(m.userid, m.filename, fileStream);
        }
        catch (UserErrorException e)
        {
            log.LogUserError(e.Message);
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync(e.Message);
        }
        catch (Exception e)
        {
            log.HandleException(e);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"ERROR: {e.Message}\n{e.StackTrace}");
        }
    }

    // TODO: Apply ValidateUser(...) to other delegates like DownloadFileDelegate, ListFilesDelegate, DeleteFileDelegate
}
