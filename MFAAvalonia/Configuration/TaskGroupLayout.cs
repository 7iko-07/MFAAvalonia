using System.Collections.Generic;

namespace MFAAvalonia.Configuration;

public sealed class TaskGroupLayout
{
    public Dictionary<string, string> TaskGroups { get; set; } = [];
    public List<string> TaskOrder { get; set; } = [];
    public List<string> BottomTasks { get; set; } = [];
    public List<TaskGroupDefinition> Groups { get; set; } = [];
    public List<string> DeletedGroups { get; set; } = [];
    // Empty groups have no task ID in TaskOrder. Anchor them to the next task;
    // an empty string represents the end of the list.
    public Dictionary<string, string> EmptyGroupNextTasks { get; set; } = [];
}

public sealed class TaskGroupDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
