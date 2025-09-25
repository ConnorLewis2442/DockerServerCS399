class Program
{
    private static async Task HelloWorldDelegate(HttpContext context)
    {
        await context.Response.WriteAsync("Hello World!");
        Console.WriteLine("Hello Called");
            
    }

    private static async Task GoodbyeWorldDelegate(HttpContext context)
    {
        await context.Response.WriteAsync("Goodbye World!");
        Console.WriteLine("goodbye Called");
    }

    static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        WebApplication app = builder.Build();

        app.MapGet("/", HelloWorldDelegate);
        app.MapGet("/hello", HelloWorldDelegate);
        app.MapGet("/goodbye", GoodbyeWorldDelegate);

        app.Run();
      
    }
}
