using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundatio.Repositories.Queries {
    public interface IIdentityQuery {
        List<string> Ids { get; }
    }

    public static class IdentityQueryExtensions {
        public static T WithId<T>(this T query, string id) where T : IIdentityQuery {
            query.Ids.Add(id);
            return query;
        }

        public static T WithIds<T>(this T query, params string[] ids) where T : IIdentityQuery {
            query.Ids.AddRange(ids.Distinct());
            return query;
        }

        public static T WithIds<T>(this T query, IEnumerable<string> ids) where T : IIdentityQuery {
            query.Ids.AddRange(ids.Distinct());
            return query;
        }
    }
}
