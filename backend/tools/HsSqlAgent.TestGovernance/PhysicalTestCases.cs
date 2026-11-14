using System.Text.RegularExpressions;

namespace HsSqlAgent.TestGovernance;

public static class PhysicalTestCases
{
    public static void Verify(string output, string framework, int expected)
    {
        if (expected < 1) throw new GovernanceException("Physical case count must be positive");
        var clean = Regex.Replace(output, "\u001b\\[[0-9;]*[a-zA-Z]", "");
        var results = clean.Split('\n').Where(line => line.Contains($"({framework}|", StringComparison.Ordinal)
            && line.Contains(" passed [", StringComparison.Ordinal)).Select(line => line.Trim()).Distinct().ToArray();
        if (results.Length != 1 || !Regex.Replace(results[0], @"\s", "").Contains($"passed[+{expected}/x0/?0]", StringComparison.Ordinal))
            throw new GovernanceException($"Physical {framework} must pass exactly {expected} cases with zero failures/skips");
    }

    public static void Run(int expected, string project, string filter)
    {
        if (expected < 1) throw new GovernanceException("Physical case count must be positive");
        var frameworks = CommandProcess.Run("dotnet", ["msbuild", project, "-getProperty:TargetFrameworks", "-nologo"]).Output.Trim();
        if (frameworks.Length == 0)
            frameworks = CommandProcess.Run("dotnet", ["msbuild", project, "-getProperty:TargetFramework", "-nologo"]).Output.Trim();
        var targets = frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (targets.Length == 0) throw new GovernanceException("No test frameworks found");
        foreach (var target in targets)
        {
            var result = CommandProcess.Run("dotnet", ["test", "--project", project, "--framework", target, "--filter", filter], echo: true);
            Verify(result.Output, target, expected);
        }
        Console.WriteLine($"Verified {expected} physical cases per framework ({string.Join(", ", targets)}), zero failures/skips");
    }
}
