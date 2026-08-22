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
/// Typed UPDATE/DELETE compiler. Mapping, binding, normalization, authorization and capability
/// validation all run before SqlKata lowering. INSERT remains fail-closed until its complete
/// structured semantics are represented in the Core AST.
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
        new SqlAstBinder(),
        CoreSqlNormalizer.CreateDefault(),
        new CoreSqlPlanValidator());

    public CompiledSqlCommand Compile(
        DmlDefinition definition,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlCompilationPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(validationContext);
        policy ??= new DmlCompilationPolicy();

        ValidateMutationPolicy(definition, policy);

        var parsed = new ParsedStatement(
            DmlDefinitionCoreMapper.Map(definition),
            sourceDialect);
        var bound = _binder.Bind(parsed);
        var canonical = _normalizer.Normalize(bound, targetProvider);
        var validated = _validator.Validate(canonical, validationContext);
        var executable = new ExecutableSqlPlan(
            validated.Statement,
            validated.Facts,
            validated.SourceDialect,
            validated.TargetProvider,
            validated.PolicyVersion);

        var command = new SqlKataDmlLowerer(targetProvider).Lower(executable);
        var expectedKind = definition.Operation switch
        {
            DmlOperation.Update => SqlStatementKind.Update,
            DmlOperation.Delete => SqlStatementKind.Delete,
            _ => throw new SqlCompilationException(
                $"DML operation '{definition.Operation}' is not supported by the Core DML compiler.")
        };
        if (command.Kind != expectedKind)
            throw new SqlCompilationException(
                $"Core DML lowerer produced {command.Kind} for {definition.Operation}.");
        return command;
    }

    private static void ValidateMutationPolicy(
        DmlDefinition definition,
        DmlCompilationPolicy policy)
    {
        var hasWhere = definition.WhereConditions is { Count: > 0 };
        switch (definition.Operation)
        {
            case DmlOperation.Update when !hasWhere
                && (policy.RequireWhereForUpdate || !policy.AllowFullTableUpdate):
                throw new UnauthorizedAccessException(
                    "Security policy denies UPDATE without WHERE.");
            case DmlOperation.Delete when !hasWhere
                && (policy.RequireWhereForDelete || !policy.AllowFullTableDelete):
                throw new UnauthorizedAccessException(
                    "Security policy denies DELETE without WHERE.");
            case DmlOperation.Update:
            case DmlOperation.Delete:
                return;
            case DmlOperation.Insert:
                throw new SqlCompilationException(
                    "INSERT is not yet supported by the Core DML compiler.");
            default:
                throw new SqlCompilationException(
                    $"Unsupported DML operation '{definition.Operation}'.");
        }
    }
}
