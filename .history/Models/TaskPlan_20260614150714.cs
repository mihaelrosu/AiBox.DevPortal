using System;

domain namespace AiBox.DevPortal.Models {
    public class TaskPlan {
        public string OriginalRequest { get; set; }
        public List<TaskSlice> Slices { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}