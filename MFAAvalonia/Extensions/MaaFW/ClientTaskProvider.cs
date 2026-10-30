using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Extensions.MaaFW;

internal static class ClientTaskProvider
{
    public const string StartGroupName = "__mfa_start_tasks";
    public const string CloseEmulatorEntry = "__mfa_close_emulator";
    public const string CloseMfaEntry = "__mfa_close_mfa";

    public static bool IsClientEntry(string? entry) => entry is
        MaaInterface.MaaInterfaceTask.StartEmulatorEntry or CloseEmulatorEntry or CloseMfaEntry;

    public static string LabelKey(string? entry) => entry switch
    {
        MaaInterface.MaaInterfaceTask.StartEmulatorEntry => "StartEmulatorTask",
        CloseEmulatorEntry => LangKeys.CloseEmulator,
        CloseMfaEntry => LangKeys.CloseMFA,
        _ => string.Empty,
    };

    public static string GroupLabel(string name) => name switch
    {
        StartGroupName => "StartTaskGroup".ToLocalization(),
        EndTaskPolicy.GroupName => "EndTaskGroup".ToLocalization(),
        _ => name,
    };

    private static string ResolveGroup(MaaInterface? definition, bool start, MaaInterface.MaaInterfacePreset? preset = null)
    {
        var name = start ? StartGroupName : EndTaskPolicy.GroupName;
        var chineseLabel = start ? "启动前执行" : "结束后执行";
        var existing = (preset?.Group ?? []).Concat(definition?.Group ?? [])
            .FirstOrDefault(group => group.Name == name || group.Name == chineseLabel ||
            LanguageHelper.GetLocalizedDisplayName(group.Label, group.Name) is { } label &&
            (label == chineseLabel || label == GroupLabel(name)));
        return existing?.Name ?? definition?.Task?.SelectMany(task => task.Group ?? [])
            .FirstOrDefault(group => group == name || group == chineseLabel) ?? name;
    }

    public static List<MaaInterface.MaaInterfaceTask> CreateTasks(
        MaaInterface? definition, MaaInterface.MaaInterfacePreset? preset = null) =>
    new List<MaaInterface.MaaInterfaceTask>
    {
        new() { Name = MaaInterface.MaaInterfaceTask.StartEmulatorEntry, Entry = MaaInterface.MaaInterfaceTask.StartEmulatorEntry,
            Check = false, Group = [ResolveGroup(definition, true, preset)] },
        new() { Name = CloseEmulatorEntry, Entry = CloseEmulatorEntry, EndAction = EndTaskPolicy.CloseEmulator,
            Check = false, Group = [ResolveGroup(definition, false, preset)] },
        new() { Name = CloseMfaEntry, Entry = CloseMfaEntry, EndAction = EndTaskPolicy.CloseMFA,
            Check = false, Group = [ResolveGroup(definition, false, preset)] },
    }.Where(task => task.IsStartTask
        ? definition?.Task?.Any(resourceTask => resourceTask.IsStartupTask) != true
        : definition?.Task?.Any(resourceTask => resourceTask.IsShutdownTask) != true).ToList();

    public static List<MaaInterface.MaaInterfaceTask> GetTaskTemplates(
        MaaInterface? definition, IEnumerable<MaaInterface.MaaInterfaceTask> resourceTasks,
        MaaInterface.MaaInterfacePreset? preset = null) =>
        resourceTasks.Where(task => !task.IsClientTask).Concat(CreateTasks(definition, preset))
            .OrderBy(task => DefaultOrder(task, definition, preset)).ToList();

    public static int DefaultOrder(MaaInterface.MaaInterfaceTask? task, MaaInterface? definition,
        MaaInterface.MaaInterfacePreset? preset = null)
    {
        if (task?.IsStartupTask == true) return 0;
        if (task?.IsShutdownTask == true) return 1;
        var group = task?.Group?.FirstOrDefault();
        if (group == ResolveGroup(definition, true, preset)) return task!.IsStartTask ? 0 : 1;
        if (group == ResolveGroup(definition, false, preset)) return task!.Entry switch
        {
            CloseEmulatorEntry => 3,
            CloseMfaEntry => 4,
            _ => 2,
        };
        return 5;
    }

    // Old resources used display names as task IDs. Keep deletion history when IDs become client-owned.
    public static string NormalizeHistoryKey(string key)
    {
        var parts = key.Split(TaskLoader.NEW_SEPARATOR, 2, StringSplitOptions.None);
        return parts.Length == 2 && IsClientEntry(parts[1])
            ? $"{parts[1]}{TaskLoader.NEW_SEPARATOR}{parts[1]}" : key;
    }
}
