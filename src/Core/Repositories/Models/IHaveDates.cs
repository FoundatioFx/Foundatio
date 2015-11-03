using System;

namespace Foundatio.Repositories.Models {
    public interface IHaveDates : IHaveCreatedDate {
        DateTime UpdatedUtc { get; set; }
    }

    public interface IHaveCreatedDate {
        DateTime CreatedUtc { get; set; }
    }
}
