namespace AzureFileServer.FileServer
{
    // This class is used to store metadata about a file or message,
    // and can be serialized/deserialized for CosmosDb storage
    public class FileMetadata
    {
        // Generate a unique ID per message/file
        private string GenerateId()
        {
            // ID includes sender and receiver for uniqueness
            return $"{SenderId}-{ReceiverId}-{Filename}-{Timestamp.Ticks}";
        }

        // Cosmos DB requires lowercase "id"
        public string id { get { return GenerateId(); } }

        // Sender and receiver 
        public string SenderId { get; set; } = string.Empty;
        public string ReceiverId { get; set; } = string.Empty;

        // Optional file info
        public string Filename { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long ContentLength { get; set; } = 0;

        // Message metadata
        public bool Delivered { get; set; } = false;
        public bool Read { get; set; } = false;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        // For text messages
        public string MessageText { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        // lowercase aliases
        public string userid { get => SenderId; set => SenderId = value; }
        public string filename { get => Filename; set => Filename = value; }
        public string contenttype { get => ContentType; set => ContentType = value; }
        public long contentlength { get => ContentLength; set => ContentLength = value; }
        public bool delivered { get => Delivered; set => Delivered = value; }
        public bool read { get => Read; set => Read = value; }

        public override string ToString()
        {
            return $"id: {id}, SenderId: {SenderId}, ReceiverId: {ReceiverId}, Filename: {Filename}, ContentType: {ContentType}, ContentLength: {ContentLength}, Delivered: {Delivered}, Read: {Read}, Timestamp: {Timestamp}, MessageText: {MessageText}";
        }
    }
}
