using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RepoScanner.Scanner
{
    public static class Program
    {
        // The return value is the process exit code the Container Apps Job sees.
        public static async Task<int> Main(string[] args)
        {
            // Sets up configuration (appsettings.json, user-secrets in Development,
            // environment variables, command line), dependency injection and logging
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

            // Register the classes the host can create for us
            builder.Services.AddSingleton<ScanOrchestrator>();

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
    }
}
