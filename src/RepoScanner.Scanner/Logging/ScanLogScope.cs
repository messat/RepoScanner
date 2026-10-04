using System;
using System.Collections.Generic;

namespace RepoScanner.Scanner.Logging
{
    // The logging scope that puts a scan's correlation ID and repository on every line logged inside it, by any class
    // (#62, Issue 10 design, decision 2). Serilog turns each pair into a property of the event, so one Log Analytics
    // search finds every line of a scan; FakeLogger keeps it in FakeLogRecord.Scopes.
    public static class ScanLogScope
    {
        public static IReadOnlyDictionary<string, object> For(Guid correlationId, string projectName, string repositoryName)
        {
            return new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["ProjectName"] = projectName,
                ["RepositoryName"] = repositoryName
            };
        }
    }
}
