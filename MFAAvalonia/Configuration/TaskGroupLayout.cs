using System.Collections.Generic;

namespace MFAAvalonia.Configuration;

public sealed class TaskGroupLayout
{
    public Dictionary<string, string> TaskGroups { get; set; } = [];
    public List<string> TaskOrder { get; set; } = [];
    public List<TaskGroupDefinition> Groups { get; set; } = [];
    public List<string> DeletedGroups { get; set; } = [];
}

public sealed class TaskGroupDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
