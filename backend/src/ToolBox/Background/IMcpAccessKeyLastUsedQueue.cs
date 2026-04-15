using System.Collections.Generic;
using System.Threading;

namespace ToolBox.Background;

public interface IMcpAccessKeyLastUsedQueue
{
	bool TryEnqueue(int keyId);
	IAsyncEnumerable<int> DequeueAllAsync(CancellationToken cancellationToken);
}
