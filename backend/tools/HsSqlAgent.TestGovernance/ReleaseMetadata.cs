using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HsSqlAgent.TestGovernance;

public static class ReleaseMetadata
{
    public static string Read(string root, string property)
    {
        var values = XDocument.Load(Path.Combine(root, "backend", "Directory.Build.props"))
            .Descendants(property).Select(element => element.Value.Trim()).ToArray();
        if (values.Length != 1 || !Regex.IsMatch(values[0], @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"))
            throw new GovernanceException($"Invalid, missing or duplicate release property: {property}");
        return values[0];
    }
}
