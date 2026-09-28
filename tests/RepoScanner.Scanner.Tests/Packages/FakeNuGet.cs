using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using RepoScanner.Scanner.Packages;

namespace RepoScanner.Scanner.Tests.Packages
{
    // Stands in for nuget.org at the bottom of the HTTP client, beneath the real retry and timeout handler,
    // answering from the canned responses in NuGetData/ (see its README). An address it does not know
    // fails the test, so no test reaches the internet.
    internal sealed class FakeNuGet : HttpMessageHandler
    {
        public const string ServiceIndexUrl = "https://api.nuget.org/v3/index.json";
        public const string VulnerabilityIndexUrl = "https://api.nuget.org/v3/vulnerabilities/index.json";
        public const string PackageBaseAddress = "https://api.nuget.org/v3-flatcontainer/";

        private static readonly string DataFolder = Path.Combine(RepositoryRoot.FullPath, "tests", "RepoScanner.Scanner.Tests", "Packages", "NuGetData");

        private readonly object _lock = new object();
        private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _replacedBodies = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<SeenRequest> _requests = new List<SeenRequest>();
        private readonly List<string> _unknownAddresses = new List<string>();
        private string? _failingUrl;
        private int _failuresLeft;
        private HttpStatusCode _failureStatus;
        private TaskCompletionSource? _held;

        public FakeNuGet()
        {
            _files[ServiceIndexUrl] = "service-index.json";
            _files[VulnerabilityIndexUrl] = "vulnerability-index.json";

            // The pages' addresses are the ones the canned index lists, as on nuget.org
            using (JsonDocument index = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataFolder, "vulnerability-index.json"))))
            {
                foreach (JsonElement page in index.RootElement.EnumerateArray())
                {
                    _files[page.GetProperty("@id").GetString()!] = "vulnerability-" + page.GetProperty("@name").GetString() + ".json";
                }
            }

            BasePageUrl = _files.Single(file => file.Value == "vulnerability-base.json").Key;
            UpdatePageUrl = _files.Single(file => file.Value == "vulnerability-update.json").Key;
        }

        public string BasePageUrl { get; }

        public string UpdatePageUrl { get; }

        // Addresses asked for that the fake does not know. A test that sees one has reached for the internet.
        public IReadOnlyList<string> UnknownAddresses
        {
            get
            {
                lock (_lock)
                {
                    return _unknownAddresses.ToList();
                }
            }
        }

        public IReadOnlyList<SeenRequest> Requests
        {
            get
            {
                lock (_lock)
                {
                    return _requests.ToList();
                }
            }
        }

        public static string VersionListUrl(string packageId)
        {
            return PackageBaseAddress + packageId.ToLowerInvariant() + "/index.json";
        }

        public int RequestCount(string url)
        {
            return Requests.Count(request => request.Url == url);
        }

        // Answers this address with the given body in place of the canned one
        public void Respond(string url, string body)
        {
            lock (_lock)
            {
                _replacedBodies[url] = body;
            }
        }

        // The next requests to the address, or to every address when url is null, get the status and no body
        public void Fail(int times = int.MaxValue, string? url = null, HttpStatusCode status = HttpStatusCode.ServiceUnavailable)
        {
            lock (_lock)
            {
                _failingUrl = url;
                _failuresLeft = times;
                _failureStatus = status;
            }
        }

        // Requests are recorded but not answered until ReleaseResponses, so callers can be made to overlap
        public void HoldResponses()
        {
            lock (_lock)
            {
                _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ReleaseResponses()
        {
            lock (_lock)
            {
                _held?.TrySetResult();
                _held = null;
            }
        }

        // Puts this fake beneath the real retry handler of the nuget.org client that
        // NuGetVulnerabilityIndexClient.Register adds, with no wait between retries so the tests stay fast
        public void ReplaceNuGetOrg(IServiceCollection services)
        {
            services.AddHttpClient(NuGetVulnerabilityIndexClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => this);
            services.Configure<HttpStandardResilienceOptions>(NuGetVulnerabilityIndexClient.ResiliencePipelineName, options => options.Retry.Delay = TimeSpan.Zero);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Task? held;

            lock (_lock)
            {
                _requests.Add(new SeenRequest(url, request.Headers.UserAgent.ToString()));
                held = _held?.Task;
            }

            if (held != null)
            {
                await held;
            }

            lock (_lock)
            {
                if (_failuresLeft > 0 && (_failingUrl == null || _failingUrl == url))
                {
                    _failuresLeft--;
                    return new HttpResponseMessage(_failureStatus);
                }

                if (_replacedBodies.TryGetValue(url, out string? replaced))
                {
                    return Json(replaced);
                }
            }

            if (_files.TryGetValue(url, out string? file))
            {
                return Json(File.ReadAllText(Path.Combine(DataFolder, file)));
            }

            if (url.StartsWith(PackageBaseAddress, StringComparison.Ordinal) && url.EndsWith("/index.json", StringComparison.Ordinal))
            {
                // nuget.org answers 404 for a package it does not know
                string packageId = url.Substring(PackageBaseAddress.Length, url.Length - PackageBaseAddress.Length - "/index.json".Length);
                string versionsFile = Path.Combine(DataFolder, "versions", packageId + ".json");

                return File.Exists(versionsFile) ? Json(File.ReadAllText(versionsFile)) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            lock (_lock)
            {
                _unknownAddresses.Add(url);
            }

            throw new InvalidOperationException("FakeNuGet does not know " + url + ": a test must not reach the internet");
        }

        private static HttpResponseMessage Json(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        public sealed record SeenRequest(string Url, string UserAgent);
    }
}
