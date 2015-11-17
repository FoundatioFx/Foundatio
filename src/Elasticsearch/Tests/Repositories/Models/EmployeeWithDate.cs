using System;
using Exceptionless;
using Exceptionless.DateTimeExtensions;
using Foundatio.Utility;

namespace Foundatio.Elasticsearch.Tests.Repositories.Models {
    public class EmployeeWithDate : Employee {
        public DateTimeOffset StartDate { get; set; }
    }
    
    public static class EmployeeWithDateGenerator {
        public static readonly string DefaultCompanyId = ObjectId.GenerateNewId().ToString();

        public static EmployeeWithDate Default => new EmployeeWithDate {
            Name = "Blake",
            Age = 29,
            CompanyName = "Exceptionless",
            CompanyId = DefaultCompanyId,
            StartDate = DateTimeOffset.Now
        };

        public static EmployeeWithDate Generate(string id = null, string name = null, int? age = null, string companyName = null, string companyId = null, DateTimeOffset? startDate = null, DateTime? createdUtc = null, DateTime? updatedUtc = null) {
            return new EmployeeWithDate {
                Id = id,
                Name = name ?? RandomData.GetAlphaString(),
                Age = age ?? RandomData.GetInt(18, 100),
                CompanyName = companyName ?? RandomData.GetAlphaString(),
                CompanyId = companyId ?? ObjectId.GenerateNewId().ToString(),
                StartDate = startDate ?? RandomData.GetDateTimeOffset(DateTimeOffset.Now.StartOfMonth(), DateTimeOffset.Now),
                CreatedUtc = createdUtc.GetValueOrDefault(),
                UpdatedUtc = updatedUtc.GetValueOrDefault()
            };
        }
    }
}