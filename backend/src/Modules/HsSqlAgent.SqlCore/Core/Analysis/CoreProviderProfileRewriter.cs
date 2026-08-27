using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Applies target-runtime capability rewrites after canonical validation and before provider
/// lowering. Profile-dependent capabilities remain fail-closed unless the deployment explicitly
/// declares the required runtime contract.
/// </summary>
internal static class CoreProviderProfileRewriter
{
    public static SqlStatement Rewrite(
        SqlStatement statement,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ValidateProfile(targetProvider, targetProfile);

        if (targetProvider != SqlAgentToolType.MsSqlServer)
            return statement;

        return RewriteStatement(statement, targetProvider, targetProfile);
    }

    public static void ValidateProfile(
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        if (targetProfile is null) return;
        if (targetProfile.Provider != targetProvider)
        {
            throw new SqlCompilationException(
                $"Target capability profile declares provider {targetProfile.Provider}, " +
                $"but compilation targets {targetProvider}.");
        }
        if (targetProfile.CompatibilityLevel is < 0)
            throw new SqlCompilationException("Provider compatibility level must be non-negative.");
    }

    public static bool SupportsSqlServerRegex(
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile) =>
        targetProvider == SqlAgentToolType.MsSqlServer
        && targetProfile is
        {
            Provider: SqlAgentToolType.MsSqlServer,
            CompatibilityLevel: >= 170
        };

    private static SqlStatement RewriteStatement(
        SqlStatement statement,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile) => statement switch
    {
        SelectStatement select => RewriteSelect(select, targetProvider, targetProfile),
        QueryStatement query => query with
        {
            Head = RewriteSelect(query.Head, targetProvider, targetProfile),
            SetOperations = query.SetOperations
                .Select(operation => operation with
                {
                    Query = RewriteStatement(operation.Query, targetProvider, targetProfile)
                })
                .ToImmutableArray(),
            OrderBy = RewriteOrderBy(query.OrderBy, targetProvider, targetProfile)
        },
        InsertStatement insert => insert with
        {
            Source = insert.Source switch
            {
                InsertValuesSource values => values with
                {
                    Rows = values.Rows
                        .Select(row => row
                            .Select(value => RewriteExpression(value, targetProvider, targetProfile))
                            .ToImmutableArray())
                        .ToImmutableArray()
                },
                InsertQuerySource querySource => querySource with
                {
                    Query = RewriteStatement(querySource.Query, targetProvider, targetProfile)
                },
                _ => throw new SqlCompilationException(
                    $"Unsupported INSERT source during provider-profile rewrite: {insert.Source.GetType().Name}")
            }
        },
        UpdateStatement update => update with
        {
            Assignments = update.Assignments
                .Select(assignment => assignment with
                {
                    Value = RewriteExpression(assignment.Value, targetProvider, targetProfile)
                })
                .ToImmutableArray(),
            Predicate = update.Predicate is null
                ? null
                : RewriteExpression(update.Predicate, targetProvider, targetProfile)
        },
        DeleteStatement delete => delete with
        {
            Predicate = delete.Predicate is null
                ? null
                : RewriteExpression(delete.Predicate, targetProvider, targetProfile)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported statement during provider-profile rewrite: {statement.GetType().Name}")
    };

    private static SelectStatement RewriteSelect(
        SelectStatement select,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile) => select with
    {
        Ctes = select.Ctes
            .Select(cte => cte with
            {
                Query = RewriteStatement(cte.Query, targetProvider, targetProfile)
            })
            .ToImmutableArray(),
        Select = select.Select
            .Select(item => item with
            {
                Expression = RewriteExpression(item.Expression, targetProvider, targetProfile)
            })
            .ToImmutableArray(),
        From = select.From is null
            ? null
            : RewriteSource(select.From, targetProvider, targetProfile),
        Joins = select.Joins
            .Select(join => join with
            {
                Source = RewriteSource(join.Source, targetProvider, targetProfile),
                Predicate = join.Predicate is null
                    ? null
                    : RewriteExpression(join.Predicate, targetProvider, targetProfile)
            })
            .ToImmutableArray(),
        Where = select.Where is null
            ? null
            : RewriteExpression(select.Where, targetProvider, targetProfile),
        GroupBy = select.GroupBy
            .Select(expression => RewriteExpression(expression, targetProvider, targetProfile))
            .ToImmutableArray(),
        Having = select.Having is null
            ? null
            : RewriteExpression(select.Having, targetProvider, targetProfile),
        OrderBy = RewriteOrderBy(select.OrderBy, targetProvider, targetProfile)
    };

    private static TableSource RewriteSource(
        TableSource source,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile) => source switch
    {
        NamedTableSource => source,
        DerivedTableSource derived => derived with
        {
            Query = RewriteStatement(derived.Query, targetProvider, targetProfile)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported table source during provider-profile rewrite: {source.GetType().Name}")
    };

    private static ImmutableArray<OrderByItem> RewriteOrderBy(
        ImmutableArray<OrderByItem> orderBy,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile) => orderBy
        .Select(item => item with
        {
            Expression = RewriteExpression(item.Expression, targetProvider, targetProfile)
        })
        .ToImmutableArray();

    private static SqlExpr RewriteExpression(
        SqlExpr expression,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile) => expression switch
    {
        LiteralExpr or ColumnExpr or BoundColumnExpr or IntervalExpr => expression,
        UnaryExpr unary => unary with
        {
            Operand = RewriteExpression(unary.Operand, targetProvider, targetProfile)
        },
        BinaryExpr binary => RewriteBinary(
            binary,
            targetProvider,
            targetProfile),
        FunctionCallExpr function => RewriteFunction(function, targetProvider, targetProfile),
        FilterExpr filter => filter with
        {
            Expression = RewriteExpression(filter.Expression, targetProvider, targetProfile),
            Predicate = RewriteExpression(filter.Predicate, targetProvider, targetProfile)
        },
        WindowedExpr windowed => windowed with
        {
            Expression = RewriteExpression(windowed.Expression, targetProvider, targetProfile),
            Window = windowed.Window with
            {
                PartitionBy = windowed.Window.PartitionBy
                    .Select(item => RewriteExpression(item, targetProvider, targetProfile))
                    .ToImmutableArray(),
                OrderBy = RewriteOrderBy(windowed.Window.OrderBy, targetProvider, targetProfile)
            }
        },
        CastExpr cast => cast with
        {
            Expression = RewriteExpression(cast.Expression, targetProvider, targetProfile)
        },
        SimpleCaseExpr simpleCase => new SimpleCaseExpr(
            RewriteBranches(simpleCase.Branches, targetProvider, targetProfile),
            simpleCase.ElseExpression is null
                ? null
                : RewriteExpression(simpleCase.ElseExpression, targetProvider, targetProfile),
            simpleCase.Span),
        CaseExpr @case => @case with
        {
            Branches = RewriteBranches(@case.Branches, targetProvider, targetProfile),
            ElseExpression = @case.ElseExpression is null
                ? null
                : RewriteExpression(@case.ElseExpression, targetProvider, targetProfile)
        },
        InExpr @in => @in with
        {
            Value = RewriteExpression(@in.Value, targetProvider, targetProfile),
            Items = @in.Items
                .Select(item => RewriteExpression(item, targetProvider, targetProfile))
                .ToImmutableArray()
        },
        BetweenExpr between => between with
        {
            Value = RewriteExpression(between.Value, targetProvider, targetProfile),
            Lower = RewriteExpression(between.Lower, targetProvider, targetProfile),
            Upper = RewriteExpression(between.Upper, targetProvider, targetProfile)
        },
        IsNullExpr isNull => isNull with
        {
            Value = RewriteExpression(isNull.Value, targetProvider, targetProfile)
        },
        SubqueryExpr subquery => subquery with
        {
            Query = RewriteStatement(subquery.Query, targetProvider, targetProfile)
        },
        ExistsExpr exists => exists with
        {
            Query = RewriteStatement(exists.Query, targetProvider, targetProfile)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported expression during provider-profile rewrite: {expression.GetType().Name}")
    };

    private static BinaryExpr RewriteBinary(
        BinaryExpr binary,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        var rewritten = binary with
        {
            Left = RewriteExpression(binary.Left, targetProvider, targetProfile),
            Right = RewriteExpression(binary.Right, targetProvider, targetProfile)
        };

        if (targetProvider != SqlAgentToolType.MsSqlServer
            || !binary.Operator.Equals("||", StringComparison.OrdinalIgnoreCase))
        {
            return rewritten;
        }

        return SqlConcatCapabilityRules.EvaluateSqlServerTarget(targetProfile) switch
        {
            SqlServerConcatTargetMode.NativePipes => rewritten,
            SqlServerConcatTargetMode.PlusOperator => rewritten with { Operator = "+" },
            SqlServerConcatTargetMode.Rejected => throw new SqlCompilationException(
                SqlConcatCapabilityRules.SqlServerTargetValidationError(targetProfile)),
            _ => throw new SqlCompilationException(
                "Unsupported SQL Server concat target mode.")
        };
    }

    private static FunctionCallExpr RewriteFunction(
        FunctionCallExpr function,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        var arguments = function.Arguments
            .Select(argument => RewriteExpression(argument, targetProvider, targetProfile))
            .ToImmutableArray();
        var rewritten = function with { Arguments = arguments };

        if (!IdentifierText(function.Name).Equals("CORE_REGEX_MATCH", StringComparison.OrdinalIgnoreCase))
            return rewritten;

        if (!SupportsSqlServerRegex(targetProvider, targetProfile))
        {
            throw new SqlCompilationException(
                "SQL capability 'function.regex_match' requires a declared SQL Server target " +
                "capability profile with compatibility level 170 or above.");
        }

        return rewritten with
        {
            Name = SqlIdentifier.Unquoted("REGEXP_LIKE", function.Name.Span)
        };
    }

    private static ImmutableArray<CaseBranch> RewriteBranches(
        ImmutableArray<CaseBranch> branches,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile) => branches
        .Select(branch => branch with
        {
            Condition = RewriteExpression(branch.Condition, targetProvider, targetProfile),
            Value = RewriteExpression(branch.Value, targetProvider, targetProfile)
        })
        .ToImmutableArray();

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
