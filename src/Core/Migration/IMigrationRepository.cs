using Foundatio.Repositories;
using GoodProspect.Domain.Migrations;

namespace Foundatio.Migrations {
    public interface IMigrationRepository : IRepository<MigrationResult> {
    }
}
