namespace AzureFileServer.FileServer
{
    public class FileMetadata
    {
        // Cosmos DB partition key must match EXACTLY: /receiverId
        public string receiverId { get; set; } = string.Empty;   // lowercase required
        public string senderId { get; set; } = string.Empty;     // lowercase to match JSON

        // Stable ID generator
        private string GenerateId()
        {
            return $"{senderId}-{receiverId}-{filename}";
        }

        public string id => GenerateId();

        // Message data
        public string messageText { get; set; } = string.Empty;
        public bool delivered { get; set; } = false;
        public bool read { get; set; } = false;
        public DateTime timestamp { get; set; } = DateTime.UtcNow;

        // File metadata
        public string filename { get; set; } = string.Empty;
        public string contentType { get; set; } = string.Empty;
        public long contentLength { get; set; } = 0;
        public string content { get; set; } = string.Empty;

        // -------- Compatibility layer (uppercase names) ------------
        // These allow your existing code to still compile, but Cosmos ignores them

        public string SenderId { get => senderId; set => senderId = value; }
        public string ReceiverId { get => receiverId; set => receiverId = value; }

        public string Filename { get => filename; set => filename = value; }
        public string ContentType { get => contentType; set => contentType = value; }
        public long ContentLength { get => contentLength; set => contentLength = value; }

        public bool Delivered { get => delivered; set => delivered = value; }
        public bool Read { get => read; set => read = value; }
        public DateTime Timestamp { get => timestamp; set => timestamp = value; }
        public string MessageText { get => messageText; set => messageText = value; }

        // Cosmos partition key field (must be lowercase!)
        public string PartitionKey => receiverId;

        public override string ToString()
        {
            return $"id: {id}, PK: {PartitionKey}, senderId: {senderId}, receiverId: {receiverId}, filename: {filename}, delivered: {delivered}, read: {read}, timestamp: {timestamp}, messageText: {messageText}";
        }
    }
}
