using System;
using System.IO;
using System.Threading.Tasks;

namespace RepoScanner.Scanner.Tests
{
    // Runs the given code with the console redirected, and returns everything it wrote. Serilog's
    // console sink writes to Console.Out, so this captures the scanner's log lines.
    internal static class ConsoleCapture
    {
        public static async Task<string> RunAsync(Func<Task> action)
        {
            TextWriter originalOut = Console.Out;

            using (StringWriter output = new StringWriter())
            {
                Console.SetOut(output);
                try
                {
                    await action();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }

                return output.ToString();
            }
        }
    }
}
