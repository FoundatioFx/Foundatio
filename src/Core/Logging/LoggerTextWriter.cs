using System;
using System.IO;
using System.Text;

namespace Foundatio.Logging {
    public class LoggerTextWriter : TextWriter {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) {
            throw new NotSupportedException();
        }

        public override void WriteLine(string value) {
            Logger.Info().Message(value).Write();
        }

        public override void WriteLine() {
        }
    }
}
