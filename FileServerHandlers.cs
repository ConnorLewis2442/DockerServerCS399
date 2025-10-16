
using System.Reflection.Metadata;
using Azure.Storage.Blobs.Models;
using AzureFileServer.Azure;
using AzureFileServer.Utils;
using Microsoft.Extensions.Primitives;

namespace AzureFileServer.FileServer;

// This is the core logic of the web server and hosts all of the HTTP
// handlers used by the web server regarding File Server functionality.
public class FileServerHandlers
{
    private readonly IConfiguration _configuration;
    private readonly Logger _logger;
    private readonly CosmosDbWrapper _cosmosDbWrapper;

    public FileServerHandlers(IConfiguration configuration)
    {
        _configuration = configuration;
        if (null == _configuration)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        string serviceName = configuration["Logging:ServiceName"];
        _logger = new Logger(serviceName);

        _cosmosDbWrapper = new CosmosDbWrapper(configuration);
    }

    private static string GetParameterFromList(string parameterName, HttpRequest request, MethodLogger log)
    {
        // Obtain the parameter from the caller
        if (request.Query.TryGetValue(parameterName, out StringValues items))
        {
            if (items.Count > 1)
            {
                throw new UserErrorException($"Multiple {parameterName} found");
            }

            log.SetAttribute($"request.{parameterName}", items[0]);
        }
        else
        {
            throw new UserErrorException($"No {parameterName} found");
        }

        return items[0];
    }

    // Health Checks (aka ping) methods are handy to have on your service
    // They allow you to report that your are alive and return any other
    // information that is useful. These are often used by load balancers
    // to decide whether to send you traffic. For example, if you need a long
    // time to initialize, you can report that you are not ready yet.
    public async Task HealthCheckDelegate(HttpContext context)
    {
        // "using" is a C# system to ensure that the object is disposed of properly
        // when the block is exited. In this case, it will call the Dispose method
        using(var log = _logger.StartMethod(nameof(HealthCheckDelegate), context))
        {
            try
            {
                // Generally, a 200 OK is returned if the service is alive
                // and that is all that the load balancer needs, but a
                // text message can be useful for humans.
                // However, in some cases, the LB will be able to process more
                // health information to know how to react to your service, so
                // don't be surprised if you see code with more involved health 
                // checks.
                await context.Response.WriteAsync("Alive");
            }
            catch(Exception e)
            {
                // While you can just throw the exception back to the web server,
                // it is not recommended. It is better to catch the exception and
                // log it, then return a 500 Internal Server Error to the caller yourself.
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

            Console.WriteLine("[DEBUG] UploadFileDelegate called.");

         
            IFormFile fileContent = request.Form.Files.FirstOrDefault();
            if (fileContent == null)
            {
                Console.WriteLine("[ERROR] No file found in request.");
                throw new UserErrorException("No file content found");
            }

            Console.WriteLine($"[DEBUG] Found file: {fileContent.FileName}, Type: {fileContent.ContentType}, Length: {fileContent.Length}");

           FileMetadata m = new FileMetadata();

            m.userid = GetParameterFromList("userid", request, log);
            m.filename = fileContent.FileName;
            m.contenttype = fileContent.ContentType;
            m.contentlength = fileContent.Length;

           
            Console.WriteLine($"[DEBUG] Uploading {m.id}");


            log.SetAttribute("request.filename", fileContent.FileName);
            log.SetAttribute("request.contenttype", fileContent.ContentType);
            log.SetAttribute("request.contentlength", fileContent.Length);

            Console.WriteLine($"[DEBUG] Metadata prepared for user {m.userid}: {System.Text.Json.JsonSerializer.Serialize(m)}");

            try
            {
                var existing = await _cosmosDbWrapper.GetItemAsync<FileMetadata>(m.id, m.userid);
                if (existing != null)
                {
                    Console.WriteLine($"[DEBUG] Existing metadata found, updating {m.id}");
                    await _cosmosDbWrapper.UpdateItemAsync(m.id, m.userid, m);
                }
                else
                {
                    Console.WriteLine($"[DEBUG] Attempting to save new metadata for {m.filename}");
                    await _cosmosDbWrapper.AddItemAsync(m, m.userid);
                    Console.WriteLine("[DEBUG] Metadata saved successfully!");
                }
            }
            catch (Exception cosmosEx)
            {
                Console.WriteLine($"[ERROR] CosmosDB operation failed: {cosmosEx}");
                throw new UserErrorException($"CosmosDB error: {cosmosEx.Message}");
            }

            
            var blobStorage = new BlobStorageWrapper(_configuration);
            using (var fileStream = fileContent.OpenReadStream())
            {
                try
                {
                    Console.WriteLine($"[DEBUG] Writing blob for user={m.userid}, file={m.filename}");
                    await blobStorage.WriteBlob(m.userid, m.filename, fileStream);
                    Console.WriteLine("[DEBUG] Blob written successfully.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Writing blob failed: {ex}");
                    throw new UserErrorException($"Blob write error: {ex.Message}");
                }
            }

            
          
        }
        catch (UserErrorException e)
        {
            Console.WriteLine($"[USER ERROR] {e.Message}");
            log.LogUserError(e.Message);
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
        using(var log = _logger.StartMethod(nameof(DownloadFileDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                FileMetadata m = new FileMetadata();
                m.userid = GetParameterFromList("userid", request, log);
                m.filename = GetParameterFromList("filename", request, log);
             

                // TODO: Implement the download file delegate to return the file
                // contents to the caller via the HTTP response after receiving both
                // the userId and the filename to find.

                FileMetadata metaData = await _cosmosDbWrapper.GetItemAsync<FileMetadata>(m.id, m.userid);

                if (metaData == null)
                    throw new UserErrorException("No file content found");


                var blobStorage = new BlobStorageWrapper(_configuration);

                context.Response.ContentType = metaData.contenttype;
                context.Response.ContentLength = metaData.contentlength;

                await blobStorage.DownloadBlob(m.userid, m.filename, context.Response.Body);


                //await context.Response.WriteAsync("Download Complete");
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

                FileMetadata m = new FileMetadata();
                m.userid = GetParameterFromList("userid", request, log);

                // TODO: Implement the list files delegate to return a list of files
                // that are associated with the userId provided in the HTTP request.

                  string query = $"SELECT * FROM c WHERE c.userid = @userid";
                  var files = await _cosmosDbWrapper.GetItemsAsync<FileMetadata>(query.Replace("@userid", $"'{m.userid}'"));

                 context.Response.ContentType = "application/json";
                 await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(files));
               //throw new NotImplementedException();
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

                FileMetadata m = new FileMetadata();
                m.userid = GetParameterFromList("userid", request, log);
                m.filename = GetParameterFromList("filename", request, log);

                // TODO: Implement the delete file delegate to remove the file
                // from the storage system and the metadata from the CosmosDB database.
                log.SetAttribute("request.userid", m.userid);
                log.SetAttribute("request.filename", m.filename);

                //delete cosmos db blob with meta data
                await _cosmosDbWrapper.DeleteItemAsync(m.id, m.userid);
                log.SetAttribute("cosmosdb.deleted", m.id);

                //delete actual blob
                var blobStorage = new BlobStorageWrapper(_configuration);
                bool deleted = await blobStorage.DeleteBlob(m.userid, m.filename);
                log.SetAttribute("blob.deleted", deleted);

             

                await context.Response.WriteAsync($"Deleted file '{m.filename}' for user '{m.userid}'");
                //throw new NotImplementedException();
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }
}