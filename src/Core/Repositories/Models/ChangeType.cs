using System;

namespace Foundatio.Repositories.Models.Messaging {
    public enum ChangeType : byte {
        Added = 0,
        Saved = 1,
        Removed = 2
    }
}
