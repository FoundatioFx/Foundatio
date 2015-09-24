using System;

namespace Foundatio.Logging {
    /// <summary>
    /// An <see langword="interface"/> defining a log writer.
    /// </summary>
    public interface ILogWriter {
        /// <summary>
        /// Writes the specified <see cref="LogData"/> to the underlying logger.
        /// </summary>
        /// <param name="logData">The log data.</param>
        void WriteLog(LogData logData);
    }
}