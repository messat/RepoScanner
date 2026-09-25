using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Scanning
{
    // One of the three scanners in the one scanner image (ADR-009): secrets, packages and code patterns.
    // The contract lives in Core so the orchestrator can run every scanner the same way.
    public interface IScanner
    {
        ScannerType ScannerType { get; }

        // Scans the files under rootPath and returns what it found. Never touches the database: saving
        // is the caller's job, once the findings have been matched against the stored ones.
        Task<IReadOnlyList<RawFinding>> ScanAsync(string rootPath, CancellationToken cancellationToken);
    }
}
