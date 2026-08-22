using System.Collections.Immutable;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Builds the immutable DML plan consumed by <see cref="DmlCoordinator"/>. Mutation and match
/// commands are both derived from the same typed DML definition, preventing approval of one target
/// or predicate from being paired with a different externally supplied mutation command.
/// </summary>
public sealed class DmlPlanFactory(
    IProviderMetadataReader metadataReader,
    CoreDmlCompiler? dmlCompiler = null,
    CoreSqlCompiler? queryCompiler = null)
{
    private readonly DmlRowIdentityResolver _rowIdentityResolver = new(metadataReader);
    private readonly CoreDmlCompiler _dmlCompiler = dmlCompiler ?? CoreDmlCompiler.CreateDefault();
    private readonly CoreSqlCompiler _queryCompiler = queryCompiler ?? CoreSqlCompiler.CreateDefault();

    public async Task<ValidatedDmlPlan> CreateAsync(
        string connectionString,
        DmlDefinition definition,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlCompilationPolicy? compilationPolicy = null,
        DmlRowIdentityAssurance assurance = DmlRowIdentityAssurance.Strict,
        int maxAffectedRows = 0,
        TimeSpan? approvalTtl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.TableName);
        if (maxAffectedRows < 0)
            throw new ArgumentOutOfRangeException(nameof(maxAffectedRows));

        if (definition.Operation is not (DmlOperation.Update or DmlOperation.Delete))
        {
            throw new InvalidOperationException(
                "Row-set approval planning currently supports UPDATE and DELETE only.");
        }

        var mutationCommand = _dmlCompiler.Compile(
            definition,
            sourceDialect,
            targetProvider,
            validationContext,
            compilationPolicy);

        var identityColumns = await _rowIdentityResolver.ResolveAsync(
            connectionString,
            definition.TableName,
            assurance,
            cancellationToken);

        var selectColumns = identityColumns.IsDefaultOrEmpty
            ? new List<SelectCondition>
            {
                new ConstantSelectCondition { Constant = 1, Alias = "__match" }
            }
            : identityColumns
                .Select(column => (SelectCondition)new FieldSelectCondition { FieldName = column })
                .ToList();

        var matchDefinition = new QueryDefinition
        {
            TableName = definition.TableName,
            SelectColumns = selectColumns,
            WhereColumnsAndValues = definition.WhereConditions,
            // We only need enough identities to either prove the complete approved set (<= max)
            // or prove that policy is exceeded. This prevents an unbounded PK materialization just
            // to discover afterward that the mutation should have been rejected.
            Limit = maxAffectedRows > 0
                ? maxAffectedRows == int.MaxValue ? int.MaxValue : maxAffectedRows + 1
                : null
        };

        var matchCommand = _queryCompiler.Compile(
            matchDefinition,
            sourceDialect,
            targetProvider,
            validationContext,
            new SqlExecutionPlanPolicy());

        var fingerprint = DmlFingerprintService.ComputePlanFingerprint(
            mutationCommand,
            validationContext.PolicyVersion);

        return new ValidatedDmlPlan(
            definition.Operation,
            definition.TableName,
            mutationCommand,
            matchCommand,
            identityColumns,
            assurance,
            fingerprint,
            validationContext.PolicyVersion,
            approvalTtl.GetValueOrDefault(TimeSpan.FromMinutes(5)),
            maxAffectedRows);
    }
}
