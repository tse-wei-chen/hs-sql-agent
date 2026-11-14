using System.Reflection;
using System.Xml.Linq;

namespace HsSqlAgent.TestGovernance;

public static class GovernanceResources
{
    public static string Read(string name)
    {
        var assembly = typeof(GovernanceResources).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(path => path.EndsWith("." + name, StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd();
    }
}

public static class PackageConsumerSmoke
{
    public static void Run(string root, string packageSource)
    {
        packageSource = Path.GetFullPath(packageSource);
        CommandProcess.Run("dotnet", ["build", "backend/src/ToolBox/ToolBox.csproj", "--configuration", "Release"], root, echo: true);
        if (!File.ReadAllText(Path.Combine(root, "Dockerfile")).Contains(
                "COPY backend/src/Modules/HsSqlAgent.Hosting/HsSqlAgent.Hosting.csproj ./backend/src/Modules/HsSqlAgent.Hosting/", StringComparison.Ordinal))
            throw new GovernanceException("Dockerfile is missing the standard Hosting project");
        foreach (var id in new[] { "HsSqlAgent.Approvals.Abstractions", "HsSqlAgent.Approvals.Webhook", "HsSqlAgent.Hosting" })
        {
            if (!Directory.GetFiles(packageSource, id + ".*.nupkg").Any(path => !path.EndsWith(".symbols.nupkg", StringComparison.Ordinal)))
                CommandProcess.Run("dotnet", ["pack", $"backend/src/Modules/{id}/{id}.csproj", "--configuration", "Release", "--output", packageSource], root, echo: true);
        }
        var packages = Directory.GetFiles(packageSource, "HsSqlAgent.Server.*.nupkg")
            .Where(path => !path.EndsWith(".symbols.nupkg", StringComparison.Ordinal)).ToArray();
        if (packages.Length != 1) throw new GovernanceException("Expected one unambiguous Server package");
        var version = Path.GetFileName(packages[0])["HsSqlAgent.Server.".Length..^".nupkg".Length];
        if (!int.TryParse(version.Split('.')[0], out var major) || major < 2)
            throw new GovernanceException($"The F# SqlCore rewrite requires a package major of at least 2: {version}");
        var temporary = Directory.CreateTempSubdirectory("hssqlagent-consumer-");
        try
        {
            var directory = temporary.FullName;
            CommandProcess.Run("dotnet", ["new", "web", "--framework", "net10.0", "--no-restore", "--output", directory], echo: true);
            var config = new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "local-packages"), new XAttribute("value", packageSource)),
                new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json")))));
            var configPath = Path.Combine(directory, "NuGet.Config");
            config.Save(configPath);
            CommandProcess.Run("dotnet", ["add", directory, "package", "HsSqlAgent.Hosting", "--version", version, "--source", packageSource, "--no-restore"], echo: true);
            File.WriteAllText(Path.Combine(directory, "Program.cs"), GovernanceResources.Read("PackageConsumer.cs.txt"));
            CommandProcess.Run("dotnet", ["restore", directory, "--configfile", configPath, "--no-cache"], echo: true);
            CommandProcess.Run("dotnet", ["build", directory, "--configuration", "Release", "--no-restore"], echo: true);
            CommandProcess.Run("dotnet", ["run", "--project", directory, "--configuration", "Release", "--no-build"], echo: true);
            PackageCompatibility.VerifyApi(root, packageSource, version);
        }
        finally { temporary.Delete(recursive: true); }
    }
}
