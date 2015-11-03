using System.Collections.Generic;

namespace Foundatio.Collections
{
    public class NumberDictionary : Dictionary<string, long>
    {
        public NumberDictionary() : base()
        {
        }

        public NumberDictionary(IDictionary<string, long> items)
        {
            this.AddRange(items);
        }
    }
}
