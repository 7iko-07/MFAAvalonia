using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Pages;

internal static partial class Program
{
    private static void CheckTaskSelections()
    {
        var tasks = Enumerable.Range(0, 4).Select(index => Item(index.ToString())).ToArray();
        Set(tasks[0], "_isCheckedWithNull", true);
        Set(tasks[1], "_isCheckedWithNull", false);
        Set(tasks[2], "_isCheckedWithNull", null);
        var checkedStates = tasks.Select(item => item.IsCheckedWithNull).ToArray();
        var queue = (TaskQueueViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskQueueViewModel));
        Set(queue, "_taskItemViewModels", new ObservableCollection<DragItemViewModel>(tasks));
        Set(queue, "_hasTaskGroups", true);
        foreach (var task in tasks) task.OwnerViewModel = queue;
        var panelOpenRequests = 0;
        queue.SetOptionRequested += (_, open) => { if (open) panelOpenRequests++; };
        var lists = new[]
        {
            new ListBox { ItemsSource = tasks.Take(1).ToArray() },
            new ListBox { ItemsSource = tasks.Skip(1).Take(1).ToArray() },
            new ListBox { ItemsSource = tasks.Skip(2).ToArray() },
        };
        var panel = new StackPanel();
        foreach (var list in lists) panel.Children.Add(list);
        var groups = new ItemsControl { Template = new FuncControlTemplate<ItemsControl>((_, _) => panel) };
        var view = new TaskQueueView { DataContext = queue, TaskGroupList = groups, Child = groups };
        view.TaskListBox.ItemsSource = tasks;
        var root = new TestRoot { Child = view };
        Layout(view);

        void Check(DragItemViewModel expected, ListBox expectedList, string scenario)
        {
            var allLists = lists.Append(view.TaskListBox).ToArray();
            if (allLists.Count(list => list.SelectedItem != null) != 1 ||
                !ReferenceEquals(expectedList.SelectedItem, expected) ||
                !tasks.Where(item => item.EnableSetting).SequenceEqual(new[] { expected }))
                throw new Exception($"{scenario}: highlight and settings must share one current task");
            if (!tasks.Select(item => item.IsCheckedWithNull).SequenceEqual(checkedStates))
                throw new Exception($"{scenario}: selection changed task execution checkboxes");
            Console.WriteLine("PASS " + scenario);
        }

        // Reproduce the screenshot: each segment and the hidden flat list retain selection.
        for (var index = 0; index < lists.Length; index++) lists[index].SelectedItem = tasks[index];
        view.TaskListBox.SelectedItem = tasks[3];
        view.Select(tasks[0], lists[0]);
        Check(tasks[0], lists[0], "click clears selections in other task segments");

        foreach (var list in lists.Append(view.TaskListBox)) view.ConnectSelection(list);
        lists[1].SelectedItem = tasks[1];
        Check(tasks[1], lists[1], "selection event synchronizes settings and all task segments");
        lists[1].IsVisible = false;
        view.Select(tasks[2], lists[2]);
        Check(tasks[2], lists[2], "checkbox click clears selection in collapsed task segment");
        view.Select(tasks[3], lists[2]);
        Check(tasks[3], lists[2], "same-list task switch remains exclusive");
        var previousOpenRequests = panelOpenRequests;
        view.CleanupSelection();
        Check(tasks[3], lists[2], "refresh keeps current grouped task without selecting hidden flat list");
        if (panelOpenRequests != previousOpenRequests)
            throw new Exception("refresh must not reopen the already active settings panel");
        Console.WriteLine("PASS selection synchronization does not reopen active settings panel");

        lists[2].SelectedItem = null;
        view.CleanupSelection();
        Check(tasks[3], lists[2], "refresh restores current task into its owning list");
        Set(tasks[3], "_isTaskSupported", false);
        view.CleanupSelection();
        Check(tasks[0], lists[0], "unsupported current task falls back to one supported task");

        lists[0].SelectedItem = null;
        tasks[0].EnableSetting = false;
        view.CleanupSelection();
        Check(tasks[0], lists[0], "initial grouped selection uses visible task list");

        Set(queue, "_hasTaskGroups", false);
        view.TaskListBox.SelectedItem = tasks[2];
        Check(tasks[2], view.TaskListBox, "flat-list selection clears old grouped selections");
        view.CleanupSelection();
        Check(tasks[2], view.TaskListBox, "flat-list refresh preserves current task");
        root.Child = null;
    }
}
