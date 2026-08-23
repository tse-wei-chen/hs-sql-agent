using SqlAgent.Service.Core.Analysis;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Lowering;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Normalization;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Pipeline;

public sealed record DmlCompilationPolicy(
    bool RequireWhereForUpdate = true,
    bool RequireWhereForDelete = true,
    bool AllowFullTableUpdate = false,
    bool AllowFullTableDelete = false);

/// <summary>
/// Typed INSERT/UPDATE/DELETE compiler. The compiler boundary starts at ParsedStatement; the
/// DmlDefinition overload remains only as a strangler adapter for legacy callers.
/// </summary>
public sealed class CoreDmlCompiler(
    ISqlBinder binder,
    ISqlNormalizer normalizer,
    ISqlPlanValidator validator)
{
    private readonly ISqlBinder _binder = binder;
    private readonly ISqlNormalizer _normalizer = normalizer;
    private readonly ISqlPlanValidator _validator = validator;

    public static CoreDmlCompiler CreateDefault() => new(
        new CoreDmlBinder(),
        new CoreDmlNormalizer(),
        new CoreDmlPlanValidator());

    [Obsolete("Map DmlDefinition to ParsedStatement first, then call Compile(ParsedStatement, ...).")]
    public CompiledSqlCommand Compile(
        DmlDefinition definition,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlCompilationPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var parsed = new ParsedStatement(
            DmlDefinitionCoreMapper.Map(definition),
            sourceDialect);
        return Compile(parsed, targetProvider, validationContext, policy);
    }

    public CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlCompilationPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(validationContext);
        policy ??= new DmlCompilationPolicy();

        ValidateMutationPolicy(parsed.Statement, policy);

        var bound = _binder.Bind(parsed);
        var canonical = _normalizer.Normalize(bound, targetProvider);
        var validated = _validator.Validate(canonical, validationContext);
        var executable = new ExecutableSqlPlan(
            validated.Statement,
            validated.Facts,
            validated.SourceDialect,
            validated.TargetProvider,
            validated.PolicyVersion);

        var command = validated.Statement is InsertStatement insert
            ? new SqlKataInsertLowerer(targetProvider).Lower(executable, insert)
            : new SqlKataDmlLowerer(targetProvider).Lower(executable);
        var expectedKind = parsed.Statement switch
        {
            InsertStatement => SqlStatementKind.Insert,
            UpdateStatement => SqlStatementKind.Update,
            DeleteStatement => SqlStatementKind.Delete,
            _ => throw new SqlCompilationException(
                $"Statement '{parsed.Statement.GetType().Name}' is not supported by the Core DML compiler.")
        };
        if (command.Kind != expectedKind)
            throw new SqlCompilationException(
                $"Core DML lowerer produced {command.Kind} for expected {expectedKind} statement.");
        return command;
    }

    private static void ValidateMutationPolicy(
        SqlStatement statement,
        DmlCompilationPolicy policy)
    {
        switch (statement)
        {
            case UpdateStatement { Predicate: null }
                when policy.RequireWhereForUpdate || !policy.AllowFullTableUpdate:
                throw new UnauthorizedAccessException(
                    "Security policy denies UPDATE without WHERE.");
            case DeleteStatement { Predicate: null }
                when policy.RequireWhereForDelete || !policy.AllowFullTableDelete:
                throw new UnauthorizedAccessException(
                    "Security policy denies DELETE without WHERE.");
            case InsertStatement:
            case UpdateStatement:
            case DeleteStatement:
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported DML statement '{statement.GetType().Name}'.");
        }
    }
}
