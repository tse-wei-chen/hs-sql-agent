using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;

namespace SqlAgent.Service.Factories;

/// <summary>
/// Management-side connection-string construction kept separate from the Core provider runtime
/// contract so UI/API DTO concerns do not leak into ISqlProvider.
/// </summary>
public interface ISqlConnectionStringFactory
{
    string BuildConnectionString(
        SqlAgentToolType provider,
        BuildDbConnectionModelBase model);
}
