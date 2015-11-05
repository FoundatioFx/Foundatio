using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public abstract class QueryBuilderBase : IQueryBuilder<QueryContainer>,
        IElasticFilterQuery, IElasticIndicesQuery, IDateRangeQuery, 
        IFieldConditionsQuery, ISearchQuery, ISoftDeletesQuery {
        public QueryBuilderBase() {
            DateRanges = new List<DateRange>();
            FieldConditions = new List<FieldCondition>();
            Indices = new List<String>();
        }

        public List<DateRange> DateRanges { get; }
        public List<FieldCondition> FieldConditions { get; }
        public bool IncludeSoftDeletes { get; set; }
        public List<string> Indices { get; set; }
        public FilterContainer ElasticFilter { get; set; }
        public string SystemFilter { get; set; }
        public string Filter { get; set; }
        public string SearchQuery { get; set; }
        public SearchOperator DefaultSearchQueryOperator { get; set; }

        public virtual QueryContainer Build(bool supportSoftDeletes = false) {
            QueryContainer container = new MatchAllQuery();
            container &= new FilteredQuery { Filter = ApplyFilter(null, supportSoftDeletes) };

            if (!String.IsNullOrEmpty(SearchQuery))
                container &= new QueryStringQuery { Query = SearchQuery, DefaultOperator = DefaultSearchQueryOperator == SearchOperator.Or ? Operator.Or : Operator.And, AnalyzeWildcard = true };

            return container;
        }

        protected virtual FilterContainer ApplyFilter(FilterContainer container, bool supportSoftDeletes) {
            if (container == null)
                container = new MatchAllFilter();

            if (supportSoftDeletes)
                container &= new TermFilter { Field = "deleted", Value = IncludeSoftDeletes };

            if (DateRanges.Count > 0) {
                foreach (var dateRange in DateRanges.Where(dr => dr.UseDateRange))
                    container &= new RangeFilter { Field = dateRange.Field, GreaterThanOrEqualTo = dateRange.GetStartDate().ToString("o"), LowerThanOrEqualTo = dateRange.GetEndDate().ToString("O") };
            }

            if (!String.IsNullOrEmpty(SystemFilter))
                container &= new QueryFilter { Query = QueryContainer.From(new QueryStringQuery { Query = SystemFilter, DefaultOperator = Operator.And }) };

            if (!String.IsNullOrEmpty(Filter))
                container &= new QueryFilter { Query = QueryContainer.From(new QueryStringQuery { Query = Filter, DefaultOperator = Operator.And }) };

            if (ElasticFilter != null)
                container &= ElasticFilter;

            if (FieldConditions.Count > 0) {
                foreach (var fieldValue in FieldConditions) {
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
    }
}