using System;
using Foundatio.Migrations;

namespace Foundatio.Elasticsearch.Repositories {
    public class MigrationsRepository : ElasticRepositoryBase<MigrationResult>, IMigrationRepository {
        public MigrationsRepository(ElasticRepositoryContext<MigrationResult> context) : base(context) {}

        protected override string GetTypeName() {
            return "migrations";
        }
    }
}
