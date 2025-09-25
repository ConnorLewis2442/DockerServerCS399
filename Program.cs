using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
class Program
{
    const string serviceName = "monitored-docker-web-service";
    const string serviceVersion = "1.0.0";

    private readonly Tracer _tracer;

    public Program()
    {
        _tracer = TracerProvider.Default.GetTracer(serviceName);
    }

    private async Task HelloWorldDelegate(HttpContext context)
    {
        await context.Response.WriteAsync("Hello World!");
        Console.WriteLine("Hello Called");

    }

    private async Task GoodbyeWorldDelegate(HttpContext context)
    {
        await context.Response.WriteAsync("Goodbye World!");
        Console.WriteLine("goodbye Called");
    }

    static void Main(string[] args)
    {
                WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Configure important OpenTelemetry settings, the console exporter, and instrumentation library
        builder.Services.AddOpenTelemetry().WithTracing(tcb =>
        {
            tcb
            .AddSource(serviceName)
            .SetResourceBuilder(
                ResourceBuilder.CreateDefault()
                    .AddService(serviceName: serviceName, serviceVersion: serviceVersion))
            .AddAspNetCoreInstrumentation()
            .AddConsoleExporter();
        });

        Program instance = new Program();

        WebApplication app = builder.Build();
 


        app.MapGet("/", instance.HelloWorldDelegate);
        app.MapGet("/hello", instance.HelloWorldDelegate);
        app.MapGet("/goodbye", instance.GoodbyeWorldDelegate);

        app.Run();

    }
}
