using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Resilience;

public interface IResiliencePolicy
{
    ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default);
    ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default);
}
