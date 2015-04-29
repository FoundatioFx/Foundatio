namespace Foundatio.Jobs {
    public class WorkItemStatus {
        public string WorkItemId { get; set; }
        public int Progress { get; set; }
        public string Message { get; set; }
        public string Type { get; set; }
    }
}