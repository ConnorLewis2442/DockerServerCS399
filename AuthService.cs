using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using AzureFileServer.Azure;

namespace AzureFileServer.Auth
{
    public class AuthService
    {
        private readonly BlobStorageWrapper _blobStorage;
        private readonly string _blobContainer = "users";
        private readonly string _blobName = "users.json";

        // JWT settings
        private readonly string _jwtSecret = "YOUR_SUPER_SECRET_KEY_HERE"; // replace with env variable in prod
        private readonly int _jwtExpiryMinutes = 60;

        public AuthService(BlobStorageWrapper blobStorage)
        {
            _blobStorage = blobStorage;
        }

        public async Task<bool> ValidateUserAsync(string username, string password)
        {
            var users = await GetUsersAsync();
            return users.Any(u => u.Username == username && u.Password == password);
        }

        public async Task<string> GenerateJwtTokenAsync(string username)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = System.Text.Encoding.ASCII.GetBytes(_jwtSecret);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, username) }),
                Expires = DateTime.UtcNow.AddMinutes(_jwtExpiryMinutes),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }

        public ClaimsPrincipal? ValidateJwtToken(string token)
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var key = System.Text.Encoding.ASCII.GetBytes(_jwtSecret);

                var parameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ClockSkew = TimeSpan.Zero
                };

                return tokenHandler.ValidateToken(token, parameters, out var validatedToken);
            }
            catch
            {
                return null;
            }
        }

        // Existing user management methods
        public async Task RegisterUserAsync(string username, string password)
        {
            var users = await GetUsersAsync();
            if (users.Any(u => u.Username == username))
                throw new Exception("User already exists");

            users.Add(new User { Username = username, Password = password });
            var json = JsonSerializer.Serialize(users, new JsonSerializerOptions { WriteIndented = true });

            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            await _blobStorage.WriteBlob(_blobContainer, _blobName, ms);
        }

        public async Task<List<User>> GetUsersAsync()
        {
            try
            {
                using var ms = new MemoryStream();
                try { await _blobStorage.DownloadBlob(_blobContainer, _blobName, ms); }
                catch { return new List<User>(); }

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
