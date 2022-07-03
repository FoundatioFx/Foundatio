using Foundatio.Tests.Messaging;
using Foundatio.Utility;

namespace Foundatio.Tests.Messaging {
    public class SimpleMessageA : ISimpleMessage {
        public SimpleMessageA() {
            Items = new DataDictionary();
        }
        public string Data { get; set; }
        public int Count { get; set; }
            
        public DataDictionary Items { get; set; }
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

namespace Foundatio.Tests.MessagingAlt {
    public class SimpleMessageA : ISimpleMessage {
        public SimpleMessageA() {
            Items = new DataDictionary();
        }
        public string Data { get; set; }
        public int Count { get; set; }
            
        public IDataDictionary Items { get; set; }
    }
}