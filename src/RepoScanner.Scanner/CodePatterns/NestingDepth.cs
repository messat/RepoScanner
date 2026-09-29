using System;

namespace RepoScanner.Scanner.CodePatterns
{
    // How deeply a file nests parentheses and angle brackets, counted before it is parsed. Roslyn's parser
    // overflows the stack, which ends the whole process and cannot be caught, on about 1,500 of them nested
    // (found on 2026-09-29 with Microsoft.CodeAnalysis.CSharp 5.9.0), so CodePatternScanner does not parse a
    // file nested deeper than its limit (Issue 6 design, decision 17).
    public static class NestingDepth
    {
        // The deepest count of open parentheses and angle brackets together, so (A<(A< counts 4. Characters
        // are counted wherever they are, including in comments and strings, which can only make the count
        // higher. An angle bracket can be a comparison or a shift, which nothing closes, so angle brackets
        // start again at each ; { and }: a generic type never spans one.
        public static int Deepest(string code)
        {
            int parentheses = 0;
            int angleBrackets = 0;
            int deepest = 0;

            foreach (char character in code)
            {
                switch (character)
                {
                    case '(':
                        parentheses++;
                        break;
                    case ')':
                        parentheses = Math.Max(0, parentheses - 1);
                        break;
                    case '<':
                        angleBrackets++;
                        break;
                    case '>':
                        angleBrackets = Math.Max(0, angleBrackets - 1);
                        break;
                    case ';':
                    case '{':
                    case '}':
                        angleBrackets = 0;
                        break;
                }

                deepest = Math.Max(deepest, parentheses + angleBrackets);
            }

            return deepest;
        }
    }
}
