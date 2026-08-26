# HsSqlAgent.Provider.Abstractions

Contracts and shared runtime building blocks for HsSqlAgent database providers. It depends on
`HsSqlAgent.SqlCore` but does not include an ADO.NET database driver.

## Install

```bash
dotnet add package HsSqlAgent.Provider.Abstractions
```

## Consume a provider

Accept `ISqlProvider` when application code should be independent of the concrete database driver:

```csharp
using HsSqlAgent.Provider.Abstractions;

static async Task<IReadOnlyList<string>> ReadSchemasAsync(
    ISqlProvider provider,
    string connectionString,
    CancellationToken cancellationToken = default)
{
    await using var connection = provider.Connections.Create(connectionString);
    await connection.OpenAsync(cancellationToken);
    return await provider.Metadata.GetSchemasAsync(connectionString, cancellationToken);
}
```

Applications normally install a concrete `HsSqlAgent.Provider.*` package instead. This package is
intended for provider authors and code that consumes `ISqlProvider`, `IDbConnectionFactory`,
`IProviderMetadataReader` or provider error mapping abstractions. Provider authors can derive from
`SqlProviderBase` to implement connection creation and metadata discovery.

Project: https://github.com/tse-wei-chen/hs-sql-agent
