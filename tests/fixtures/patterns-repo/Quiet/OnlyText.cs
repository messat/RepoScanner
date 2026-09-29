namespace PatternsRepo.Quiet
{
    // Every pattern appears here only in comments and strings, so nothing in this file is a finding:
    // db.Users.FromSqlRaw("SELECT * FROM Users WHERE Name = '" + name + "'");
    // new BinaryFormatter(); settings.TypeNameHandling = TypeNameHandling.All;
    // handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true;
    // MD5.Create(); policy.AllowAnyOrigin(); try { Save(); } catch { }
    public class OnlyText
    {
        /* catch { } and new SHA1Managed() in a block comment */
        public string[] Examples()
        {
            return new[]
            {
                "db.Users.FromSqlRaw($\"SELECT * FROM Users WHERE Name = '{name}'\")",
                "new BinaryFormatter().Deserialize(stream)",
                "HttpClientHandler.DangerousAcceptAnyServerCertificateValidator",
                "SHA1.HashData(bytes)",
                "policy.SetIsOriginAllowed(origin => true)",
                "try { Save(); } catch { }"
            };
        }
    }
}
