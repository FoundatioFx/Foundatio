using Foundatio.Collections;

namespace Foundatio.Repositories {
    public class FacetResult {
        public FacetResult() {
            Terms = new NumberDictionary();
        }

        public string Field { get; set; }
        public NumberDictionary Terms { get; set; }
    }
}
