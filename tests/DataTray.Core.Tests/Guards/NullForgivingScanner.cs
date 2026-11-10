using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DataTray.Core.Tests.Guards;

// Finds the null-forgiving operator (x!) by syntax tree, so `!=`, `!flag` and text in strings or comments never count.
// ponytail: parsed without preprocessor symbols, so a `!` inside an inactive #if branch is not seen.
internal static class NullForgivingScanner
{
    private static readonly string[] Roots = ["src", "plugins", "tests"];

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    public static IReadOnlyList<int> Lines(string source) =>
        CSharpSyntaxTree.ParseText(source, ParseOptions)
            .GetRoot()
            .DescendantNodes()
            .Where(node => node.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            .Select(node => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .ToList();

    /// <summary>Repository-relative path (forward slashes) to the lines of every file that has the operator.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<int>> ScanRepository(string repoRoot) =>
        Roots
            .SelectMany(root => Directory.EnumerateFiles(Path.Combine(repoRoot, root), "*.cs", SearchOption.AllDirectories))
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))
            .Where(file => !file.Split('/').Any(part => part is "bin" or "obj"))
            .Select(file => (File: file, Lines: Lines(File.ReadAllText(Path.Combine(repoRoot, file)))))
            .Where(found => found.Lines.Count > 0)
            .ToDictionary(found => found.File, found => found.Lines);
}
