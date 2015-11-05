using System;
using Foundatio.Elasticsearch.Repositories.Queries;
using Foundatio.Migrations;

namespace Foundatio.Elasticsearch.Repositories {
    public class MigrationsRepository : Repository<MigrationResult, Query>, IMigrationRepository {
        public MigrationsRepository(RepositoryContext<MigrationResult> context) : base(context) {}

        protected override string GetTypeName() {
            return "migrations";
        }
    }
}
