using System.Configuration;

namespace Foundatio.Tests.Utility {
    public static class ConnectionStrings {
        public static string Get(string name) {
            var connectionString = ConfigurationManager.ConnectionStrings[name];
            return connectionString != null ? connectionString.ConnectionString : null;
        }
    }
}
