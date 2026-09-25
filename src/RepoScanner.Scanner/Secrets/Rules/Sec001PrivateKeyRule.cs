using System.Text.RegularExpressions;
using RepoScanner.Core.Enums;

namespace RepoScanner.Scanner.Secrets.Rules
{
    // A PEM private key block: RSA, EC, DSA, OpenSSH or PKCS#8, encrypted or not. Whoever holds it can
    // sign or decrypt as its owner, so it is Critical (Issue 3 design, Secret rules).
    public sealed class Sec001PrivateKeyRule : ISecretRule
    {
        public string RuleId => "SEC001";

        public Severity Severity => Severity.Critical;

        public string Message => "Private key";

        // From BEGIN to the END line of the same type (\k<type>), lazily, so two keys in one file are
        // two matches. The secret is everything in between: base64, line breaks and, in an older
        // encrypted key, header lines such as "Proc-Type: 4,ENCRYPTED". The line breaks may also be the
        // escapes \n and \r, as when the key is held on one line in a JSON string.
        public Regex Pattern { get; } = SecretRulePattern.Create(
            @"-----BEGIN (?<type>(?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?)PRIVATE KEY-----" +
            @"(?<secret>(?:[A-Za-z0-9+/=\s:,-]|\\[nr])+?)" +
            @"-----END \k<type>PRIVATE KEY-----");
    }
}
