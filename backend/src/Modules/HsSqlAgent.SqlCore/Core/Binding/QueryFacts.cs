using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Binding;

public sealed record QueryAliasFact(string Alias, string Target, int ScopeId);

public sealed record QueryFacts(
    ImmutableHashSet<string> ReferencedTables,
    ImmutableArray<QueryAliasFact> Aliases,
    bool ContainsSubquery,
    bool ContainsCte);
