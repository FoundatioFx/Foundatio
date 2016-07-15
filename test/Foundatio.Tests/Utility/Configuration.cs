using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace Foundatio.Tests.Utility {
    public static class Configuration {
        private static readonly IConfiguration _configuration;
        static Configuration() {
            _configuration = new ConfigurationBuilder()
                .SetBasePath(GetBasePath())
                .AddJsonFile("appsettings.json", true, true)
                .AddEnvironmentVariables()
                .Build();
        }

        public static IConfigurationSection GetSection(string name) {
            return _configuration.GetSection(name);
        }

        public static string GetConnectionString(string name) {
            return _configuration.GetConnectionString(name);
        }

        private static string GetBasePath() {
            string basePath = Path.GetFullPath("..\\");
            for (int i = 0; i < 5; i++) {
                if (File.Exists(Path.Combine(basePath, "appsettings.json")))
                    return basePath;

                basePath += "..\\";
            }

            return Path.GetFullPath(".");
        }
    }
}
