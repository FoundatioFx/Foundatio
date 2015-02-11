using System;
using System.Threading.Tasks;

namespace Foundatio.Messaging {
    public interface IMessageSubscriber {
        void Subscribe<T>(Action<T> handler) where T : class;
    }

    public interface IMessageSubscriber2 {
        void Subscribe<T>(Func<T, Task> handler) where T : class;
    }
}
