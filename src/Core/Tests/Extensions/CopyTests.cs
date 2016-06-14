using System;
using Foundatio.Extensions;
using Xunit;

namespace Foundatio.Tests.Extensions {
    public class CopyTests {
        [Fact]
        public void CanCopyIdenticalValueOnDifferentProperties() {
            // properties are the same type
            // values are structs
            // values are equal
            var child = new Child { Stuff = "test" };
            var root = new Root {
                Value1 = child,
                Value2 = child
            };

            var copy = root.Copy();

            Assert.Equal(root.Value1.ToString(), copy.Value1.ToString());
            Assert.Equal(root.Value2.ToString(), copy.Value2.ToString());
        }

        [Fact]
        public void CanHandleRecursion() {
            // only happens if the values are the same
            var root = new RecursiveRoot();
            root.Sub = new RecursiveSub { Root = root };

            var copy = root.Copy();
        }
    }

    public class RecursiveRoot {
        public RecursiveSub Sub { get; set; }
    }

    public class RecursiveSub {
        public RecursiveRoot Root { get; set; }
    }

    public class Root {
        public object Value1;
        public object Value2;
    }

    public struct Child {
        public string Stuff;

        public override string ToString() {
            return Stuff;
        }
    }
}
