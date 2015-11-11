using System;
using System.Collections.Generic;
using Foundatio.Elasticsearch.Tests.Repositories.Models;
using Foundatio.Elasticsearch.Configuration;
using Nest;

namespace Foundatio.Elasticsearch.Tests.Repositories.Configuration {
    public class EmployeeIndex : IElasticIndex {
        public int Version => 1;
        public static string Alias => "employees";
        public string AliasName => Alias;
        public string VersionedName => String.Concat(AliasName, "-v", Version);

        public IDictionary<Type, IndexType> GetIndexTypes() {
            return new Dictionary<Type, IndexType> {
                { typeof(Employee), new IndexType { Name = "employee" } }
            };
        }

        public CreateIndexDescriptor CreateIndex(CreateIndexDescriptor idx) {
            return idx.AddMapping<Employee>(GetEmployeeMap);
        }

        private PutMappingDescriptor<Employee> GetEmployeeMap(PutMappingDescriptor<Employee> map) {
            return map
                .Index(VersionedName)
                .Dynamic()
                .TimestampField(ts => ts.Enabled().Path(u => u.UpdatedUtc).IgnoreMissing(false))
                .Properties(p => p
                    .String(f => f.Name(e => e.Id).IndexName(Fields.Employee.Id).Index(FieldIndexOption.NotAnalyzed))
                    .String(f => f.Name(e => e.CompanyId).IndexName(Fields.Employee.CompanyId).Index(FieldIndexOption.NotAnalyzed))
                    .String(f => f.Name(e => e.CompanyName).IndexName(Fields.Employee.CompanyName).Index(FieldIndexOption.NotAnalyzed))
                    .String(f => f.Name(e => e.Name).IndexName(Fields.Employee.Name).Index(FieldIndexOption.NotAnalyzed))
                    .Number(f => f.Name(e => e.Age).IndexName(Fields.Employee.Age))
                    .Date(f => f.Name(e => e.CreatedUtc).IndexName(Fields.Employee.CreatedUtc))
                    .Date(f => f.Name(e => e.UpdatedUtc).IndexName(Fields.Employee.UpdatedUtc))
                );
        }
        
        public class Fields {
            public class Employee {
                public const string Id = "id";
                public const string CompanyId = "company";
                public const string CompanyName = "company_name";
                public const string Name = "name";
                public const string Age = "age";
                public const string CreatedUtc = "created";
                public const string UpdatedUtc = "updated";
            }
        }
    }
}