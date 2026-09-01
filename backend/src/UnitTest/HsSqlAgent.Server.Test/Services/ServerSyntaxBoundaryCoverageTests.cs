using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class ServerSyntaxBoundaryCoverageTests
{
    [Fact]
    public void ServerSyntaxBoundary_HasStableQueryAndDmlFloor()
    {
        var query =
            DialectSyntaxBoundaryMatrixTests
                .SixDialectBoundaryMatrix()
                .Count();
        var insertValues =
            DmlSyntaxBoundaryMatrixTests
                .SixDialectInsertValuesBoundaryMatrix()
                .Count();
        var rowSetMutations =
            DmlRowSetSyntaxBoundaryMatrixTests
                .SixDialectRowSetBoundaryMatrix()
                .Count();
        var insertSelectFailClosed =
            DmlSyntaxBoundaryMatrixTests
                .SixDialectInsertSelectFailClosedMatrix()
                .Count();

        Assert.Equal(18, query);
        Assert.Equal(18, insertValues);
        Assert.Equal(12, rowSetMutations);
        Assert.Equal(6, insertSelectFailClosed);
        Assert.Equal(
            54,
            query +
            insertValues +
            rowSetMutations +
            insertSelectFailClosed);
    }
}
