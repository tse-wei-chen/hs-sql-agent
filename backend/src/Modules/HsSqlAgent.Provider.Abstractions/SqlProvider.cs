namespace HsSqlAgent.Provider.Abstractions;

public sealed class SqlProvider : ISqlProvider, IProviderDmlPreviewTransactionSource
{
    public SqlProvider(
        SqlAgentToolType type,
        IDbConnectionFactory connections,
        IProviderLowerer lowerer,
        IProviderMetadataReader metadata,
        IProviderErrorMapper errors)
        : this(type, connections, lowerer, metadata, errors, new ProviderDmlPreviewTransactionFactory())
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
