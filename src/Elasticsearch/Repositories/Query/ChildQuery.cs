using System.Collections.Generic;
using Nest;

namespace Foundatio.Elasticsearch.Repositories {
    public class ChildQuery : IDateRangeQuery,
        IFieldConditionsQuery, ISearchQuery, ITypeQuery {
        public ChildQuery() {
            DateRanges = new List<DateRange>();
            FieldConditions = new List<FieldCondition>();
        }

        public List<DateRange> DateRanges { get; }
        public List<FieldCondition> FieldConditions { get; }
        public string SystemFilter { get; set; }
        public string Filter { get; set; }
        public string SearchQuery { get; set; }
        public SearchOperator DefaultSearchQueryOperator { get; set; }
        public string Type { get; set; }
    }
}
