using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoScanner.Core.Persistence;
using RepoScanner.Core.Scanning;
using RepoScanner.Data;
using RepoScanner.Data.Repositories;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.CodePatterns.Rules;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Files;
using RepoScanner.Scanner.Git;
using RepoScanner.Scanner.Local;
using RepoScanner.Scanner.Packages;
using RepoScanner.Scanner.Queue;
using RepoScanner.Scanner.Secrets;
using RepoScanner.Scanner.Secrets.Rules;
using RepoScanner.Scanner.Sources;
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
                // --path <folder> scans a folder on this machine instead of reading the queue (#20). A
                // missing or wrong folder is a settings problem: one line and exit code 2, like the others.
                string? localFolder = null;
                if (LocalPathArgument.TryGet(args, out string? givenFolder))
                {
                    if (givenFolder == null || !Directory.Exists(givenFolder))
                    {
                        Log.Fatal("{Argument} must name a folder that exists, scanner not started: {Folder}",
                            LocalPathArgument.Name, givenFolder ?? "(no folder given)");
                        return ExitCodes.InvalidConfiguration;
                    }

                    localFolder = Path.GetFullPath(givenFolder);
                }

                HostApplicationBuilder builder = CreateBuilder(args);
                configureServices?.Invoke(builder.Services);

                using (IHost host = builder.Build())
                {
                    // Starting the host runs start-up checks, including settings validation
                    await host.StartAsync();

                    // Stops the work cleanly if the job is asked to shut down (Ctrl+C locally, SIGTERM in Azure)
                    IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

                    if (localFolder != null)
                    {
                        // LocalScanRunner uses the scoped DbContext and repositories, so it runs in a scope of its own
                        using (IServiceScope scope = host.Services.CreateScope())
                        {
                            LocalScanRunner runner = scope.ServiceProvider.GetRequiredService<LocalScanRunner>();
                            await runner.RunAsync(localFolder, lifetime.ApplicationStopping);
                        }
                    }
                    else
                    {
                        ScanOrchestrator orchestrator = host.Services.GetRequiredService<ScanOrchestrator>();
                        await orchestrator.RunAsync(lifetime.ApplicationStopping);
                    }

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
            catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is OptionsValidationException))
            {
                // Each settings group is checked on its own, so with more than one wrong, as on a new machine without
                // the database connection string or the PAT, the host reports them together: still one line, exit 2
                IEnumerable<string> failures = exception.InnerExceptions.Cast<OptionsValidationException>().SelectMany(inner => inner.Failures);
                Log.Fatal("Invalid configuration, scanner not started: {ValidationFailures}", failures);
                return ExitCodes.InvalidConfiguration;
            }
            catch (ScanQueueNotFoundException exception)
            {
                // A missing queue is fixed by creating it or correcting its name, like any setting (Issue 7 design,
                // decision 18): one line naming it, no stack trace
                Log.Fatal("Queue missing, scanner stopped: {Problem}", exception.Message);
                return ExitCodes.InvalidConfiguration;
            }
            catch (AzureDevOpsAuthenticationException exception)
            {
                // Every repository would fail the same way, so the run stopped, leaving the messages (Issue 8 design,
                // decision 13). Fixed by renewing the PAT, not code: one line saying so, no stack trace.
                Log.Fatal("Azure DevOps refused the token, scanner stopped: {Problem}", exception.Message);
                return ExitCodes.Failure;
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

            // The secret scanner (#16, #17). Singletons: the walker and the rules keep nothing between scans.
            builder.Services.AddSingleton<RepositoryFileWalker>();
            builder.Services.AddSingleton<ISecretRule, Sec001PrivateKeyRule>();
            builder.Services.AddSingleton<ISecretRule, Sec002AzureSharedKeyRule>();
            builder.Services.AddSingleton<ISecretRule, Sec003ConnectionStringPasswordRule>();
            builder.Services.AddSingleton<ISecretRule, Sec004UrlCredentialsRule>();
            builder.Services.AddSingleton<ISecretRule, Sec005EntraClientSecretRule>();
            builder.Services.AddSingleton<ISecretRule, Sec006NuGetApiKeyRule>();
            builder.Services.AddSingleton<ISecretRule, Sec007NamedSettingRule>();
            builder.Services.AddSingleton<IScanner, SecretScanner>();

            // The package scanner (#43 to #46), registered after the secret scanner, so it runs second. Singletons:
            // the nuget.org client keeps what it downloads for the whole execution (ADR-026).
            builder.Services.AddSingleton<ProjectFileReader>();
            NuGetVulnerabilityIndexClient.Register(builder.Services);
            builder.Services.AddSingleton<IScanner, PackageScanner>();

            // The code-pattern scanner (#49), registered last, so it runs third. Its rules, CP001 to CP006, are
            // registered as ICodePatternRule (#50 to #53). Singletons: a rule makes a new walker for each file.
            builder.Services.AddSingleton<ICodePatternRule, Cp001SqlFromStringsRule>();
            builder.Services.AddSingleton<ICodePatternRule, Cp002InsecureDeserializationRule>();
            builder.Services.AddSingleton<ICodePatternRule, Cp003CertificateValidationDisabledRule>();
            builder.Services.AddSingleton<ICodePatternRule, Cp004WeakHashingRule>();
            builder.Services.AddSingleton<ICodePatternRule, Cp005AnyOriginRule>();
            builder.Services.AddSingleton<ICodePatternRule, Cp006EmptyCatchRule>();
            builder.Services.AddSingleton<IScanner, CodePatternScanner>();

            // Scans one repository and saves it, for --path and the queue loop alike (#31). Scoped, because it
            // uses the scoped repositories.
            builder.Services.AddScoped<RepositoryScanRunner>();

            // The clock behind the time limits, so tests can move it forward instead of waiting
            builder.Services.AddSingleton(TimeProvider.System);

            if (LocalPathArgument.TryGet(args, out _))
            {
                // Local mode reads no queue, so queue settings are neither registered nor required (#20).
                // Scoped, because it uses the scoped repositories.
                builder.Services.AddScoped<LocalScanRunner>();
            }
            else
            {
                // Queue settings: read from the Scanner:Queue section, checked against the rules on
                // QueueOptions, and checked when the host starts rather than when first used
                builder.Services.AddOptions<QueueOptions>()
                    .Bind(builder.Configuration.GetSection(QueueOptions.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                // The loop's time limits (#31) and where repositories are cloned from (#37). Queue mode only, like the
                // queue settings, and checked when the host starts.
                builder.Services.AddOptions<ExecutionOptions>()
                    .Bind(builder.Configuration.GetSection(ExecutionOptions.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();
                builder.Services.AddOptions<AzureDevOpsOptions>()
                    .Bind(builder.Configuration.GetSection(AzureDevOpsOptions.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                // The lane's queue and its poison queue (#30): Azurite's connection string locally, the job's
                // managed identity in Azure. Building the clients makes no network call.
                builder.Services.AddSingleton<IScanQueue>(services =>
                {
                    IOptions<QueueOptions> queueOptions = services.GetRequiredService<IOptions<QueueOptions>>();
                    return new QueueConsumer(
                        QueueClients.Create(queueOptions.Value, queueOptions.Value.Name),
                        QueueClients.Create(queueOptions.Value, queueOptions.Value.PoisonQueueName),
                        queueOptions,
                        services.GetRequiredService<ILogger<QueueConsumer>>());
                });

                // A queued repository's code: a shallow clone from Azure DevOps into a new folder under the temporary
                // directory, the replica's own disk in Azure (#37, ADR-016). Singletons: none keeps anything between clones.
                builder.Services.AddSingleton<GitProcess>();
                builder.Services.AddSingleton(services => new WorkspaceFolders(
                    Path.Combine(Path.GetTempPath(), "reposcanner"), services.GetRequiredService<ILogger<WorkspaceFolders>>()));
                builder.Services.AddSingleton<IRepositorySource, AzureDevOpsCloneSource>();

                builder.Services.AddSingleton<ScanOrchestrator>();
            }

            return builder;
        }
    }
}
