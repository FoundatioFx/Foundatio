using System;
using System.Collections.Generic;
using Foundatio.Elasticsearch.Tests.Repositories.Models;
using Foundatio.Elasticsearch.Configuration;
using Nest;

namespace Foundatio.Elasticsearch.Tests.Repositories.Configuration {
    public class EmployeeWithDateIndex : ITemplatedElasticIndex {
        public int Version => 1;
        public static string Alias => "employees_with_date";
        public string AliasName => Alias;
        public string VersionedName => String.Concat(AliasName, "-v", Version);

        public IDictionary<Type, IndexType> GetIndexTypes() {
            return new Dictionary<Type, IndexType> {
                { typeof(EmployeeWithDate), new IndexType { Name = "employees_with_date" } }
            };
        }

        public CreateIndexDescriptor CreateIndex(CreateIndexDescriptor idx) {
            throw new NotImplementedException();
        }

        public PutTemplateDescriptor CreateTemplate(PutTemplateDescriptor template) {
            return template
                .Template(VersionedName + "-*")
                .AddMapping<EmployeeWithDate>(map => map
                    .Dynamic()
                    .TimestampField(ts => ts.Enabled().Path(u => u.UpdatedUtc).IgnoreMissing(false))
                    .Properties(p => p
                        .String(f => f.Name(e => e.Id).IndexName(Fields.EmployeeWithDate.Id).Index(FieldIndexOption.NotAnalyzed))
                        .String(f => f.Name(e => e.CompanyId).IndexName(Fields.EmployeeWithDate.CompanyId).Index(FieldIndexOption.NotAnalyzed))
                        .String(f => f.Name(e => e.CompanyName).IndexName(Fields.EmployeeWithDate.CompanyName).Index(FieldIndexOption.NotAnalyzed))
                        .String(f => f.Name(e => e.Name).IndexName(Fields.EmployeeWithDate.Name).Index(FieldIndexOption.NotAnalyzed))
                        .Number(f => f.Name(e => e.Age).IndexName(Fields.EmployeeWithDate.Age))
                        .Date(f => f.Name(e => e.StartDate).IndexName(Fields.EmployeeWithDate.StartDate))
                        .Date(f => f.Name(e => e.CreatedUtc).IndexName(Fields.EmployeeWithDate.CreatedUtc))
                        .Date(f => f.Name(e => e.UpdatedUtc).IndexName(Fields.EmployeeWithDate.UpdatedUtc))
                    ));
        }
        
        public class Fields {
            public class EmployeeWithDate {
                public const string Id = "id";
                public const string CompanyId = "company";
                public const string CompanyName = "company_name";
                public const string Name = "name";
                public const string Age = "age";
                public const string StartDate = "start";
                public const string CreatedUtc = "created";
                public const string UpdatedUtc = "updated";
            }
        }
    }
}