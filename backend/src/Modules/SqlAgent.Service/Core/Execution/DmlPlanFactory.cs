using System.Collections.Immutable;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Builds the immutable DML plan consumed by <see cref="DmlCoordinator"/>. The mutation command is
/// already compiled/validated; this factory derives the read-only match command from the same DML
/// predicate and provider metadata so approval is bound to deterministic row identity.
/// </summary>
public sealed class DmlPlanFactory(
    IProviderMetadataReader metadataReader,
    CoreSqlCompiler? queryCompiler = null)
{
    private readonly DmlRowIdentityResolver _rowIdentityResolver = new(metadataReader);
    private readonly CoreSqlCompiler _queryCompiler = queryCompiler ?? CoreSqlCompiler.CreateDefault();

    public async Task<ValidatedDmlPlan> CreateAsync(
        string connectionString,
        DmlDefinition definition,
        CompiledSqlCommand mutationCommand,
        SqlAgentToolType sourceDialect,
        SqlPlanValidationContext validationContext,
        DmlRowIdentityAssurance assurance = DmlRowIdentityAssurance.Strict,
        TimeSpan? approvalTtl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(mutationCommand);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.TableName);

        if (definition.Operation is not (DmlOperation.Update or DmlOperation.Delete))
        {
            throw new InvalidOperationException(
                "Row-set approval planning currently supports UPDATE and DELETE only.");
        }

        var expectedKind = definition.Operation == DmlOperation.Update
            ? SqlStatementKind.Update
            : SqlStatementKind.Delete;
        if (mutationCommand.Kind != expectedKind)
        {
            throw new InvalidOperationException(
                $"Compiled mutation kind '{mutationCommand.Kind}' does not match DML operation '{definition.Operation}'.");
        }

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
            WhereColumnsAndValues = definition.WhereConditions
        };

        var matchCommand = _queryCompiler.Compile(
            matchDefinition,
            sourceDialect,
            mutationCommand.TargetProvider,
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
            approvalTtl.GetValueOrDefault(TimeSpan.FromMinutes(5)));
    }
}
