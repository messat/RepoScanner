using System.Security.Cryptography;

namespace PatternsRepo.Crypto
{
    // CP004: weak hashing. Insecure on purpose: this file is never built.
    public class Checksums
    {
        public byte[] Md5Of(byte[] bytes)
        {
            using (MD5 md5 = MD5.Create()) // CP004 (CA5351)
            {
                return md5.ComputeHash(bytes);
            }
        }

        public byte[] Sha1Of(byte[] bytes)
        {
            return SHA1.HashData(bytes); // CP004 (CA5350)
        }

        public ICryptoTransform LegacyEncryptor(byte[] keyBytes, byte[] ivBytes)
        {
            TripleDESCryptoServiceProvider provider = new TripleDESCryptoServiceProvider(); // CP004 (CA5350)
            return provider.CreateEncryptor(keyBytes, ivBytes);
        }

        // Not a finding: SHA-256
        public byte[] Sha256Of(byte[] bytes)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(bytes);
            }
        }
    }
}
