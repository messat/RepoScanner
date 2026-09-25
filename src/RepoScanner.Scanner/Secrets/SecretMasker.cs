using System;

namespace RepoScanner.Scanner.Secrets
{
    // The preview a person sees on the dashboard instead of the secret (Issue 3 design, Masking and
    // hashing). Enough to recognise which key it is, never enough to use it.
    public static class SecretMasker
    {
        // Always four, whatever the secret's length, so the preview does not reveal how long it is
        public const string Stars = "****";

        // Never more than 4 characters, and never more than a quarter of the secret
        public const int MaxCharactersShown = 4;

        public static string Mask(string secret)
        {
            int shown = Math.Min(MaxCharactersShown, secret.Length / 4);
            return secret.Substring(0, shown) + Stars;
        }
    }
}
