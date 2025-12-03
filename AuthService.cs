using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AzureFileServer.Auth
{
    public class AuthService
    {
        private readonly string _usersFile;

        public AuthService(string usersFile)
        {
            _usersFile = usersFile;
        }

        public async Task<bool> ValidateUserAsync(string username, string password)
        {
            if (!File.Exists(_usersFile))
                return false;

            var json = await File.ReadAllTextAsync(_usersFile);
            var users = JsonSerializer.Deserialize<List<User>>(json) ?? new List<User>();

            return users.Any(u => u.Username == username && u.Password == password);
        }

        public async Task RegisterUserAsync(string username, string password)
        {
            List<User> users;
            if (File.Exists(_usersFile))
            {
                var json = await File.ReadAllTextAsync(_usersFile);
                users = JsonSerializer.Deserialize<List<User>>(json) ?? new List<User>();
            }
            else
            {
                users = new List<User>();
            }

            if (users.Any(u => u.Username == username))
                throw new Exception("User already exists");

            users.Add(new User { Username = username, Password = password });

            var updatedJson = JsonSerializer.Serialize(users, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_usersFile, updatedJson);
        }
    }

    public class User
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
