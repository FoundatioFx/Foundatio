using System.Threading.Tasks;

namespace Foundatio.Migrations {
    public abstract class MigrationBase : IMigration {
        public abstract int Version { get; }
        public abstract Task RunAsync();

        protected int CalculateProgress(long total, long completed, int startProgress = 0, int endProgress = 100) {
            return startProgress + (int)((100 * (double)completed / total) * (((double)endProgress - startProgress) / 100));
        }
    }
}
