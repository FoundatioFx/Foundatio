using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Foundatio.Utility {
    public class SortedQueue<TKey, TValue> : IProducerConsumerCollection<KeyValuePair<TKey, TValue>>
        where TKey : IComparable<TKey> {

        private readonly object _lock = new object();
        private readonly SortedDictionary<TKey, TValue> _sortedDictionary = new SortedDictionary<TKey, TValue>();

        public SortedQueue() { }

        public SortedQueue(IEnumerable<KeyValuePair<TKey, TValue>> collection) {
            if (collection == null)
                throw new ArgumentNullException(nameof(collection));

            foreach (var kvp in collection)
                _sortedDictionary.Add(kvp.Key, kvp.Value);
        }

        public void Enqueue(TKey key, TValue value) {
            Enqueue(new KeyValuePair<TKey, TValue>(key, value));
        }

        public void Enqueue(KeyValuePair<TKey, TValue> item) {
            lock (_lock)
                _sortedDictionary.Add(item.Key, item.Value);
        }

        public bool TryDequeue(out KeyValuePair<TKey, TValue> item) {
            item = default;

            lock (_lock) {
                if (_sortedDictionary.Count > 0) {
                    item = _sortedDictionary.First();
                    return _sortedDictionary.Remove(item.Key);
                }
            }

            return false;
        }

        public bool TryDequeueIf(out KeyValuePair<TKey, TValue> item, Predicate<TValue> condition) {
            item = default;

            lock (_lock) {
                if (_sortedDictionary.Count > 0) {
                    item = _sortedDictionary.First();
                    if (!condition(item.Value))
                        return false;

                    return _sortedDictionary.Remove(item.Key);
                }
            }

            return false;
        }

        public bool TryPeek(out KeyValuePair<TKey, TValue> item) {
            item = default;

            lock (_lock) {
                if (_sortedDictionary.Count > 0) {
                    item = _sortedDictionary.First();
                    return true;
                }
            }

            return false;
        }

        public void Clear() {
            lock (_lock)
                _sortedDictionary.Clear();
        }

        public bool IsEmpty => Count == 0;

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() {
            var array = ToArray();
            return array.AsEnumerable().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return GetEnumerator();
        }

        void ICollection.CopyTo(Array array, int index) {
            lock (_lock)
                ((ICollection)_sortedDictionary).CopyTo(array, index);
        }

        public int Count {
            get {
                lock (_lock)
                    return _sortedDictionary.Count;
            }
        }

        object ICollection.SyncRoot => _lock;

        bool ICollection.IsSynchronized => true;

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int index) {
            lock (_lock)
                _sortedDictionary.CopyTo(array, index);
        }

        bool IProducerConsumerCollection<KeyValuePair<TKey, TValue>>.TryAdd(KeyValuePair<TKey, TValue> item) {
            Enqueue(item);
            return true;
        }

        bool IProducerConsumerCollection<KeyValuePair<TKey, TValue>>.TryTake(out KeyValuePair<TKey, TValue> item) {
            return TryDequeue(out item);
        }

        public KeyValuePair<TKey, TValue>[] ToArray() {
            lock (_lock)
                return _sortedDictionary.ToArray();
        }
    }
}