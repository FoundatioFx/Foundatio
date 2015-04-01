namespace Foundatio.Jobs {
    public class LongRunningTaskStatus {
        public string JobId { get; set; }
        public int Progress { get; set; }
        public string Message { get; set; }
    }
}