using System.Diagnostics;
using System.Text;

namespace HsSqlAgent.TestGovernance;

/// <summary>
/// Source-line budget based on exact committed Git revisions, not working-tree
/// state. This is informational; it does not claim semantic or performance proof.
/// </summary>
public static class SqlCoreCodeDelta
{
    public static readonly string[] Categories =
        ["production", "tests", "generatedMigrations", "docs", "other"];

    public static string Classify(string path)
    {
        var normalized = path.Replace('\\', '/');
        var parts = normalized.ToLowerInvariant().Split('/');
        var name = parts[^1];
        if (normalized.StartsWith(".github/scripts/", StringComparison.Ordinal)
            && name.StartsWith("test_", StringComparison.Ordinal) && name.EndsWith(".py", StringComparison.Ordinal))
            return "tests";
        if (new[] { ".test.cs", ".spec.cs", ".test.ts", ".spec.ts", ".test.tsx", ".spec.tsx" }
            .Any(name.EndsWith))
            return "tests";
        if (normalized.StartsWith("backend/src/UnitTest/", StringComparison.Ordinal)
            || normalized.StartsWith("backend/tests/", StringComparison.Ordinal)
            || normalized.Contains("/__tests__/", StringComparison.Ordinal)
            || normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase))
            return "tests";
        if (new[] { ".md", ".mdx", ".rst", ".txt" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
            || parts[0] == "docs")
            return "docs";
        if (normalized.StartsWith("backend/src/Modules/", StringComparison.Ordinal))
        {
            if (parts.Any(s => s is "migrations" or "generated" or "obj" or "bin")
                || new[] { ".g.cs", ".g.fs", ".designer.cs", ".assemblyinfo.cs" }.Any(name.EndsWith))
                return "generatedMigrations";
            if (new[] { ".fs", ".fsi", ".fsx", ".cs" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                return "production";
        }
        return "other";
    }

    public static CodeDeltaReport Summarize(string repository, string revisionBase, string revisionHead)
    {
        var b = GitText(repository, "rev-parse", "--verify", revisionBase + "^{commit}").Trim();
        var h = GitText(repository, "rev-parse", "--verify", revisionHead + "^{commit}").Trim();
        var bytes = GitBytes(repository, "diff", "--numstat", "-z", "--no-renames", b, h, "--");
        var entries = Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var categories = Categories.ToDictionary(c => c, _ => new DeltaCounts(), StringComparer.Ordinal);
        var binary = 0;
        foreach (var entry in entries)
        {
            var parts = entry.Split('\t', 3);
            if (parts.Length != 3)
                throw new GovernanceException("Malformed git --numstat -z record");
            var bucket = categories[Classify(parts[2])];
            bucket.Files++;
            if (parts[0] == "-" && parts[1] == "-")
                binary++;
            else if (int.TryParse(parts[0], out var added) && added >= 0
                     && int.TryParse(parts[1], out var deleted) && deleted >= 0)
            {
                bucket.Added += added;
                bucket.Deleted += deleted;
            }
            else throw new GovernanceException("Malformed git line count");
        }
        var total = new DeltaCounts
        {
            Added = categories.Values.Sum(c => c.Added),
            Deleted = categories.Values.Sum(c => c.Deleted),
            Files = categories.Values.Sum(c => c.Files),
        };
        return new CodeDeltaReport(
            1, b, h, "git diff --numstat -z --no-renames, committed revisions only",
            categories, binary, total);
    }

    public static string Markdown(CodeDeltaReport report)
    {
        var lines = new List<string>
        {
            "## SqlCore PR code delta (source lines, not performance)",
            $"Base: `{report.BaseCommit}`",
            $"Head: `{report.HeadCommit}`",
            "",
            "| Category | Added | Deleted | Net | Files |",
            "|---|---:|---:|---:|---:|",
        };
        foreach (var key in Categories)
        {
            var b = report.Categories[key];
            lines.Add($"| {key} | +{b.Added} | -{b.Deleted} | {b.Net:+#;-#;0} | {b.Files} |");
        }
        var t = report.Total;
        lines.Add($"| **Total** | **+{t.Added}** | **-{t.Deleted}** | **{t.Net:+#;-#;0}** | **{t.Files}** |");
        if (report.BinaryFiles > 0)
            lines.Add($"\n**Caution:** {report.BinaryFiles} binary changed file(s) have unknown line counts, not zero-size change.");
        lines.AddRange([
            "",
            "Generated/migrations and tests/docs are excluded from production totals.",
            "Moves across categories count as deletion + addition; no rename hiding.",
            "This report does not establish safety, semantic complexity, or speedup. See the paired 48-workload SqlCore comparison artifact for performance.",
        ]);
        return string.Join("\n", lines);
    }

    private static string GitText(string repository, params string[] args) =>
        Encoding.UTF8.GetString(GitBytes(repository, args));

    private static byte[] GitBytes(string repository, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(repository);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new GovernanceException("Could not launch git");
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new GovernanceException($"git {string.Join(' ', args)} failed: {error}");
        return output.ToArray();
    }
}

public sealed class DeltaCounts
{
    public int Added { get; set; }
    public int Deleted { get; set; }
    public int Files { get; set; }
    public int Net => Added - Deleted;
}

public sealed record CodeDeltaReport(
    int SchemaVersion, string BaseCommit, string HeadCommit, string Method,
    Dictionary<string, DeltaCounts> Categories, int BinaryFiles, DeltaCounts Total);
