using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RepoScanner.Scanner.Tests.Sources
{
    // A stand-in for Azure DevOps on 127.0.0.1, so a real git can be refused, or kept waiting, with no network. It
    // records the Authorization header of every request, which shows the PAT reached git's request.
    internal sealed class FakeAzureDevOps : IDisposable
    {
        // What Azure DevOps answers a clone whose token it refuses
        private const string Refusal =
            "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"Azure DevOps\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

        // Request headers are a few hundred bytes; anything this long is not a git request
        private const int MaxHeaderBytes = 64 * 1024;

        private readonly bool _refuse;
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private readonly ConcurrentQueue<string> _authorizationHeaders = new ConcurrentQueue<string>();
        private readonly ConcurrentBag<TcpClient> _clients = new ConcurrentBag<TcpClient>();
        private readonly TaskCompletionSource _firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeAzureDevOps(bool refuse)
        {
            _refuse = refuse;
            _listener.Start();
            OrganizationUrl = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/BlacklightInternalsMo";
            _ = AcceptAsync();
        }

        public string OrganizationUrl { get; }

        // The value after "Authorization:", such as "Basic <Base64>", one per request in the order they came
        public IReadOnlyList<string> AuthorizationHeaders => _authorizationHeaders.ToArray();

        // Completes when git's first request has arrived, so a test knows git is running before it acts
        public Task FirstRequest => _firstRequest.Task;

        // Answers every request 401, as Azure DevOps does for a wrong, expired or revoked PAT
        public static FakeAzureDevOps StartRefusing()
        {
            return new FakeAzureDevOps(refuse: true);
        }

        // Takes every request and never answers, as a server that has stopped responding does
        public static FakeAzureDevOps StartStalling()
        {
            return new FakeAzureDevOps(refuse: false);
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            foreach (TcpClient client in _clients)
            {
                client.Dispose();
            }

            _stopping.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    _clients.Add(client);
                    _ = AnswerAsync(client);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException || exception is SocketException || exception is ObjectDisposedException)
            {
                // Disposed: the test is over
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                string headers = await ReadHeadersAsync(stream);
                foreach (string line in headers.Split("\r\n"))
                {
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    {
                        _authorizationHeaders.Enqueue(line.Substring("Authorization:".Length).Trim());
                    }
                }

                _firstRequest.TrySetResult();

                if (_refuse)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(Refusal), _stopping.Token);
                    client.Dispose();
                }

                // Stalling: the connection stays open, unanswered, until the test disposes the server
            }
            catch (Exception exception) when (exception is IOException || exception is OperationCanceledException || exception is ObjectDisposedException)
            {
                // Git hung up, or the test is over
            }
        }

        // Up to the blank line that ends an HTTP request's headers
        private async Task<string> ReadHeadersAsync(NetworkStream stream)
        {
            MemoryStream received = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (received.Length < MaxHeaderBytes)
            {
                int count = await stream.ReadAsync(buffer, _stopping.Token);
                if (count == 0)
                {
                    break;
                }

                received.Write(buffer, 0, count);
                string text = Encoding.ASCII.GetString(received.ToArray());
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    return text;
                }
            }

            return Encoding.ASCII.GetString(received.ToArray());
        }
    }
}
