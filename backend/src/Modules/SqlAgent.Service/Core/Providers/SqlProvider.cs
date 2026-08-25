using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Providers;

/// <summary>
/// Core-owned provider composition. Provider identity, connection creation, lowering, metadata and
/// execution-error mapping are explicit collaborators; no legacy strategy behavior is exposed here.
/// </summary>
public sealed class SqlProvider : ISqlProvider, IProviderDmlPreviewTransactionSource
{
    public SqlProvider(
        SqlAgentToolType type,
        IDbConnectionFactory connections,
        IProviderLowerer lowerer,
        IProviderMetadataReader metadata,
        IProviderErrorMapper errors)
        : this(
            type,
            connections,
            lowerer,
            metadata,
            errors,
            new ProviderDmlPreviewTransactionFactory())
    {
    }

    public SqlProvider(
        SqlAgentToolType type,
        IDbConnectionFactory connections,
        IProviderLowerer lowerer,
        IProviderMetadataReader metadata,
        IProviderErrorMapper errors,
        IDmlPreviewTransactionFactory previewTransactions)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(lowerer);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(previewTransactions);

        Type = type;
        Connections = connections;
        Lowerer = lowerer;
        Metadata = metadata;
        Errors = errors;
        PreviewTransactions = previewTransactions;
    }

    public SqlAgentToolType Type { get; }
    public IDbConnectionFactory Connections { get; }
    public IProviderLowerer Lowerer { get; }
    public IProviderMetadataReader Metadata { get; }
    public IProviderErrorMapper Errors { get; }
    public IDmlPreviewTransactionFactory PreviewTransactions { get; }
}
