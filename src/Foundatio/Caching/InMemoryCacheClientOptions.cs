namespace Foundatio.Caching {
    public class InMemoryCacheClientOptions : SharedOptions {
        /// <summary>
        /// The maximum number of items to store in the cache
        /// </summary>
        public int? MaxItems { get; set; } = 1000;
        
        /// <summary>
        /// Whether or not values should be cloned during get and set to make sure that any cache entry changes are isolated
        /// </summary>
        public bool CloneValues { get; set; } = false;
    }

    public class InMemoryCacheClientOptionsBuilder : SharedOptionsBuilder<InMemoryCacheClientOptions, InMemoryCacheClientOptionsBuilder> {
        public InMemoryCacheClientOptionsBuilder MaxItems(int? maxItems) {
            Target.MaxItems = maxItems;
            return this;
        }

        public InMemoryCacheClientOptionsBuilder CloneValues(bool cloneValues) {
            Target.CloneValues = cloneValues;
            return this;
        }

    }
}