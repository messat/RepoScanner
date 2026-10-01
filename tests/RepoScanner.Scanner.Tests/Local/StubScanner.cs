using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;

namespace RepoScanner.Scanner.Tests.Local
{
    // A scanner that returns the findings it was given, or fails, so RepositoryScanRunner can be tested
    // without reading any files
    internal sealed class StubScanner : IScanner
    {
        private readonly IReadOnlyList<RawFinding> _findings;
        private readonly Exception? _failure;

        public StubScanner(params RawFinding[] findings)
        {
            _findings = findings;
        }

        public StubScanner(Exception failure)
        {
            _findings = Array.Empty<RawFinding>();
            _failure = failure;
        }

        // Secret unless a test needs a second scanner type
        public ScannerType ScannerType { get; init; } = ScannerType.Secret;

        // How many times the runner has run it
        public int CallCount { get; private set; }

        // Runs as the scan starts, for example to stop the run partway
        public Action? OnScan { get; init; }

        // The folder it was last asked to scan
        public string? RootPath { get; private set; }

        public Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken)
        {
            CallCount++;
            RootPath = rootPath;
            OnScan?.Invoke();

            return _failure == null
                ? Task.FromResult(_findings)
                : Task.FromException<IReadOnlyList<RawFinding>>(_failure);
        }
    }
}
