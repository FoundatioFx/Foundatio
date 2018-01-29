using System;
using Foundatio.Utility;

namespace Foundatio.Caching {
    public class InMemoryCacheClientOptions : SharedOptions {}

    public class InMemoryCacheClientOptionsBuilder : OptionsBuilder<InMemoryCacheClientOptions>, ISharedOptionsBuilder {}
}