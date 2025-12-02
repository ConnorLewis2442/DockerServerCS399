namespace AzureFileServer.FileServer
{
    // This class is used to store metadata about a file and can be
    // used for serializing and deserializing the JSON data in CosmosDb
    public class FileMetadata
    {
        private string GenerateId()
        {
            return $"{this.userid}-{this.filename}";
        }

        // Note that "id" must be lower case for the Cosmos APIs to work
        // and for consistency, all keys are lower case
        public string id { get { return GenerateId(); } }

        public string userid { get; set; } = "/userid";
        public string filename { get; set; } = string.Empty;
        public string contenttype { get; set; } = string.Empty;
        public long contentlength { get; set; } = 0;

        // New metadata fields for messaging app
        public bool delivered { get; set; } = false;
        public bool read { get; set; } = false;
        public DateTime timestamp { get; set; } = DateTime.UtcNow;

        public override string ToString()
        {
            return $"id: {id}, userid: {userid}, filename: {filename}, contenttype: {contenttype}, contentlength: {contentlength}, delivered: {delivered}, read: {read}, timestamp: {timestamp}";
        }
    }
}
