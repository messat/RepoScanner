using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;

namespace RepoScanner.Scanner.Tests.Local
{
    // A scanner that returns the findings it was given, or fails, so LocalScanRunner can be tested
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

        public ScannerType ScannerType => ScannerType.Secret;

        public Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken)
        {
            return _failure == null
                ? Task.FromResult(_findings)
                : Task.FromException<IReadOnlyList<RawFinding>>(_failure);
        }
    }
}
