using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoScanner.Scanner.Configuration;
using Serilog;
using Serilog.Events;

namespace RepoScanner.Scanner
{
    public static class Program
    {
        // The return value is the process exit code the Container Apps Job sees.
        public static async Task<int> Main(string[] args)
        {
            HostApplicationBuilder builder = CreateBuilder(args);

            using (IHost host = builder.Build())
            {
                // Starting the host runs start-up checks (e.g. options validation, task 4 step 3)
                await host.StartAsync();

                // Stops the work cleanly if the job is asked to shut down (Ctrl+C locally, SIGTERM in Azure)
                IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

                ScanOrchestrator orchestrator = host.Services.GetRequiredService<ScanOrchestrator>();
                await orchestrator.RunAsync(lifetime.ApplicationStopping);

                await host.StopAsync();
            }

            return 0;
        }

        // Configures the host without running it, so tests can build it and inspect the services.
        public static HostApplicationBuilder CreateBuilder(string[] args)
        {
            // Sets up configuration (appsettings.json, user-secrets in Development,
            // environment variables, command line), dependency injection and default logging.
            // Content root is the app's own folder, so appsettings files are found wherever
            // the app is started from (dotnet run from the repo root, or /app in the container).
            HostApplicationBuilderSettings settings = new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            };
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(settings);

            // Remove the default logging providers (Console, Debug, EventSource, EventLog):
            // Serilog is the only place logs go
            builder.Logging.ClearProviders();

            builder.Services.AddSerilog(logger => logger
                // Our own code logs from Information upwards
                .MinimumLevel.Information()
                // Framework messages (e.g. "Application started. Press Ctrl+C") only when they matter
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                // Lets later code add properties (e.g. CorrelationId, Issue 10) to every log line in a scope
                .Enrich.FromLogContext()
                // Time, level, class name, message
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

            // Queue settings: read from the Scanner:Queue section, checked against the rules on
            // QueueOptions, and checked when the host starts rather than when first used
            builder.Services.AddOptions<QueueOptions>()
                .Bind(builder.Configuration.GetSection(QueueOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Register the classes the host can create for us
            builder.Services.AddSingleton<ScanOrchestrator>();

            return builder;
        }
    }
}
