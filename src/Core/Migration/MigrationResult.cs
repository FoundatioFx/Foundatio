using System;
using Foundatio.Repositories.Models;

namespace Foundatio.Migrations {
    public class MigrationResult : IIdentity {
        public string Id { get { return "migration-" + Version; } set {} }
        public int Version { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime CompletedUtc { get; set; }
    }
}
