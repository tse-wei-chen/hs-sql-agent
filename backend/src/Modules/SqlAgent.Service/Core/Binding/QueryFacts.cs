using System.Collections.Immutable;

namespace SqlAgent.Service.Core.Binding;

public sealed record QueryAliasFact(string Alias, string Target, int ScopeId);

public sealed record QueryFacts(
    ImmutableHashSet<string> ReferencedTables,
    ImmutableArray<QueryAliasFact> Aliases,
    bool ContainsSubquery,
    bool ContainsCte);
