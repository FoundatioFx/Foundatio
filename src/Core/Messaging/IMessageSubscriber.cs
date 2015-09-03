using System;

namespace Foundatio.Messaging {
    public interface IMessageSubscriber {
        void Subscribe<T>(Action<T> handler) where T : class;
    }
}
