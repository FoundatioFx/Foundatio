using System;
using System.Collections.Generic;
using Nest;

namespace Foundatio.Elasticsearch.Configuration {
    public interface IElasticIndex {
        int Version { get; }
        string AliasName { get; }
        string VersionedName { get; }
        IDictionary<Type, IndexType> GetIndexTypes();
        CreateIndexDescriptor CreateIndex(CreateIndexDescriptor idx);
    }

    public interface ITemplatedElasticIndex : IElasticIndex {
        PutTemplateDescriptor CreateTemplate(PutTemplateDescriptor template);
    }

    public class IndexType {
        public string Name { get; set; }
        public string ParentPath { get; set; }
    }
}
