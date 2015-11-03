using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;

namespace Foundatio.Migrations {
    public class MigrationManager {
        private readonly IServiceProvider _container;
        private readonly IMigrationRepository _migrationRepository;

        public MigrationManager(IServiceProvider container, IMigrationRepository migrationRepository) {
            _container = container;
            _migrationRepository = migrationRepository;
        }

        public async Task RunAsync() {
            var migrations = await GetPendingMigrationsAsync().AnyContext();
            foreach (var m in migrations) {
                await MarkMigrationStartedAsync(m.Version).AnyContext();
                await m.RunAsync().AnyContext();
                await MarkMigrationCompleteAsync(m.Version).AnyContext();
            }
        }

        private async Task MarkMigrationStartedAsync(int version) {
            await _migrationRepository.AddAsync(new MigrationResult { Version = version, StartedUtc = DateTime.UtcNow }).AnyContext();
        }

        private async Task MarkMigrationCompleteAsync(int version) {
            var m = await _migrationRepository.GetByIdAsync("migration-" + version).AnyContext();
            m.CompletedUtc = DateTime.UtcNow;
            await _migrationRepository.SaveAsync(m).AnyContext();
        }

        private ICollection<IMigration> GetAllMigrations() {
            var migrationTypes = TypeHelper.GetDerivedTypes<IMigration>(new[] { typeof(IMigration).Assembly });
            return migrationTypes
                .Select(migrationType => (IMigration)_container.GetService(migrationType))
                .OrderBy(m => m.Version)
                .ToList();
        }

        private async Task<ICollection<IMigration>> GetPendingMigrationsAsync() {
            var allMigrations = GetAllMigrations();
            var completedMigrations = await _migrationRepository.GetAllAsync(paging: 1000).AnyContext();
            var currentVersion = completedMigrations.Documents.Count > 0 ? completedMigrations.Documents.Max(m => m.Version) : 0;
            return allMigrations.Where(m => m.Version > currentVersion).ToList();
        }
    }
}
