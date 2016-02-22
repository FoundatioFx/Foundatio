using System;

namespace Foundatio.Tests.Messaging {
    public class SimpleMessageA : ISimpleMessage {
        public string Data { get; set; }
        public int Count { get; set; }
    }

    public class DerivedSimpleMessageA : SimpleMessageA {}

    public class SimpleMessageB : ISimpleMessage {
        public string Data { get; set; }
    }

    public class SimpleMessageC {
        public string Data { get; set; }
    }

    public interface ISimpleMessage {
        string Data { get; set; }
    }
}
