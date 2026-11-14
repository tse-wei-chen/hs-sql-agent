namespace HsSqlAgent.TestGovernance;

public static class PackageCompatibility
{
    public static void VerifyMigrations(string root)
    {
        var tag = "v" + ReleaseMetadata.Read(root, "AdminMigrationBaselineVersion");
        const string commit = "e6f289ac183a89a649acaa46c6845c763a9b6c68";
        string Git(params string[] args) => CommandProcess.Run("git", args, root).Output.Trim();
        Git("fetch", "--quiet", "--no-tags", "--depth=1", "origin", $"refs/tags/{tag}:refs/tags/{tag}");
        if (Git("rev-parse", $"refs/tags/{tag}^{{commit}}") != commit)
            throw new GovernanceException("Immutable migration baseline tag moved");
        var paths = Git("ls-tree", "-r", "--name-only", $"refs/tags/{tag}", "--",
            "backend/src/Modules/HsSqlAgent.SqliteMigrations/Admin", "backend/src/Modules/HsSqlAgent.PostgresMigrations/Admin")
            .Split('\n').Where(path => System.Text.RegularExpressions.Regex.IsMatch(path, @"/\d{14}_[^/]+\.cs$")).ToArray();
        if (paths.Length < 2 || !paths.Any(path => path.Contains("/HsSqlAgent.SqliteMigrations/", StringComparison.Ordinal))
            || !paths.Any(path => path.Contains("/HsSqlAgent.PostgresMigrations/", StringComparison.Ordinal)))
            throw new GovernanceException("Published migration baseline is incomplete");
        foreach (var path in paths)
            if (Git("rev-parse", "--verify", $"HEAD:{path}") != Git("rev-parse", $"refs/tags/{tag}:{path}"))
                throw new GovernanceException($"Published migration changed: {path}; add a new migration instead");
        foreach (var path in new[]
        {
            "backend/src/Modules/HsSqlAgent.SqliteMigrations/Admin/20261018150000_AddManagementApiKeys.cs",
            "backend/src/Modules/HsSqlAgent.PostgresMigrations/Admin/20261018150000_AddManagementApiKeysPostgres.cs"
        }) Git("cat-file", "-e", $"HEAD:{path}");
        Console.WriteLine($"Preserved {paths.Length} published migration files against {tag} ({commit})");
    }

    public static void VerifyApi(string root, string packageDirectory, string version)
    {
        var baselineVersion = ReleaseMetadata.Read(root, "PackageApiBaselineVersion");
        if (version == baselineVersion || version.StartsWith(baselineVersion + "-", StringComparison.Ordinal)
            || version.Split('.')[0] != baselineVersion.Split('.')[0]) return;
        var packages = Directory.GetFiles(packageDirectory, $"HsSqlAgent.*.{version}.nupkg");
        if (packages.Length == 0) throw new GovernanceException($"No public packages for {version}");
        var temporary = Directory.CreateTempSubdirectory("hssqlagent-api-");
        try
        {
            var tools = Path.Combine(temporary.FullName, "tools");
            CommandProcess.Run("dotnet", ["tool", "install", "Microsoft.DotNet.ApiCompat.Tool", "--version", "10.0.100", "--tool-path", tools], echo: true);
            using var client = new HttpClient();
            foreach (var package in packages)
            {
                var id = Path.GetFileName(package)[..^($".{version}.nupkg".Length)];
                var lower = id.ToLowerInvariant();
                var baseline = Path.Combine(temporary.FullName, $"{id}.{baselineVersion}.nupkg");
                var url = $"https://api.nuget.org/v3-flatcontainer/{lower}/{baselineVersion}/{lower}.{baselineVersion}.nupkg";
                File.WriteAllBytes(baseline, client.GetByteArrayAsync(url).GetAwaiter().GetResult());
                CommandProcess.Run(Path.Combine(tools, OperatingSystem.IsWindows() ? "apicompat.exe" : "apicompat"),
                    ["package", package, "--baseline-package", baseline, "--enable-rule-cannot-change-parameter-name"], echo: true);
            }
        }
        finally { temporary.Delete(recursive: true); }
    }
}
