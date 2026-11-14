using System.Text.Json.Nodes;

namespace HsSqlAgent.TestGovernance;

public sealed record AdvisoryEvidence(int NugetTotal, int NugetHigh, int NodeHigh, int NodeCritical);

public static class DependencySecurity
{
    public static AdvisoryEvidence Verify(string nugetJson, string nodeJson)
    {
        var nuget = JsonNode.Parse(nugetJson);
        var node = JsonNode.Parse(nodeJson);
        if (nuget?["version"]?.ToString() != "1" || nuget["projects"] is not JsonArray projects)
            throw new GovernanceException("NuGet vulnerability report is missing or malformed");
        int total = 0, high = 0;
        foreach (var project in projects)
        foreach (var framework in project?["frameworks"]?.AsArray() ?? [])
        foreach (var kind in new[] { "topLevelPackages", "transitivePackages" })
        foreach (var package in framework?[kind]?.AsArray() ?? [])
        foreach (var vulnerability in package?["vulnerabilities"]?.AsArray() ?? [])
        {
            total++;
            var severity = vulnerability?["severity"]?.ToString() ?? "";
            if (severity.Equals("high", StringComparison.OrdinalIgnoreCase) || severity.Equals("critical", StringComparison.OrdinalIgnoreCase)
                || (int.TryParse(severity, out var number) && number >= 2)) high++;
        }
        if (node?["error"] is not null || node?["metadata"]?["vulnerabilities"] is not JsonObject counts)
            throw new GovernanceException("pnpm advisory metadata is missing or registry audit failed");
        int Count(string key) => counts[key] is null ? 0 : int.Parse(counts[key]!.ToString());
        var result = new AdvisoryEvidence(total, high, Count("high"), Count("critical"));
        Console.WriteLine($"NuGet advisories: {total} ({high} high/critical); pnpm: {result.NodeHigh} high, {result.NodeCritical} critical");
        if (high > 0 || result.NodeHigh > 0 || result.NodeCritical > 0)
            throw new GovernanceException("High/critical dependency advisories block release evidence");
        return result;
    }

    public static void Audit(string root, string output)
    {
        Directory.CreateDirectory(output);
        var solution = Path.Combine(root, "backend", "hs-sql-agent.slnx");
        CommandProcess.Run("dotnet", ["restore", solution, "--verbosity", "quiet"]);
        var nuget = CommandProcess.Run("dotnet", ["package", "list", "--project", solution, "--no-restore", "--vulnerable",
            "--include-transitive", "--format", "json", "--output-version", "1"]);
        File.WriteAllText(Path.Combine(output, "nuget-vulnerabilities.json"), nuget.Output);
        var node = CommandProcess.Run("pnpm", ["audit", "--prod", "--json"], Path.Combine(root, "frontend"), allowFailure: true);
        File.WriteAllText(Path.Combine(output, "pnpm-production-vulnerabilities.json"), node.Output);
        if (node.ExitCode > 1) throw new GovernanceException($"pnpm audit failed ({node.ExitCode}): {node.Error}");
        Verify(nuget.Output, node.Output);
    }
}
