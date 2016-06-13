using System;
using Foundatio.Extensions;
using Xunit;

namespace Foundatio.Tests.Extensions {
    public class CopyTests {
        [Fact]
        public void CopyTest1() {
            var contact = new Root();

            // only happens if the values are the same
            contact.Value1 = new MyValueType { Value = 12 };
            contact.Value2 = new MyValueType { Value = 12 };

            var copy = contact.Copy();

            Assert.Equal(contact.Value1, copy.Value1);
            Assert.Equal(contact.Value2, copy.Value2);
        }
    }

    public class Root {
        public object Value1 { get; set; }
        public object Value2 { get; set; }
    }

    public struct MyValueType {
        public long Value { get; set; }

        public override bool Equals(object obj) {
            return Value == (obj as MyValueType?)?.Value;
        }

        public override int GetHashCode() {
            return Value.GetHashCode();
        }

        public override string ToString() {
            return Value.ToString();
        }
    }
}
