using System.Collections.Immutable;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlReturningProjectionTests
{
    [Fact]
    public void FromColumns_ClassifiesTargetColumnsAsCanonicalColumnItems()
    {
        var columns = ImmutableArray.Create(
            SqlIdentifier.Unquoted("id", SourceSpan.Unknown),
            SqlIdentifier.Unquoted("name", SourceSpan.Unknown));

        var projection = DmlReturningProjection.FromColumns(columns);

        Assert.Collection(
            projection,
            item => Assert.Equal("id", Assert.Single(Assert.IsType<DmlReturningColumnItem>(item).Identifier.Parts).Value),
            item => Assert.Equal("name", Assert.Single(Assert.IsType<DmlReturningColumnItem>(item).Identifier.Parts).Value));
    }

    [Fact]
    public void FromColumns_ClassifiesLoneWildcardAsDedicatedCanonicalItem()
    {
        var projection = DmlReturningProjection.FromColumns(
            ImmutableArray.Create(SqlIdentifier.Unquoted("*", SourceSpan.Unknown)));

        Assert.IsType<DmlReturningWildcardItem>(Assert.Single(projection));
    }

    [Fact]
    public void FromColumns_RejectsWildcardMixedWithExplicitColumns()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            DmlReturningProjection.FromColumns(
                ImmutableArray.Create(
                    SqlIdentifier.Unquoted("*", SourceSpan.Unknown),
                    SqlIdentifier.Unquoted("id", SourceSpan.Unknown))));

        Assert.Contains("cannot be mixed", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
