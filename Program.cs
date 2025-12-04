using Microsoft.AspNetCore.Http.Json;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Allow large JSON
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

var app = builder.Build();

// Cosmos setup
string connectionString = builder.Configuration["CosmosDb:ConnectionString"];
CosmosClient client = new CosmosClient(connectionString);
Database db = await client.CreateDatabaseIfNotExistsAsync("MessagingDB");
Container messages = await db.CreateContainerIfNotExistsAsync("Messages", "/receiverId");

// FileServerHandlers
var fileServer = new FileServerHandlers(messages);

// LOGIN
app.MapPost("/login", async ctx =>
{
    await ctx.Response.WriteAsync("Logged in"); // dummy login, no token
});

// SEND MESSAGE
app.MapPost("/sendmessage", async (HttpContext context) =>
{
    await fileServer.SendMessageDelegate(context, "_");
});

// GET UNDELIVERED
app.MapGet("/undelivered", async (HttpContext context) =>
{
    string receiver = "alice"; // hardcoded for testing
    await fileServer.GetUndeliveredDelegate(context, receiver);
});

app.Run();
