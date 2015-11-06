using System;
using System.Collections.Generic;
using Foundatio.Repositories;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface IFieldConditionsQuery {
        List<FieldCondition> FieldConditions { get; }
    }

    public class FieldConditionsQueryBuilder : QueryBuilderBase {
        public override void BuildFilter<T>(IReadOnlyRepository<T> repository, FilterContainer container, object query) {
            var fieldValuesQuery = query as IFieldConditionsQuery;
            if (fieldValuesQuery == null || fieldValuesQuery.FieldConditions.Count <= 0)
                return;

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
    }

    public class FieldCondition {
        public string Field { get; set; }
        public object Value { get; set; }
        public ComparisonOperator Operator { get; set; }
    }

    public enum ComparisonOperator {
        Equals,
        NotEquals,
        IsEmpty,
        HasValue
    }

    public static class FieldValueQueryExtensions {
        public static T WithFieldEquals<T>(this T query, string field, object value) where T : IFieldConditionsQuery {
            query.FieldConditions.Add(new FieldCondition { Field = field, Value = value, Operator = ComparisonOperator.Equals });
            return query;
        }

        public static T WithFieldNotEquals<T>(this T query, string field, object value) where T : IFieldConditionsQuery {
            query.FieldConditions.Add(new FieldCondition { Field = field, Value = value, Operator = ComparisonOperator.NotEquals });
            return query;
        }

        public static T WithEmptyField<T>(this T query, string field) where T : IFieldConditionsQuery {
            query.FieldConditions.Add(new FieldCondition { Field = field, Operator = ComparisonOperator.IsEmpty });
            return query;
        }

        public static T WithNonEmptyField<T>(this T query, string field) where T : IFieldConditionsQuery {
            query.FieldConditions.Add(new FieldCondition { Field = field, Operator = ComparisonOperator.HasValue });
            return query;
        }
    }
}
