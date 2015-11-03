using Foundatio.Migrations;
using Foundatio.Repositories;

namespace Foundatio.Elasticsearch.Repositories {
    public class MigrationsRepository : Repository<MigrationResult>, IMigrationRepository {
        public MigrationsRepository(RepositoryContext<MigrationResult> context) : base(context) {
        }

        protected override string GetTypeName() {
            return "migrations";
        }
    }
}
