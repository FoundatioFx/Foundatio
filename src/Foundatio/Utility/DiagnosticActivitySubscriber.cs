using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Foundatio.Diagnostics {
    public sealed class DiagnosticActivitySubscriber : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object>> {
        private readonly string _source;
        private readonly string _name;
        private readonly Action<object> _onStart;
        private readonly Action<object> _onStop;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        public DiagnosticActivitySubscriber(string source, string name, Action<object> onStart, Action<object> onStop) {
            _source = source;
            _name = name;
            _onStart = onStart;
            _onStop = onStop;
        }
        
        void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value) {
            if (value.Name != _source) return;
            
            var subscription = value.Subscribe(this);
            _subscriptions.Add(subscription);
        }
        
        void IObserver<DiagnosticListener>.OnError(Exception error) { }
        void IObserver<DiagnosticListener>.OnCompleted() {
            _subscriptions.ForEach(x => x.Dispose());
            _subscriptions.Clear();
        }
        
        void IObserver<KeyValuePair<string, object>>.OnNext(KeyValuePair<string, object> kvp) {
            if (kvp.Key == _name + ".Start")
                _onStart?.Invoke(kvp.Value);
            else if (kvp.Key == _name + ".Stop")
                _onStop?.Invoke(kvp.Value);
        }
        
        void IObserver<KeyValuePair<string, object>>.OnError(Exception error) { }
        void IObserver<KeyValuePair<string, object>>.OnCompleted() { }
    }
}