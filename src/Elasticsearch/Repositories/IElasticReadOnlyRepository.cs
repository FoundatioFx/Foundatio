using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Repositories;
using Foundatio.Repositories.Models;
using Foundatio.Repositories.Queries;

namespace Foundatio.Elasticsearch.Repositories {
    public interface IElasticReadOnlyRepository<T> : IReadOnlyRepository<T> where T : class, new() {
        Task<FindResults<T>> GetBySearchAsync(string systemFilter, string userFilter = null, string query = null, SortingOptions sorting = null, PagingOptions paging = null, FacetOptions facets = null);
        Task<ICollection<FacetResult>> GetFacetsAsync(string systemFilter, FacetOptions facets, string userFilter = null, string query = null);
    }
}
