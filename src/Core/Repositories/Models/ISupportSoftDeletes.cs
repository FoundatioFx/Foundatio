namespace Foundatio.Repositories.Models {
    public interface ISupportSoftDeletes {
        bool IsDeleted { get; set; }
    }
}