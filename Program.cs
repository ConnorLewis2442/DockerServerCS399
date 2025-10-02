using CS397.Trace;
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

    
    private async Task UploadFile(HttpContext context)
{
    var span = _tracer.StartSpan("UploadFile");
    try
    {
        var userId = context.Request.Query["userId"].ToString();
        span.SetAttribute("userId", userId);

        var file = context.Request.Form.Files.FirstOrDefault();
        if (file == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("No file uploaded.");
            return;
        }

        span.SetAttribute("fileName", file.FileName);

        // Your upload logic here: save file to Blob, metadata to Cosmos DB, etc.

        await context.Response.WriteAsync("Upload successful");
    }
    catch (Exception e)
    {
        span.SetAttribute("error", true);
        span.SetAttribute("error.message", e.Message);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal server error");
    }
    finally
    {
        span.End();
    }
}
    private async Task HelloWorldDelegate(HttpContext context)
    {
        // OpenTelemetry uses concepts of "span" to correlate all messages together
        // from the same operation. We start with a new span, include some attributes,
        // and then wrap the work in a try-catch-finally block to ensure the span is
        // ended even if an exception is thrown, and to include error information in
        // the span.
        TelemetrySpan currentSpan = _tracer.StartSpan("HelloWorldDelegate");
        currentSpan.SetAttribute("http.method", context.Request.Method);
        currentSpan.SetAttribute("http.url", context.Request.Path);

        // Let's add the current TraceId to the response headers
        // Remember, good behavior is to include "x-" in front of custom headers
        context.Response.Headers.Append("x-trace-id", currentSpan.Context.TraceId.ToString());

        try
        {
            await context.Response.WriteAsync("Hello, World!");
        }
        catch (Exception e)
        {
            // If any error happend via an exception, we can include that information
            // in the log. Including the stack trace can be either useful or very
            // noisy, so you often control that with log levels.
            currentSpan.SetAttribute("error", true);
            currentSpan.SetAttribute("error.message", e.Message);
            currentSpan.SetAttribute("error.stacktrace", e.StackTrace);

            // 500 is the typical status code for an internal server error
            // We got an unhandled exception, so we don't know what went wrong
            // Hence we log the information and return a 500 status code
            context.Response.StatusCode = 500;
        }
        finally
        {
            // Ending the span will automatically include the time the entire operation took
            currentSpan.End();
        }
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
            .AddJsonConsoleExporter();
        });

        Program instance = new Program();

        WebApplication app = builder.Build();
 


        app.MapGet("/", instance.HelloWorldDelegate);
        app.MapGet("/hello", instance.HelloWorldDelegate);
        app.MapGet("/goodbye", instance.GoodbyeWorldDelegate);
        app.MapPost("/UploadFile", instance.UploadFile);

        app.Run();

    }
}
