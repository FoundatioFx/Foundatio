using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundatio.Repositories.Models {
    public class SortingOptions {
        public static readonly SortingOptions Empty = new SortingOptions();

        public SortingOptions() {
            Fields = new List<FieldSort>();
        }

        public List<FieldSort> Fields { get; }

        public static SortingOptions Parse(string sort) {
            if (String.IsNullOrEmpty(sort))
                return Empty;

            var sortingOptions = new SortingOptions();
            var fields = sort.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var field in fields) {
                string name = field;
                var order = SortOrder.Ascending;
                if (name.StartsWith("-")) {
                    name = name.Substring(1);
                    order = SortOrder.Descending;
                }
                sortingOptions.Fields.Add(new FieldSort { Field = name, Order = order });
            }

            return sortingOptions;
        }

        static public implicit operator SortingOptions(string value) {
            return Parse(value);
        }
    }

    public class FieldSort {
        public string Field { get; set; }
        public SortOrder? Order { get; set; }
    }

    public enum SortOrder {
        Ascending,
        Descending,
    }

    public static class SortingOptionsExtensions {
        public static SortingOptions WithField(this SortingOptions options, string field, SortOrder sort = SortOrder.Ascending) {
            var fieldSort = options.Fields.FirstOrDefault(f => f.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (fieldSort == null) {
                fieldSort = new FieldSort { Field = field, Order = sort };
                options.Fields.Add(fieldSort);
            }

            fieldSort.Order = sort;

            return options;
        }
    }
}
