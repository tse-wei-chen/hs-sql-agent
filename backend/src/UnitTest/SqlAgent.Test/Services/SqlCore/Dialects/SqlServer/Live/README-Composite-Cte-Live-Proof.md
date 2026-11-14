# SQL Server composite CTE live proof: test ownership

Formerly this single test file held **2,103 lines**, including large
parameterized SQL Server physical-key/CTE proof scenarios. It now uses
one `public sealed partial class SqlCoreSqlServerCompositeCteLiveProofTests`
over six focused files. Source method bodies, `[Fact]`, `[Theory]` and
all `[InlineData]` attributes were preserved verbatim; existing fully
qualified xUnit test names and provider CI selectors are unchanged.

| File suffix | Physical proof being checked |
| --- | --- |
| (main) | Ordered composite physical UNIQUE key, CTE alias/derived hops and the primary live transaction matrix |
| `.IndexesAndAliases.cs` | Nonunique/filtered/disabled physical index evidence, types, alias-based false proof |
| `.TextSemantics.cs` | Native linguistic collation, text length and exact SQL Server equality |
| `.NullableKeys.cs` | Nullable source UNIQUE key, ANSI equality and shared test connection factory |
| `.CrossPhysicalJoins.cs` | Reversed index ordinal, cross-table physical UNIQUE joined-key proofs and fail-closed secondary catalogs |
| `.RowsetRevalidation.cs` | BIGINT physical joins and correlated EXISTS/anti-join, row-set drift and commit safety |

All six partial files retain namespace `SqlAgent.Test.Services`. The
private `SqlServerConnectionFactory` remains nested in the *same logical
test class*, so code in other partials can reuse it without changing
sharing, disposal, SQL, CI filters or test parallelism.

The large parameterized primary matrix remains in the main file on
purpose: each inline case has specific expected SQL Server semantics.
Any future conversion to generated `MemberData` must prove unchanged
case identity, case count, diagnostic expectations and physical parity
rather than hiding hundreds of proofs behind a generic test helper.

CI gates: full `sqlagent-test`, SQL Server fence, syntax/parity jobs
and SqlCore baseline against the exact final HEAD.
