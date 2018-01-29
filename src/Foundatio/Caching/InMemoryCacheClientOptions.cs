using System;

namespace Foundatio.Caching {
    public class InMemoryCacheClientOptions : SharedCacheClientOptions {}

    public class InMemoryCacheClientOptionsBuilder : OptionsBuilder<InMemoryCacheClientOptions>, ISharedCacheClientOptionsBuilder {}
}