using System;
using System.Collections.Generic;
using Foundatio.Repositories.Models;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public class Query : QueryBuilderBase, IIdentityQuery, ICachableQuery,
        IPagableQuery, IFacetQuery, ISelectedFieldsQuery, ISortableQuery, 
        IParentQuery, IChildQuery {
        public Query() {
            Ids = new List<string>();
            SelectedFields = new List<string>();
            SortBy = new List<FieldSort>();
            FacetFields = new List<FacetField>();
        }

        public List<string> Ids { get; }
        public string CacheKey { get; set; }
        public TimeSpan? ExpiresIn { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public int? Limit { get; set; }
        public int? Page { get; set; }
        public bool UseSnapshotPaging { get; set; }
        public List<string> SelectedFields { get; }
        public List<FieldSort> SortBy { get; }
        public bool SortByScore { get; set; }
        public List<FacetField> FacetFields { get; }
        public ITypeQuery ParentQuery { get; set; }
        public ITypeQuery ChildQuery { get; set; }

        protected override FilterContainer ApplyFilter(FilterContainer container, bool supportSoftDeletes) {
            if (container == null)
                container = new MatchAllFilter();

            var parentQuery = ParentQuery as QueryBuilderBase;
            if (parentQuery != null)
                container &= new HasParentFilter { Query = parentQuery.Build(supportSoftDeletes), Type = ParentQuery.Type };

            var childQuery = ChildQuery as QueryBuilderBase;
            if (childQuery != null)
                container &= new HasChildFilter { Query = childQuery.Build(supportSoftDeletes), Type = ChildQuery.Type };

            if (Ids.Count > 0)
                container &= new IdsFilter { Values = Ids };
            
            return base.ApplyFilter(container, supportSoftDeletes);
        }
    }
}
