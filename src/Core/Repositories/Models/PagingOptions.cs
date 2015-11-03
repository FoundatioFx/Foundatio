namespace Foundatio.Repositories {
    public class PagingOptions {
        public int? Limit { get; set; }
        public int? Page { get; set; }
        public bool UseSnapshotPaging { get; set; }

        static public implicit operator PagingOptions(int limit) {
            return new PagingOptions { Limit = limit };
        }
    }

    public static class PagingOptionsExtensions {
        public static PagingOptions WithLimit(this PagingOptions options, int? limit) {
            options.Limit = limit;
            return options;
        }

        public static PagingOptions WithPage(this PagingOptions options, int? page) {
            options.Page = page;
            return options;
        }

        public static PagingOptions UseSnapshotPaging(this PagingOptions options, bool useSnapshotPaging = true) {
            options.UseSnapshotPaging = useSnapshotPaging;
            return options;
        }
    }
}
