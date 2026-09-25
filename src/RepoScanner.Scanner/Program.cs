using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoScanner.Core.Persistence;
using RepoScanner.Data;
using RepoScanner.Data.Repositories;
using RepoScanner.Scanner.Configuration;
using Serilog;
using Serilog.Events;

namespace RepoScanner.Scanner
{
    public static class Program
    {
        // Time, level, class name, message
        private const string ConsoleOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        // The return value is the process exit code the Container Apps Job sees (see ExitCodes).
        public static async Task<int> Main(string[] args)
        {
            return await RunAsync(args, configureServices: null);
        }

        // Separate from Main so tests can add services (e.g. one that fails on purpose) and check
        // the exit code. Internal: visible to RepoScanner.Scanner.Tests only (see the .csproj).
        internal static async Task<int> RunAsync(string[] args, Action<IServiceCollection>? configureServices)
        {
            // Start-up logger: exists before the host is built and after it is disposed, so a
            // failure to start is always logged. The host has its own logger for normal running.
            Log.Logger = new LoggerConfiguration()
                .Enrich.WithProperty("SourceContext", typeof(Program).FullName)
                .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
                .CreateLogger();

            try
            {
                HostApplicationBuilder builder = CreateBuilder(args);
                configureServices?.Invoke(builder.Services);

                using (IHost host = builder.Build())
                {
                    // Starting the host runs start-up checks, including settings validation
                    await host.StartAsync();

                    // Stops the work cleanly if the job is asked to shut down (Ctrl+C locally, SIGTERM in Azure)
                    IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

                    ScanOrchestrator orchestrator = host.Services.GetRequiredService<ScanOrchestrator>();
                    await orchestrator.RunAsync(lifetime.ApplicationStopping);

                    await host.StopAsync();
                }

                return ExitCodes.Success;
            }
            catch (OptionsValidationException exception)
            {
                // A settings problem is fixed by changing settings, not code: list what is wrong, no stack trace
                Log.Fatal("Invalid configuration, scanner not started: {ValidationFailures}", exception.Failures);
                return ExitCodes.InvalidConfiguration;
            }
            catch (Exception exception)
            {
                // Anything else is unexpected: log it with the full exception so it can be diagnosed
                Log.Fatal(exception, "Scanner failed");
                return ExitCodes.Failure;
            }
            finally
            {
                // Write out anything still buffered before the process exits
                await Log.CloseAndFlushAsync();
            }
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
                // The host's own "Hosting failed to start" error is not needed: Main logs every
                // start-up failure once, with the matching exit code
                .MinimumLevel.Override("Microsoft.Extensions.Hosting", LogEventLevel.Fatal)
                // Lets later code add properties (e.g. CorrelationId, Issue 10) to every log line in a scope
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: ConsoleOutputTemplate),
                // Keep the start-up logger in Log.Logger: the host's logger is disposed with the host,
                // before Main's catch blocks run
                preserveStaticLogger: true);

            // Queue settings: read from the Scanner:Queue section, checked against the rules on
            // QueueOptions, and checked when the host starts rather than when first used
            builder.Services.AddOptions<QueueOptions>()
                .Bind(builder.Configuration.GetSection(QueueOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Database settings: read from the Scanner:Database section and checked when the host starts,
            // like the queue settings. Required in every mode: the scanner's job is to save findings.
            builder.Services.AddOptions<DatabaseOptions>()
                .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // The shared EF Core context and repositories from RepoScanner.Data, one of each per scope.
            // The connection string comes from the validated settings, and a transient SQL error (such as
            // Azure SQL failing over) is retried rather than failing the scan (ADR-015). The repositories
            // use one SaveChanges per method and no explicit transactions, which retrying supports.
            builder.Services.AddDbContext<ScannerDbContext>((services, options) =>
            {
                DatabaseOptions database = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
                options.UseSqlServer(database.ConnectionString, sql => sql.EnableRetryOnFailure());
            });
            builder.Services.AddScoped<IScanRunRepository, ScanRunRepository>();
            builder.Services.AddScoped<IFindingRepository, FindingRepository>();

            // Register the classes the host can create for us
            builder.Services.AddSingleton<ScanOrchestrator>();

            return builder;
        }
    }
}
