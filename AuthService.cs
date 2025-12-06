using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AzureFileServer.Azure;

namespace AzureFileServer.Auth
{
    /// <summary>
    /// Handles user authentication and registration.
    /// Users are stored as JSON in Azure Blob Storage.
    /// </summary>
    public class AuthService
    {
        private readonly BlobStorageWrapper _blobStorage;
        private readonly string _blobContainer = "users";
        private readonly string _blobName = "users.json";

        public AuthService(BlobStorageWrapper blobStorage)
        {
            _blobStorage = blobStorage ?? throw new ArgumentNullException(nameof(blobStorage));
        }

        /// <summary>
        /// Validates if the given username/password combination exists.
        /// </summary>
        public async Task<bool> ValidateUserAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return false;

            var users = await GetUsersAsync();
            return users.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase) && u.Password == password);
        }

        /// <summary>
        /// Registers a new user.
        /// Throws an exception if the user already exists.
        /// </summary>
        public async Task RegisterUserAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username cannot be empty.", nameof(username));
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Password cannot be empty.", nameof(password));

            var users = await GetUsersAsync();

            if (users.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
                throw new Exception("User already exists");

            users.Add(new User
            {
                Username = username,
                Password = password
            });

            var json = JsonSerializer.Serialize(users, new JsonSerializerOptions { WriteIndented = true });
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            await _blobStorage.WriteBlob(_blobContainer, _blobName, ms);
        }

        /// <summary>
        /// Retrieves all registered users.
        /// Returns an empty list if none exist or on error.
        /// </summary>
        public async Task<List<User>> GetUsersAsync()
        {
            try
            {
                using var ms = new MemoryStream();
                try
                {
                    await _blobStorage.DownloadBlob(_blobContainer, _blobName, ms);
                }
                catch
                {
                    // No users.json exists yet
                    return new List<User>();
                }

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

    /// <summary>
    /// Represents a registered user.
    /// </summary>
    public class User
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
