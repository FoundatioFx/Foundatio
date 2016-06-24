using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Utility {
    public interface IAsyncDisposable {
        Task DisposeAsync();
    }

    public static class Async {
        public static async Task<TReturn> Using<TResource, TReturn>(TResource resource, Func<TResource, Task<TReturn>> body)
            where TResource : IAsyncDisposable {
            Exception exception = null;
            TReturn result = default(TReturn);
            try {
                result = await body(resource).AnyContext();
            } catch (Exception ex) {
                exception = ex;
            }

            await resource.DisposeAsync().AnyContext();
            if (exception != null) {
                var info = ExceptionDispatchInfo.Capture(exception);
                info.Throw();
            }

            return result;
        }

        public static Task Using<TResource>(TResource resource, Func<Task> body) where TResource : IAsyncDisposable {
            return Using(resource, r => body());
        }

        public static Task Using<TResource>(TResource resource, Action body) where TResource : IAsyncDisposable {
            return Using(resource, r => {
                body();
                return Task.CompletedTask;
            });
        }

        public static Task Using<TResource>(TResource resource, Func<TResource, Task> body) where TResource : IAsyncDisposable {
            return Using(resource, async r => {
                await body(resource).AnyContext();
                return Task.CompletedTask;
            });
        }

        public static Task<TReturn> Using<TResource, TReturn>(TResource resource, Func<Task<TReturn>> body) where TResource : IAsyncDisposable {
            return Using(resource, r => body());
        }
    }
}
