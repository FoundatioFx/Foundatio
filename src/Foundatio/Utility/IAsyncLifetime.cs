using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Foundatio.Utility {
    public interface IAsyncLifetime : IAsyncDisposable {
        Task InitializeAsync();
    }
}