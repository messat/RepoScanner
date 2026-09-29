using System;
using System.IO;

namespace PatternsRepo.Errors
{
    // CP006: empty catch blocks. Insecure on purpose: this file is never built. The markers are outside the
    // braces, so they do not count as a reason for the block being empty.
    public class Catches
    {
        public void SaveReport(string path, string text)
        {
            try
            {
                File.WriteAllText(path, text);
            }
            catch (IOException) // CP006
            {
            }
        }

        public void DeleteTemporaryFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch // CP006
            {
            }
        }

        // Not findings: a comment inside saying why, and a rethrow
        public void DeleteIfThere(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // The file may already be gone, which is what this method wants
            }
        }

        public string Load(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch
            {
                throw;
            }
        }
    }
}
