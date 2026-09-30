using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;

namespace RepoScanner.Scanner.Queue
{
    // Reads a queue message's body as a ScanRequest (#29). The body is untrusted input: anything that is
    // not exactly the format in the Issue 7 design is malformed, and goes straight to the poison queue
    // with the reason given here (decision 13).
    public static class ScanRequestReader
    {
        // What a Windows folder name cannot hold, besides \ and /, which get their own message. Listed
        // rather than taken from Path.GetInvalidFileNameChars, which on Linux (the container) allows them.
        private static readonly char[] CharactersAFolderNameCannotHold = { '<', '>', ':', '"', '|', '?', '*' };

        // False, with what is wrong, when the body is not a scan request this scanner can read. The
        // reason names a field and a rule but never repeats what was sent: it goes into the log.
        public static bool TryRead(string body, [NotNullWhen(true)] out ScanRequest? request, [NotNullWhen(false)] out string? problem)
        {
            request = null;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                // Includes a message sent as Base64 (decision 3)
                problem = "The message is not JSON";
                return false;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    problem = "The message is not a JSON object";
                    return false;
                }

                if (!TryReadSchemaVersion(root, out problem)
                    || !TryReadGuid(root, "correlationId", out Guid correlationId, out problem)
                    || !TryReadGuid(root, "repositoryId", out Guid repositoryId, out problem)
                    || !TryReadText(root, "projectName", out string projectName, out problem)
                    || !TryReadText(root, "repositoryName", out string repositoryName, out problem)
                    || !TryReadText(root, "defaultBranch", out string defaultBranch, out problem))
                {
                    return false;
                }

                problem = CheckRepositoryName(repositoryName) ?? CheckDefaultBranch(defaultBranch);
                if (problem != null)
                {
                    return false;
                }

                request = new ScanRequest(correlationId, repositoryId, projectName, repositoryName, defaultBranch);
                return true;
            }
        }

        private static bool TryReadSchemaVersion(JsonElement root, [NotNullWhen(false)] out string? problem)
        {
            if (!TryGetField(root, "schemaVersion", out JsonElement value, out problem))
            {
                return false;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int version))
            {
                problem = "schemaVersion must be a whole number";
                return false;
            }

            if (version != ScanRequest.SchemaVersion)
            {
                problem = "schemaVersion " + version + " is not supported: this scanner reads version " + ScanRequest.SchemaVersion;
                return false;
            }

            return true;
        }

        private static bool TryReadGuid(JsonElement root, string name, out Guid guid, [NotNullWhen(false)] out string? problem)
        {
            guid = Guid.Empty;
            if (!TryGetField(root, name, out JsonElement value, out problem))
            {
                return false;
            }

            if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out guid))
            {
                problem = name + " must be a GUID";
                return false;
            }

            if (guid == Guid.Empty)
            {
                problem = name + " must not be all zeros";
                return false;
            }

            return true;
        }

        private static bool TryReadText(JsonElement root, string name, out string text, [NotNullWhen(false)] out string? problem)
        {
            text = string.Empty;
            if (!TryGetField(root, name, out JsonElement value, out problem))
            {
                return false;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                problem = name + " must be text";
                return false;
            }

            text = value.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                problem = name + " must not be empty";
                return false;
            }

            return true;
        }

        // Field names are matched ignoring case, and fields the scanner does not read are ignored, so a
        // sender can add one (decision 3). A field sent twice is refused: two readers could each take a
        // different one, so neither can be trusted.
        private static bool TryGetField(JsonElement root, string name, out JsonElement value, [NotNullWhen(false)] out string? problem)
        {
            value = default;
            bool found = false;

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (found)
                {
                    problem = name + " appears more than once";
                    return false;
                }

                value = property.Value;
                found = true;
            }

            problem = found ? null : name + " is missing";
            return found;
        }

        // The name becomes a folder under LocalRoot in Issue 7 and part of the clone URL in Issue 8, so a
        // name such as ..\..\Windows must not reach either
        private static string? CheckRepositoryName(string repositoryName)
        {
            if (repositoryName == "." || repositoryName == "..")
            {
                return "repositoryName must not be . or ..";
            }

            if (repositoryName.Contains('\\') || repositoryName.Contains('/'))
            {
                return @"repositoryName must not contain \ or /";
            }

            if (repositoryName.IndexOfAny(CharactersAFolderNameCannotHold) >= 0 || repositoryName.Any(char.IsControl))
            {
                return "repositoryName must not contain < > : \" | ? * or control characters";
            }

            return null;
        }

        // The Azure DevOps REST API gives refs/heads/develop; the sender strips the prefix, because
        // git clone --branch takes develop (Issue 7 design, Message format)
        private static string? CheckDefaultBranch(string defaultBranch)
        {
            if (defaultBranch.StartsWith("refs/", StringComparison.OrdinalIgnoreCase))
            {
                return "defaultBranch must be a branch name such as develop, not refs/heads/develop";
            }

            return null;
        }
    }
}
