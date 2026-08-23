using System.Collections.Immutable;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Builds the immutable DML plan consumed by <see cref="DmlCoordinator"/>. Mutation and match
/// commands are both derived from the same typed DML definition and resolved physical target,
/// preventing approval of one target or predicate from being paired with a different mutation.
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

        var identity = await _rowIdentityResolver.ResolveTargetAsync(
            connectionString,
            definition.TableName,
            assurance,
            cancellationToken);
        var resolvedDefinition = WithResolvedTarget(definition, identity.QualifiedTableName);
        var parsedMutation = new ParsedStatement(
            DmlDefinitionCoreMapper.Map(resolvedDefinition),
            sourceDialect);

        var mutationCommand = _dmlCompiler.Compile(
            parsedMutation,
            targetProvider,
            validationContext,
            compilationPolicy);

        var identityColumns = identity.Columns;
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
            TableName = identity.QualifiedTableName,
            SelectColumns = selectColumns,
            WhereColumnsAndValues = resolvedDefinition.WhereConditions,
            // We only need enough identities to either prove the complete approved set (<= max)
            // or prove that policy is exceeded. This prevents an unbounded PK materialization just
            // to discover afterward that the mutation should have been rejected.
            Limit = maxAffectedRows > 0
                ? maxAffectedRows == int.MaxValue ? int.MaxValue : maxAffectedRows + 1
                : null
        };
        var parsedMatch = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(matchDefinition),
            sourceDialect);

        var matchCommand = _queryCompiler.Compile(
            parsedMatch,
            targetProvider,
            validationContext,
            new SqlExecutionPlanPolicy());

        var fingerprint = DmlFingerprintService.ComputePlanFingerprint(
            mutationCommand,
            validationContext.PolicyVersion);

        return new ValidatedDmlPlan(
            resolvedDefinition.Operation,
            identity.QualifiedTableName,
            mutationCommand,
            matchCommand,
            identityColumns,
            assurance,
            fingerprint,
            validationContext.PolicyVersion,
            approvalTtl.GetValueOrDefault(TimeSpan.FromMinutes(5)),
            maxAffectedRows);
    }

    private static DmlDefinition WithResolvedTarget(
        DmlDefinition definition,
        string qualifiedTableName) => new()
    {
        Operation = definition.Operation,
        TableName = qualifiedTableName,
        WhereConditions = definition.WhereConditions,
        Values = definition.Values,
        Columns = definition.Columns,
        MultiValues = definition.MultiValues,
        FromQuery = definition.FromQuery,
        ConfirmToken = null
    };
}
