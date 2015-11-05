using System;

namespace Foundatio.Repositories.Queries {
    public interface IQueryBuilder<T> where T : class, new() {
        T Build(bool supportSoftDeletes = false);
    }
}
