using System.Threading.Tasks;

namespace Foundatio.Migrations {
    public interface IMigration {
        int Version { get; }
        Task RunAsync();
    }
}
