using System.Text.Json;
using AzureFileServer.Azure;

namespace AzureFileServer.Auth
{
    public class AuthService
    {
        private readonly BlobStorageWrapper _blobStorage;
        private readonly string _blobContainer = "users";
        private readonly string _blobName = "users.json";

        public AuthService(BlobStorageWrapper blobStorage)
        {
            _blobStorage = blobStorage;
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
            var json = JsonSerializer.Serialize(users, new JsonSerializerOptions { WriteIndented = true });

            using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            {
                await _blobStorage.WriteBlob(_blobContainer, _blobName, ms);
            }
        }

        public async Task<List<User>> GetUsersAsync()
        {
            try
            {
                using var ms = new MemoryStream();
                bool exists = await _blobStorage.DownloadBlob(_blobContainer, _blobName, ms);

                if (!exists)
                    return new List<User>();

                ms.Position = 0;
                using var reader = new StreamReader(ms);
                string json = await reader.ReadToEndAsync();
                return JsonSerializer.Deserialize<List<User>>(json) ?? new List<User>();
            }
            catch
            {
                return new List<User>();
            }
        }
    }

    public class User
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
