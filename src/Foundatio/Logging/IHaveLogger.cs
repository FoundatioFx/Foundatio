using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Logging {
    public interface IHaveLogger {
        ILogger Logger { get; }
    }
}
