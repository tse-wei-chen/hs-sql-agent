namespace HsSqlAgent.Provider.Abstractions;

public sealed class SqlProvider : ISqlProvider, IProviderDmlPreviewTransactionSource
{
    public SqlProvider(
        SqlAgentToolType type,
        IDbConnectionFactory connections,
        IProviderMetadataReader metadata,
        IProviderErrorMapper errors)
        : this(type, connections, metadata, errors, new ProviderDmlPreviewTransactionFactory())
    {
    }

    public SqlProvider(
        SqlAgentToolType type,
        IDbConnectionFactory connections,
        IProviderMetadataReader metadata,
        IProviderErrorMapper errors,
        IDmlPreviewTransactionFactory previewTransactions)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(previewTransactions);
        Type = type;
        Connections = connections;
        Metadata = metadata;
        Errors = errors;
        PreviewTransactions = previewTransactions;
    }

    public SqlAgentToolType Type { get; }
    public IDbConnectionFactory Connections { get; }
    public IProviderMetadataReader Metadata { get; }
    public IProviderErrorMapper Errors { get; }
    public IDmlPreviewTransactionFactory PreviewTransactions { get; }
}
