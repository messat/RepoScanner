using System;
using System.Security.Cryptography;
using System.Text;

namespace RepoScanner.Scanner.Secrets
{
    // Lets a finding recognise the same secret across scans and files without storing it (ADR-023)
    public static class SecretHasher
    {
        // SHA-256 of the UTF-8 bytes as 64 lower-case hex characters, which fits the SecretHash column
        // (varchar(64)). Unsalted on purpose: the same secret must always give the same hash.
        public static string Hash(string secret)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexStringLower(hash);
        }
    }
}
