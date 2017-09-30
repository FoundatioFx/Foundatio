namespace Foundatio.Tests.Messaging {
    public class SimpleMessageA : ISimpleMessage {
        public string Data { get; set; }
        public int Count { get; set; }
    }

    public class DerivedSimpleMessageA : SimpleMessageA { }
    public class Derived2SimpleMessageA : SimpleMessageA { }
    public class Derived3SimpleMessageA : SimpleMessageA { }
    public class Derived4SimpleMessageA : SimpleMessageA { }
    public class Derived5SimpleMessageA : SimpleMessageA { }
    public class Derived6SimpleMessageA : SimpleMessageA { }
    public class Derived7SimpleMessageA : SimpleMessageA { }
    public class Derived8SimpleMessageA : SimpleMessageA { }
    public class Derived9SimpleMessageA : SimpleMessageA { }
    public class Derived10SimpleMessageA : SimpleMessageA { }
    public class NeverPublishedMessage { }

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
