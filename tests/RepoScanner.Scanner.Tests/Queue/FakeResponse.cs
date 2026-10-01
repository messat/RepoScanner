using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Azure;
using Azure.Core;

namespace RepoScanner.Scanner.Tests.Queue
{
    // The HTTP response behind a fake Azure SDK call. Azure's Response is abstract so that tests can
    // make one; nothing here reads its headers or body.
    public sealed class FakeResponse : Response
    {
        private readonly int _status;

        public FakeResponse(int status)
        {
            _status = status;
        }

        public override int Status => _status;

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
            // Nothing to release: there is no real connection
        }

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }

        protected override bool ContainsHeader(string name)
        {
            return false;
        }

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
        {
            return Array.Empty<HttpHeader>();
        }
    }
}
