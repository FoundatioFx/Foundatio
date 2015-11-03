using System;
using System.Collections.Generic;

namespace Foundatio.Elasticsearch.Jobs {
    public class ReindexWorkItem {
        public ReindexWorkItem() {
            ParentMaps = new List<ParentMap>();
        }

        public string OldIndex { get; set; }
        public string NewIndex { get; set; }
        public string Alias { get; set; }
        public bool DeleteOld { get; set; }
        public string TimestampField { get; set; }
        public DateTime? StartUtc { get; set; }
        public List<ParentMap> ParentMaps { get; set; }
    }

    public class ParentMap {
        public string Type { get; set; }
        public string ParentPath { get; set; }
    }
}