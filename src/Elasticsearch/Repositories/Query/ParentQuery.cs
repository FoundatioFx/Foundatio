using System.Collections.Generic;
using Foundatio.Repositories.Queries;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public class ParentQuery : IIdentityQuery, IDateRangeQuery, IFieldConditionsQuery, ISearchQuery, ITypeQuery {
        public ParentQuery() {
            DateRanges = new List<DateRange>();
            FieldConditions = new List<FieldCondition>();
            Ids = new List<string>();
        }

        public List<string> Ids { get; }
        public string Type { get; set; }
        public List<DateRange> DateRanges { get; }
        public List<FieldCondition> FieldConditions { get; }
        public string SystemFilter { get; set; }
        public string Filter { get; set; }
        public string SearchQuery { get; set; }
        public SearchOperator DefaultSearchQueryOperator { get; set; }
    }
}
