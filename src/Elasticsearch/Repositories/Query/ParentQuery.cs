using System.Collections.Generic;

namespace Foundatio.Elasticsearch.Repositories {
    public class ParentQuery : IIdentityQuery, IOrganizationIdQuery, IDateRangeQuery,
        IFieldConditionsQuery, ISearchQuery, IContactIdQuery, ITypeQuery {
        public ParentQuery() {
            OrganizationIds = new List<string>();
            DateRanges = new List<DateRange>();
            FieldConditions = new List<FieldCondition>();
            ContactIds = new List<string>();
            Ids = new List<string>();
        }

        public List<string> OrganizationIds { get; }
        public List<DateRange> DateRanges { get; }
        public List<FieldCondition> FieldConditions { get; }
        public string SystemFilter { get; set; }
        public string Filter { get; set; }
        public string SearchQuery { get; set; }
        public SearchOperator DefaultSearchQueryOperator { get; set; }
        public List<string> ContactIds { get; }
        public List<string> Ids { get; }
        public string Type { get; set; }
    }
}
