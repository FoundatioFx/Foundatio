using System;
using System.Collections.Generic;
using Nest;

namespace Foundatio.Elasticsearch.Configuration {
    public interface IElasticsearchIndex {
        int Version { get; }
        string AliasName { get; }
        string VersionedName { get; }
        IDictionary<Type, IndexType> GetIndexTypes();
        CreateIndexDescriptor CreateIndex(CreateIndexDescriptor idx);
    }

    public interface ITemplatedElasticsearchIndex : IElasticsearchIndex {
        PutTemplateDescriptor CreateTemplate(PutTemplateDescriptor template);
    }

    public class IndexType {
        public string Name { get; set; }
        public string ParentPath { get; set; }
    }
}
