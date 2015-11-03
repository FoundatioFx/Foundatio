using System;
using System.Collections.Generic;
using Foundatio.Repositories.Models;
using Foundatio.Repositories.Models.Messaging;

namespace Foundatio.Repositories {
    public class DocumentsChangeEventArgs<T> : EventArgs where T : class, IIdentity, new() {
        public DocumentsChangeEventArgs(ChangeType changeType, ICollection<ModifiedDocument<T>> documents, IRepository<T> repository) {
            ChangeType = changeType;
            Documents = documents ?? new List<ModifiedDocument<T>>();
            Repository = repository;
        }

        public ChangeType ChangeType { get; private set; }
        public ICollection<ModifiedDocument<T>> Documents { get; private set; }
        public IRepository<T> Repository { get; private set; }
    }

    public class DocumentsEventArgs<T> : EventArgs where T : class, IIdentity, new() {
        public DocumentsEventArgs(ICollection<T> documents, IRepository<T> repository) {
            Documents = documents ?? new List<T>();
            Repository = repository;
        }

        public ICollection<T> Documents { get; private set; }
        public IRepository<T> Repository { get; private set; }
    }

    public class ModifiedDocumentsEventArgs<T> : EventArgs where T : class, IIdentity, new() {
        public ModifiedDocumentsEventArgs(ICollection<ModifiedDocument<T>> documents, IRepository<T> repository) {
            Documents = documents ?? new List<ModifiedDocument<T>>();
            Repository = repository;
        }

        public ICollection<ModifiedDocument<T>> Documents { get; private set; }
        public IRepository<T> Repository { get; private set; }
    }

    public class ModifiedDocument<T> where T : class, new() {
        public ModifiedDocument(T value, T original) {
            Value = value;
            Original = original;
        }

        public T Value { get; private set; }
        public T Original { get; private set; }
    }
}
