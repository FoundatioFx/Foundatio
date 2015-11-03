using System;

namespace Foundatio.Repositories.Models {
    public class FacetResult {
        public FacetResult() {
            Terms = new NumberDictionary();
        }

        public string Field { get; set; }
        public NumberDictionary Terms { get; set; }
    }
}
