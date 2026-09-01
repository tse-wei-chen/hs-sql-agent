using Admin.Service.Models;
using Moq;

namespace HsSqlAgent.Server.Test.Services;

internal static class SyntaxBoundaryTestSupport
{
    public static Mock<ISqlProvider> Provider(SqlAgentToolType type)
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(type);
        return provider;
    }

    public static SecurityPolicyModel Policy() => new()
    {
        QueryMaxRows = 100,
        QueryTimeoutSeconds = 30,
        RequireWhereForUpdate = true,
        RequireWhereForDelete = true,
        AllowFullTableUpdate = false,
        AllowFullTableDelete = false,
        DmlMaxAffectedRows = 100
    };

    public static IReadOnlySet<string> AllowedTables(string csv) =>
        csv.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
