using System;
using System.Threading.Tasks;

namespace Foundatio.Utility;

public interface IAsyncLifetime : IAsyncDisposable
{
    Task InitializeAsync();
}
