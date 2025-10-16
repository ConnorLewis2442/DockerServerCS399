using Azure.Identity;
using Azure.Storage.Blobs;

using AzureFileServer.Utils;

namespace AzureFileServer.Azure;

// A very simple wrapper to make it easier to call Azure Storage Blob APIs
public class BlobStorageWrapper
{
    private readonly Logger _logger;
    private readonly BlobServiceClient _client;

    public BlobStorageWrapper(IConfiguration configuration)
    {
        if (null == configuration)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        string serviceName = configuration["Logging:ServiceName"];
        _logger = new Logger(serviceName);

        string blobEndpoint = configuration["AzureFileServer:ConnectionStrings:BlobStorageEndpoint"];
        string blobConnectioonString = "DefaultEndpointsProtocol=https;AccountName=blobstorage399;AccountKey=MEhoHND0Tn7HB6bNG8u7JzT65ihAhT3pv+Tp2dNrtWgv1uHeyY3x4cx20obZKBdsDA8peQKh+Xlt+AStLyUk9g==;EndpointSuffix=core.windows.net";
        if (string.IsNullOrEmpty(blobConnectioonString))
        {
            _client = new BlobServiceClient(new Uri(blobEndpoint), new DefaultAzureCredential());
        }
        else
        {
            _client = new BlobServiceClient(blobConnectioonString);
        }
    }

    public async Task WriteBlob(string containerName, string filename, Stream blobContentStream)
    {
        if (string.IsNullOrEmpty(containerName))
        {
            throw new ArgumentException("Container name cannot be null or empty", nameof(containerName));
        }
        if (string.IsNullOrEmpty(filename))
        {
            throw new ArgumentException("Filename cannot be null or empty", nameof(filename));
        }
        if (null == blobContentStream)
        {
            throw new ArgumentException("Blob Content cannot be null", nameof(blobContentStream));
        }

        using (var log = _logger.StartMethod(nameof(WriteBlob)))
        {
            log.SetAttribute("containerName", containerName);
            log.SetAttribute("filename", filename);

            // First step is to ensure the container exists - this is sometimes a pain with
            // create failing if it already exists, or get failing if it doesn't. So we force the 
            // issue with CreateIfNotExistsAsync().
            await _client.GetBlobContainerClient(containerName).CreateIfNotExistsAsync();
            BlobContainerClient container = _client.GetBlobContainerClient(containerName);
            BlobClient blob = container.GetBlobClient(filename);

            // We set the overwrite flag to true, so that if the blob already exists, it will be overwritten.
            await blob.UploadAsync(blobContentStream, true);
        }
    }

    public async Task DownloadBlob(string containerName, string filename, Stream uploadStream)
    {
        if (string.IsNullOrEmpty(containerName))
        {
            throw new ArgumentException("Container name cannot be null or empty", nameof(containerName));
        }
        if (string.IsNullOrEmpty(filename))
        {
            throw new ArgumentException("Filename cannot be null or empty", nameof(filename));
        }

        using (var log = _logger.StartMethod(nameof(DownloadBlob)))
        {
            log.SetAttribute("containerName", containerName);
            log.SetAttribute("filename", filename);

            BlobContainerClient container = _client.GetBlobContainerClient(containerName);
            BlobClient blob = container.GetBlobClient(filename);

            await blob.DownloadToAsync(uploadStream);
        }
    }
}