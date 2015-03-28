using System;
using System.IO;
using System.Text;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class TestOutputWriter : TextWriter {
        private readonly ITestOutputHelper _output;

        public TestOutputWriter(ITestOutputHelper output) {
            _output = output;
        }

        public override Encoding Encoding {
            get { return Encoding.UTF8; }
        }

        public override void WriteLine(string value) {
            _output.WriteLine(value);
        }

        public override void WriteLine() {
            _output.WriteLine(String.Empty);
        }
    }
}
