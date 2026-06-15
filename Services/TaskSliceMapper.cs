using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceMapper
{
    public TaskPlanSlice ToTaskPlanSlice(TaskSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        return slice;
    }

    public TaskSlice ToTaskSlice(TaskPlanSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        return slice;
    }
}
