using System;
using System.Collections;
using System.Collections.Generic;

namespace Foundatio.Collections
{
    internal static class CollectionHelpers
    {
        public static IReadOnlyCollection<T> ReifyCollection<T>(IEnumerable<T> source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            var result = source as IReadOnlyCollection<T>;
            if (result != null)
                return result;
            var collection = source as ICollection<T>;
            if (collection != null)
                return new CollectionWrapper<T>(collection);
            var nongenericCollection = source as ICollection;
            if (nongenericCollection != null)
                return new NongenericCollectionWrapper<T>(nongenericCollection);

            return new List<T>(source);
        }

        private sealed class NongenericCollectionWrapper<T> : IReadOnlyCollection<T>
        {
            private readonly ICollection _collection;

            public NongenericCollectionWrapper(ICollection collection)
            {
                if (collection == null)
                    throw new ArgumentNullException(nameof(collection));
                _collection = collection;
            }

            public int Count
            {
                get
                {
                    return _collection.Count;
                }
            }

            public IEnumerator<T> GetEnumerator()
            {
                foreach (T item in _collection)
                    yield return item;
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return _collection.GetEnumerator();
            }
        }

        private sealed class CollectionWrapper<T> : IReadOnlyCollection<T>
        {
            private readonly ICollection<T> _collection;

            public CollectionWrapper(ICollection<T> collection)
            {
                if (collection == null)
                    throw new ArgumentNullException(nameof(collection));
                _collection = collection;
            }

            public int Count
            {
                get
                {
                    return _collection.Count;
                }
            }

            public IEnumerator<T> GetEnumerator()
            {
                return _collection.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return _collection.GetEnumerator();
            }
        }
    }
}
