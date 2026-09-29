using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace PatternsRepo.Web
{
    // CP005: any origin allowed. Insecure on purpose: this file is never built.
    public static class CorsSetup
    {
        public static void AddOpenPolicies(IServiceCollection services)
        {
            services.AddCors(options =>
            {
                options.AddPolicy("Open", policy => policy.AllowAnyOrigin().AllowAnyHeader()); // CP005
                options.AddPolicy("AlsoOpen", policy => policy.SetIsOriginAllowed(origin => true)); // CP005
            });
        }

        // Not a finding: the allowed origins are listed
        public static void AddPortalPolicy(IServiceCollection services)
        {
            services.AddCors(options =>
            {
                options.AddPolicy("Portal", policy => policy.WithOrigins("https://portal.example.com").AllowAnyHeader());
            });
        }
    }
}
