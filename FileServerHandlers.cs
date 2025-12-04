public async Task SendMessageDelegate(HttpContext context, string senderId)
{
    using var log = _logger.StartMethod(nameof(SendMessageDelegate), context);
    try
    {
        string receiverId = GetParameter(context.Request, "receiverId");
        string messageText = context.Request.HasFormContentType ? context.Request.Form["messageText"].ToString() : string.Empty;
        IFormFile fileContent = context.Request.HasFormContentType ? context.Request.Form.Files.FirstOrDefault() : null;

        FileMetadata m = new FileMetadata
        {
            SenderId = senderId,  // use logged-in user
            ReceiverId = receiverId,
            Timestamp = DateTime.UtcNow,
            Delivered = false,
            Read = false,
            MessageText = messageText ?? string.Empty
        };

        if (fileContent != null)
        {
            m.Filename = fileContent.FileName;
            m.ContentType = fileContent.ContentType;
            m.ContentLength = fileContent.Length;

            var blobStorage = new BlobStorageWrapper(_configuration);
            using var fileStream = fileContent.OpenReadStream();
            await blobStorage.WriteBlob(receiverId, m.Filename, fileStream);
        }

        await _cosmosDbWrapper.AddItemAsync(m, receiverId);
    }
    catch (Exception e)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"ERROR: {e.Message}\n{e.StackTrace}");
    }
}
