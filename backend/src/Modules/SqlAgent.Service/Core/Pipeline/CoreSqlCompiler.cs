using SqlAgent.Service.Core.Analysis;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Lowering;
using SqlAgent.Service.Core.Normalization;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Pipeline;

/// <summary>
/// Compiler pipeline entry point. The typed boundary starts at <see cref="ParsedStatement"/> so
/// binding, normalization, validation, policy rewriting and lowering cannot be invoked with a
/// transport DTO.
/// </summary>
public sealed class CoreSqlCompiler(
    ISqlBinder binder,
    ISqlNormalizer normalizer,
    ISqlPlanValidator validator,
    ISqlExecutionPolicyRewriter policyRewriter)
{
    private readonly ISqlBinder _binder = binder;
    private readonly ISqlNormalizer _normalizer = normalizer;
    private readonly ISqlPlanValidator _validator = validator;
    private readonly ISqlExecutionPolicyRewriter _policyRewriter = policyRewriter;

    public static CoreSqlCompiler CreateDefault() => new(
        new SqlAstBinder(),
        CoreSqlNormalizer.CreateDefault(),
        new CoreSqlPlanValidator(),
        new CoreSqlExecutionPolicyRewriter());

    public CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        SqlExecutionPlanPolicy executionPolicy,
        SqlProviderCapabilityProfile? targetProfile = null)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentNullException.ThrowIfNull(executionPolicy);

        CoreProviderProfileRewriter.ValidateProfile(targetProvider, targetProfile);
        CoreSourceProfileRewriter.ValidateProfile(parsed.SourceDialect, parsed.SourceProfile);

        var bound = _binder.Bind(parsed);
        if (parsed.EnforceSourceDialectSyntax)
        {
            CoreSourceDialectValidator.Validate(bound.Statement, bound.SourceDialect);
            bound = bound with
            {
                Statement = CoreSourceProfileRewriter.Prepare(
                    bound.Statement,
                    bound.SourceDialect,
                    parsed.SourceProfile)
            };
        }

        var canonical = _normalizer.Normalize(bound, targetProvider);
        if (parsed.EnforceSourceDialectSyntax)
        {
            canonical = canonical with
            {
                Statement = CoreSourceProfileRewriter.Restore(canonical.Statement)
            };
        }
        canonical = canonical with
        {
            Statement = CoreNullOrderingRewriter.Rewrite(canonical.Statement, targetProvider)
        };
        CoreNoFromReferenceValidator.Validate(canonical.Statement, targetProvider);
        var validated = _validator.Validate(canonical, validationContext);
        var executable = _policyRewriter.Rewrite(validated, executionPolicy);
        executable = executable with
        {
            Statement = CoreProviderProfileRewriter.Rewrite(
                executable.Statement,
                targetProvider,
                targetProfile)
        };
        executable = executable with
        {
            Statement = CoreRootCteSetTailRewriter.Rewrite(executable.Statement)
        };
        CoreSqlKataBackendCompatibility.ValidateQuery(executable.Statement, targetProvider);

        IProviderLowerer lowerer = CoreSqlKataDerivedCteLowerer.CanLower(executable.Statement)
            ? new CoreSqlKataDerivedCteLowerer(targetProvider)
            : new SqlKataProviderLowerer(targetProvider);
        return lowerer.Lower(executable);
    }
}
