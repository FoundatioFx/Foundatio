using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Foundatio.Utility {
    public class TraceTextWriter : TextWriter {
        private readonly string _category = String.Empty;
        private Encoding _encoding;

        public TraceTextWriter() {}

        public TraceTextWriter(string category) {
            _category = category;
        }

        public override Encoding Encoding => _encoding ?? (_encoding = new UnicodeEncoding(false, false));

        public override void Write(char value) {
            if (String.IsNullOrEmpty(_category))
                Trace.Write(value);
            else
                Trace.Write(value, _category);
        }

        public override void Write(string value) {
            if (String.IsNullOrEmpty(_category))
                Trace.Write(value);
            else
                Trace.Write(value, _category);
        }
    }
}
