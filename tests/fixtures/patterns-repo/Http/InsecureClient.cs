using System.Net;
using System.Net.Http;
using System.Net.Security;

namespace PatternsRepo.Http
{
    // CP003: certificate validation switched off. Insecure on purpose: this file is never built.
    public class InsecureClient
    {
        public HttpClient Create()
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true; // CP003
            return new HttpClient(handler);
        }

        public HttpClient CreateWithBuiltInValidator()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator // CP003
            };
            return new HttpClient(handler);
        }

        public void TrustEverythingInFramework()
        {
            ServicePointManager.ServerCertificateValidationCallback += delegate { return true; }; // CP003
        }

        // Not a finding: the callback checks the certificate
        public HttpClient CreateChecked()
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => errors == SslPolicyErrors.None;
            return new HttpClient(handler);
        }
    }
}
