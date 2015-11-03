using System;
using System.Linq;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public static class QueryBuilder {
        public static QueryContainer GetElasticsearchQuery(this object query, bool supportSoftDeletes = false) {
            QueryContainer container = new MatchAllQuery();
            container &= new FilteredQuery { Filter = ApplyFilter(query, null, supportSoftDeletes) };

            var searchQuery = query as ISearchQuery;
            if (!String.IsNullOrEmpty(searchQuery?.SearchQuery))
                container &= new QueryStringQuery { Query = searchQuery.SearchQuery, DefaultOperator = searchQuery.DefaultSearchQueryOperator == SearchOperator.Or ? Operator.Or : Operator.And, AnalyzeWildcard = true };

            return container;
        }

        private static FilterContainer ApplyFilter(object query, FilterContainer container, bool supportSoftDeletes = false) {
            if (container == null)
                container = new MatchAllFilter();

            var pq = query as IParentQuery;
            if (pq?.ParentQuery != null)
                container &= new HasParentFilter { Query = pq.ParentQuery.GetElasticsearchQuery(supportSoftDeletes), Type = pq.ParentQuery.Type };

            var cq = query as IChildQuery;
            if (cq?.ChildQuery != null)
                container &= new HasChildFilter { Query = cq.ChildQuery.GetElasticsearchQuery(supportSoftDeletes), Type = cq.ChildQuery.Type };

            var identityQuery = query as IIdentityQuery;
            if (identityQuery != null && identityQuery.Ids.Count > 0)
                container &= new IdsFilter { Values = identityQuery.Ids };

            if (supportSoftDeletes) {
                var softDeletesQuery = query as ISoftDeletesQuery;
                bool includeDeleted = softDeletesQuery?.IncludeSoftDeletes ?? false;
                container &= new TermFilter { Field = "deleted", Value = includeDeleted };
            }

            var dateRangeQuery = query as IDateRangeQuery;
            if (dateRangeQuery?.DateRanges.Count > 0) {
                foreach (var dateRange in dateRangeQuery.DateRanges.Where(dr => dr.UseDateRange))
                    container &= new RangeFilter { Field = dateRange.Field, GreaterThanOrEqualTo = dateRange.GetStartDate().ToString("o"), LowerThanOrEqualTo = dateRange.GetEndDate().ToString("O") };
            }

            var searchQuery = query as ISearchQuery;
            if (searchQuery != null) {
                if (!String.IsNullOrEmpty(searchQuery.SystemFilter))
                    container &= new QueryFilter { Query = QueryContainer.From(new QueryStringQuery { Query = searchQuery.SystemFilter, DefaultOperator = Operator.And }) };

                if (!String.IsNullOrEmpty(searchQuery.Filter))
                    container &= new QueryFilter { Query = QueryContainer.From(new QueryStringQuery { Query = searchQuery.Filter, DefaultOperator = Operator.And }) };
            }

            var elasticQuery = query as IElasticFilterQuery;
            if (elasticQuery?.ElasticFilter != null)
                container &= elasticQuery.ElasticFilter;

            var fieldValuesQuery = query as IFieldConditionsQuery;
            if (fieldValuesQuery?.FieldConditions.Count > 0) {
                foreach (var fieldValue in fieldValuesQuery.FieldConditions) {
                    switch (fieldValue.Operator) {
                        case ComparisonOperator.Equals:
                            container &= new TermFilter { Field = fieldValue.Field, Value = fieldValue.Value };
                            break;
                        case ComparisonOperator.NotEquals:
                            container &= new NotFilter { Filter = FilterContainer.From(new TermFilter { Field = fieldValue.Field, Value = fieldValue.Value }) };
                            break;
                        case ComparisonOperator.IsEmpty:
                            container &= new MissingFilter { Field = fieldValue.Field };
                            break;
                        case ComparisonOperator.HasValue:
                            container &= new ExistsFilter { Field = fieldValue.Field };
                            break;
                    }
                }
            }

            return container;
        }

        public static AggregationDescriptor<T> GetAggregationDescriptor<T>(this object query) where T : class {
            var facetQuery = query as IFacetQuery;
            if (facetQuery == null || facetQuery.FacetFields.Count == 0)
                return null;

            var descriptor = new AggregationDescriptor<T>();
            foreach (var t in facetQuery.FacetFields)
                descriptor = descriptor.Terms(t.Field, s => s.Field(t.Field).Size(t.Size ?? 100));

            return descriptor;
        }
    }
}
