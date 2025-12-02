using AzureFileServer.Azure;
using AzureFileServer.FileServer;
using System.Text;

namespace AzureFileServer.Notification
{
    public class NotificationService
    {
        private readonly CosmosDbWrapper _cosmosDbWrapper;
        private readonly IConfiguration _configuration;

        public NotificationService(CosmosDbWrapper cosmosDbWrapper, IConfiguration configuration)
        {
            _cosmosDbWrapper = cosmosDbWrapper ?? throw new ArgumentNullException(nameof(cosmosDbWrapper));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        
        // Fetch all undelivered messages for a user.
        public async Task<List<FileMetadata>> GetUndeliveredMessages(string userId)
        {
            string query = $"SELECT * FROM c WHERE c.userid = @userid AND c.delivered = false";
            var messages = await _cosmosDbWrapper.GetItemsAsync<FileMetadata>(query.Replace("@userid", $"'{userId}'"));
            return messages.ToList();
        }

        // mark as delivered
        public async Task MarkAsDelivered(FileMetadata message)
        {
            message.delivered = true;
            await _cosmosDbWrapper.UpdateItemAsync(message.id, message.userid, message);
        }

        // Push undelivered messages 
        public async Task<List<(FileMetadata metadata, byte[] content)>> PushUndeliveredMessagesWithContent(string userId)
        {
            var undelivered = await GetUndeliveredMessages(userId);
            var blobStorage = new BlobStorageWrapper(_configuration);
            var result = new List<(FileMetadata, string)>();

            foreach (var msg in undelivered)
            {
                using var ms = new MemoryStream();
                await blobStorage.DownloadBlob(msg.userid, msg.filename, ms);
                byte[] content = ms.ToArray();

                Console.WriteLine($"[PUSH] Sending message '{msg.filename}' to {userId}");

                // Mark as delivered
                await MarkAsDelivered(msg);

                // Encode content to Base64 for JSON-safe transmission
                string base64Content = Convert.ToBase64String(content);

                result.Add((msg, base64Content));
            }

            return result;
        }
    }
}
