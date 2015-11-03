using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Repositories.Models {
    public class FindResults<T> where T : class, new() {
        public FindResults(ICollection<T> documents = null, long total = 0, ICollection<FacetResult> facetResults = null, string scrollId = null, Func<FindResults<T>, Task<FindResults<T>>> getNextPage = null) {
            Documents = documents ?? new List<T>();
            Facets = facetResults ?? new List<FacetResult>();
            ScrollId = scrollId;
            GetNextPageFunc = getNextPage;
            Total = total;
        }

        public ICollection<T> Documents { get; set; }
        public long Total { get; set; }
        public ICollection<FacetResult> Facets { get; set; }

        public string ScrollId { get; set; }
        public int Page { get; set; } = 1;
        public bool HasMore { get; set; }
        internal Func<FindResults<T>, Task<FindResults<T>>> GetNextPageFunc { get; set; }
        public async Task<bool> NextPageAsync() {
            Documents.Clear();

            if (GetNextPageFunc == null) {
                Page = -1;
                return false;
            }

            var results = await GetNextPageFunc(this).AnyContext();
            Documents.AddRange(results.Documents);

            return Documents.Count > 0;
        }
    }
}
