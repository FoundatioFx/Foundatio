using System;
using System.Collections.Generic;

namespace Foundatio.Logging
{
    public class PropertyBuilder
    {
        public IDictionary<string, object> Properties { get; } = new Dictionary<string, object>();

        public PropertyBuilder Property(string name, object value) {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            Properties[name] = value;
            return this;
        }

    }
}