using System;
using System.Collections.Generic;
using Foundatio.Repositories.Models;
using Foundatio.Repositories.Queries;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public class Query : IIdentityQuery, ICachableQuery, IDateRangeQuery,
        IFieldConditionsQuery, IPagableQuery, ISearchQuery, IFacetQuery,
        ISelectedFieldsQuery, ISortableQuery, IParentQuery, IChildQuery {
        public Query() {
            Ids = new List<string>();
            OrganizationIds = new List<string>();
            DateRanges = new List<DateRange>();
            FieldConditions = new List<FieldCondition>();
            SelectedFields = new List<string>();
            SortBy = new List<FieldSort>();
            ContactIds = new List<string>();
            FacetFields = new List<FacetField>();
        }

        public List<string> Ids { get; }
        public string CacheKey { get; set; }
        public TimeSpan? ExpiresIn { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public List<DateRange> DateRanges { get; }
        public List<FieldCondition> FieldConditions { get; }
        public int? Limit { get; set; }
        public int? Page { get; set; }
        public bool UseSnapshotPaging { get; set; }
        public string SystemFilter { get; set; }
        public string Filter { get; set; }
        public string SearchQuery { get; set; }
        public SearchOperator DefaultSearchQueryOperator { get; set; }
        public List<string> SelectedFields { get; }
        public List<FieldSort> SortBy { get; }
        public bool SortByScore { get; set; }
        public List<FacetField> FacetFields { get; }
        public ITypeQuery ParentQuery { get; set; }
        public ITypeQuery ChildQuery { get; set; }
    }
}
