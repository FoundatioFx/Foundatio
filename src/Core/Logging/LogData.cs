using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Foundatio.Logging {
    /// <summary>
    /// A class holding log data before being written.
    /// </summary>
    public sealed class LogData {
        private IDictionary<string, object> _properties;

        /// <summary>
        /// Gets or sets the trace level.
        /// </summary>
        /// <value>
        /// The trace level.
        /// </value>
        public LogLevel LogLevel { get; set; }

        /// <summary>
        /// Gets or sets the event id.
        /// </summary>
        /// <value>
        /// The event id.
        /// </value>
        public EventId EventId { get; set; }

        /// <summary>
        /// Gets or sets the message.
        /// </summary>
        /// <value>
        /// The message.
        /// </value>
        public string Message { get; set; }

        /// <summary>
        /// Gets or sets the message parameters. Used with String.Format.
        /// </summary>
        /// <value>
        /// The parameters.
        /// </value>
        public object[] Parameters { get; set; }
        
        /// <summary>
        /// Gets or sets the format provider.
        /// </summary>
        /// <value>
        /// The format provider.
        /// </value>
        public IFormatProvider FormatProvider { get; set; }

        /// <summary>
        /// Gets or sets the exception.
        /// </summary>
        /// <value>
        /// The exception.
        /// </value>
        public Exception Exception { get; set; }

        /// <summary>
        /// Gets or sets the name of the member.
        /// </summary>
        /// <value>
        /// The name of the member.
        /// </value>
        public string MemberName { get; set; }

        /// <summary>
        /// Gets or sets the file path.
        /// </summary>
        /// <value>
        /// The file path.
        /// </value>
        public string FilePath { get; set; }

        /// <summary>
        /// Gets or sets the line number.
        /// </summary>
        /// <value>
        /// The line number.
        /// </value>
        public int LineNumber { get; set; }

        /// <summary>
        /// Gets or sets the log properties.
        /// </summary>
        /// <value>
        /// The log properties.
        /// </value>
        public IDictionary<string, object> Properties {
            get { return _properties ?? (_properties = new Dictionary<String, Object>()); }
            set { _properties = value; }
        }

        /// <summary>
        /// Returns a <see cref="System.String" /> that represents this instance.
        /// </summary>
        /// <returns>
        /// A <see cref="System.String" /> that represents this instance.
        /// </returns>
        public override string ToString() {
            return ToString(true, true);
        }

        public string ToString(bool includeFileInfo, bool includeException) {
            var message = new StringBuilder();
            message.Append("[").Append(DateTime.UtcNow.ToString("HH:mm:ss.fff")).Append(" ").Append(LogLevel.ToString()[0]).Append(" ").Append(Logger).Append("] ");

            if (includeFileInfo && !String.IsNullOrEmpty(FilePath) && !String.IsNullOrEmpty(MemberName)) {
                message.Append("[").Append(Path.GetFileName(FilePath)).Append(" ").Append(MemberName).Append("()").Append(" Ln: ").Append(LineNumber).Append("] ");
            }

            if (Parameters != null && Parameters.Length > 0)
                message.AppendFormat(FormatProvider, Message, Parameters);
            else
                message.Append(Message);

            if (includeException && Exception != null)
                message.Append(" ").Append(Exception);

            return message.ToString();
        }
    }
}