using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskPlanDependencyGraphService
{
    public TaskPlanExecutionGraph BuildExecutionGraph(IEnumerable<TaskPlanSlice> slices)
    {
        var result = new TaskPlanExecutionGraph();
        var sliceList = slices.ToList();
        var duplicateIds = sliceList
            .GroupBy(slice => slice.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        foreach (var duplicateId in duplicateIds)
        {
            result.ValidationErrors.Add($"Duplicate slice ID detected: {duplicateId}");
        }

        var sliceMap = sliceList
            .GroupBy(slice => slice.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recursionStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedIds = new List<string>();
        var uniqueSlices = sliceList
            .GroupBy(slice => slice.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        // 1. Validate Missing Dependencies
        foreach (var slice in uniqueSlices)
        {
            foreach (var depId in slice.DependsOnSliceIds)
            {
                if (!sliceMap.ContainsKey(depId))
                {
                    result.ValidationErrors.Add($"Slice {slice.Id} depends on non-existent ID: {depId}");
                }
            }
        }

        // 2. Topological Sort and Cycle Detection
        foreach (var slice in uniqueSlices)
        {
            if (!visited.Contains(slice.Id))
            {
                Visit(slice.Id, sliceMap, visited, recursionStack, orderedIds, result);
            }
        }

        result.OrderedSliceIds = orderedIds;
        return result;
    }

    private void Visit(string id, Dictionary<string, TaskPlanSlice> map, HashSet<string> visited, HashSet<string> recursionStack, List<string> orderedIds, TaskPlanExecutionGraph results)
    {
        visited.Add(id);
        recursionStack.Add(id);

        if (map.TryGetValue(id, out var slice))
        {
            foreach (var depId in slice.DependsOnSliceIds)
            {
                if (recursionStack.Contains(depId))
                {
                    // Cycle detected: the dependency is already in the current path.
                    results.CyclesDetected.Add($"Cycle involving {id} and {depId}");
                }
                else if (!visited.Contains(depId))
                {
                    Visit(depId, map, visited, recursionStack, orderedIds, results);
                }
            }
        }

        recursionStack.Remove(id);
        orderedIds.Add(id);
    }
}
