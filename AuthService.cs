using System.Text.Json;
using AzureFileServer.Azure;

namespace AzureFileServer.Auth
{
    public class AuthService
    {
        private readonly BlobStorageWrapper _blobStorage;
        private readonly string _containerName = "users-container";
        private readonly string _blobName = "users.json";

        public AuthService(BlobStorageWrapper blobStorage)
        {
            _blobStorage = blobStorage;
        }

        private async Task<List<User>> GetUsersAsync()
        {
            if (!await _blobStorage.BlobExists(_containerName, _blobName))
                return new List<User>();

            using var stream = await _blobStorage.ReadBlob(_containerName, _blobName);
            return await JsonSerializer.DeserializeAsync<List<User>>(stream) ?? new List<User>();
        }

        private async Task SaveUsersAsync(List<User> users)
        {
            using var stream = new MemoryStream();
            await JsonSerializer.SerializeAsync(stream, users, new JsonSerializerOptions { WriteIndented = true });
            stream.Position = 0;
            await _blobStorage.WriteBlob(_containerName, _blobName, stream);
        }

        public async Task<bool> ValidateUserAsync(string username, string password)
        {
            var users = await GetUsersAsync();
            return users.Any(u => u.Username == username && u.Password == password);
        }

        public async Task RegisterUserAsync(string username, string password)
        {
            var users = await GetUsersAsync();
            if (users.Any(u => u.Username == username))
                throw new Exception("User already exists");

            users.Add(new User { Username = username, Password = password });
            await SaveUsersAsync(users);
        }
    }

    public class User
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
