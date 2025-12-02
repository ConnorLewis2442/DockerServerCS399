using AzureFileServer.Azure;
using AzureFileServer.FileServer;

namespace AzureFileServer.Notification
{
    public class NotificationService
    {
        private readonly CosmosDbWrapper _cosmosDbWrapper;

        public NotificationService(CosmosDbWrapper cosmosDbWrapper)
        {
            _cosmosDbWrapper = cosmosDbWrapper;
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
        /// Check undelivered messages and "push" them to the client.
        /// For now, this just returns them; in a real app, you'd send via WebSocket/SignalR/etc.
        /// </summary>
        public async Task<List<FileMetadata>> PushUndeliveredMessages(string userId)
        {
            var undelivered = await GetUndeliveredMessages(userId);
            foreach (var msg in undelivered)
            {
                // simulate push
                Console.WriteLine($"[PUSH] Sending message '{msg.filename}' to {userId}");

                // mark as delivered
                await MarkAsDelivered(msg);
            }
            return undelivered;
        }
    }

        public async Task<IEnumerable<FileMetadata>> PushUndeliveredMessages(string userid)
        {
            var query = $"SELECT * FROM c WHERE c.userid = @userid AND c.delivered = false";
            var undelivered = await _cosmosDbWrapper.GetItemsAsync<FileMetadata>(query.Replace("@userid", $"'{userid}'"));
        
            foreach (var msg in undelivered)
            {
                // Example: just log for now or push via WebSocket later
                Console.WriteLine($"Pushing {msg.filename} to {userid}");
        
                // mark as delivered
                msg.delivered = true;
                await _cosmosDbWrapper.UpdateItemAsync(msg.id, msg.userid, msg);
            }
        
            return undelivered;
        }

}
