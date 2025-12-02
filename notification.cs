using AzureFileServer.Azure;
using AzureFileServer.FileServer;

namespace AzureFileServer.Notification
{
    public class NotificationService
    {
        private readonly CosmosDbWrapper _cosmosDbWrapper;
        private readonly IConfiguration _configuration;

        public NotificationService(CosmosDbWrapper cosmosDbWrapper, IConfiguration configuration)
        {
            _cosmosDbWrapper = cosmosDbWrapper;
            _configuration = configuration;
        }

        /// <summary>
        /// Fetch all undelivered messages for a user.
        /// </summary>
        public async Task<List<FileMetadata>> GetUndeliveredMessages(string userId)
        {
            string query = $"SELECT * FROM c WHERE c.userid = @userid AND c.delivered = false";
            var messages = await _cosmosDbWrapper.GetItemsAsync<FileMetadata>(query.Replace("@userid", $"'{userId}'"));
            return messages.ToList();
        }

        /// <summary>
        /// Mark a message as delivered.
        /// </summary>
        public async Task MarkAsDelivered(FileMetadata message)
        {
            message.delivered = true;
            await _cosmosDbWrapper.UpdateItemAsync(message.id, message.userid, message);
        }

        /// <summary>
        /// Push undelivered messages including file content (Base64 encoded).
        /// </summary>
        public async Task<List<(FileMetadata metadata, byte[] content)>> PushUndeliveredMessagesWithContent(string userId)
        {
            var undelivered = await GetUndeliveredMessages(userId);
            var blobStorage = new BlobStorageWrapper(_configuration);
            var result = new List<(FileMetadata, byte[])>();

            foreach (var msg in undelivered)
            {
                using var ms = new MemoryStream();
                await blobStorage.DownloadBlob(msg.userid, msg.filename, ms);
                byte[] content = ms.ToArray();

                Console.WriteLine($"[PUSH] Sending message '{msg.filename}' to {userId}");

                // Mark as delivered
                await MarkAsDelivered(msg);

                result.Add((msg, content));
            }

            return result;
        }
    }
}
