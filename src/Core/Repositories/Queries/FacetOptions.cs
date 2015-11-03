using System;
using System.Collections.Generic;

namespace Foundatio.Elasticsearch.Repositories {
    public class FacetOptions {
        public static FacetOptions Empty = new FacetOptions();

        public FacetOptions() {
            Fields = new List<FacetField>();
        }

        public List<FacetField> Fields { get; }

        public static FacetOptions Parse(string facets) {
            if (String.IsNullOrEmpty(facets))
                return FacetOptions.Empty;

            var facetOptions = new FacetOptions();
            var fields = facets.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var field in fields) {
                string name = field;
                int size = 25;
                var parts = field.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) {
                    name = parts[0];
                    int partSize;
                    if (Int32.TryParse(parts[1], out partSize))
                        size = partSize;
                }

                facetOptions.Fields.Add(new FacetField { Field = name, Size = size });
            }

            return facetOptions;
        }

        static public implicit operator FacetOptions(string value) {
            return Parse(value);
        }
    }

    public class FacetField {
        public string Field { get; set; }
        public int? Size { get; set; }
    }
}
