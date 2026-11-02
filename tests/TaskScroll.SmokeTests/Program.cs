using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Pages;

internal static partial class Program
{
    private static readonly string Build = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../bin/AnyCPU/Debug"));
    [ModuleInitializer]
    internal static void Resolve() => AssemblyLoadContext.Default.Resolving += (_, name) =>
        File.Exists(Path.Combine(Build, name.Name + ".dll")) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Build, name.Name + ".dll")) : null;

    internal static void Set(object instance, string name, object? value) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static DragItemViewModel Item(string id)
    {
        var item = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
        Set(item, "_interfaceItem", new MaaInterface.MaaInterfaceTask { LocalId = id, Name = id });
        Set(item, "_isTaskSupported", true);
        return item;
    }

    public static void Main()
    {
        try { Run(); }
        catch (Exception error) { Console.WriteLine(error); Environment.Exit(1); }
    }

    private static void Run()
    {
        CheckTaskSelections();
        foreach (var grouped in new[] { false, true })
        foreach (var scenario in new[] { "visible-selection", "offscreen-selection", "no-selection", "replacement-items", "deleted-selection", "coalesced-refresh" })
        {
            var tasks = Enumerable.Range(0, 30).Select(i => Item(i.ToString())).ToList();
            var queue = (TaskQueueViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskQueueViewModel));
            Set(queue, "_taskItemViewModels", new ObservableCollection<DragItemViewModel>(tasks));
            Set(queue, "_hasTaskGroups", grouped);
            var selected = scenario == "no-selection" ? null : tasks[scenario == "offscreen-selection" ? 2 : 12];
            if (selected != null) Set(selected, "_enableSetting", true);
            var view = new TaskQueueView { DataContext = queue, Width = 300, Height = 200 };
            var root = new TestRoot { Child = view };
            view.Fill(tasks, grouped);
            Layout(view);
            view.Scroll.Offset = new Vector(0, 400);
            Layout(view);
            var originalOffset = view.Scroll.Offset.Y;
            if (originalOffset != 400) throw new Exception($"test setup offset={originalOffset}, extent={view.Scroll.Extent}, viewport={view.Scroll.Viewport}");
            var anchorId = selected != null && scenario != "offscreen-selection" ? "12" : "10";
            var oldY = view.RowY(anchorId);
            view.Capture();
            var replacement = scenario == "replacement-items" ? tasks.Select(t => Item(t.InterfaceItem!.LocalId!)).ToList() : tasks.ToList();
            if (scenario == "deleted-selection") replacement.RemoveAt(12);
            else replacement.InsertRange(0, new[] { Item("new-a"), Item("new-b") });
            Set(queue, "_taskItemViewModels", new ObservableCollection<DragItemViewModel>(replacement));
            view.Fill(replacement, grouped);
            Layout(view);
            view.Scroll.Offset = default;
            Layout(view);
            if (scenario == "coalesced-refresh") view.Capture();
            Dispatcher.UIThread.RunJobs();
            Layout(view);
            Dispatcher.UIThread.RunJobs();
            Layout(view);
            if (scenario == "deleted-selection")
            {
                if (Math.Abs(view.Scroll.Offset.Y - originalOffset) > .1) throw new Exception("deleted anchor loses numeric offset");
            }
            else if (Math.Abs(view.RowY(anchorId) - oldY) > .1)
                throw new Exception($"{grouped}/{scenario}: row moved from {oldY} to {view.RowY(anchorId)}, offset {view.Scroll.Offset.Y}");
            if (selected != null && scenario != "deleted-selection" &&
                (view.ActiveList.SelectedItem as DragItemViewModel)?.InterfaceItem?.LocalId != selected.InterfaceItem!.LocalId)
                throw new Exception("the original task selection was not restored");
            if (!view.ActiveList.AutoScrollToSelectedItem) throw new Exception("normal automatic scrolling was not restored");
            Console.WriteLine($"PASS {(grouped ? "grouped" : "flat")} {scenario}");
            root.Child = null;
        }
    }

    private static void Layout(Control control)
    {
        foreach (var node in control.GetVisualDescendants().OfType<Control>().Prepend(control).Reverse())
        {
            node.InvalidateMeasure();
            node.InvalidateArrange();
        }
        control.Measure(new Size(300, 200));
        control.Arrange(new Rect(0, 0, 300, 200));
    }
}

namespace MFAAvalonia.Views.Pages
{
    // Host the production viewport code with simple templates, without starting
    // the application's services, native windows, resource loading or devices.
    public partial class TaskQueueView : Decorator
    {
        internal ListBox TaskListBox = new();
        internal ScrollViewer TaskGroupScrollViewer = new();
        internal ItemsControl TaskGroupList = new();
        internal ListBox ActiveList = null!;
        internal ScrollViewer Scroll = null!;
        internal void Capture() => OnTaskListChanging();
        internal double RowY(string id) => ActiveList.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(row => (row.DataContext as DragItemViewModel)?.InterfaceItem?.LocalId == id).TranslatePoint(default, Scroll)!.Value.Y;

        internal void Fill(List<DragItemViewModel> tasks, bool grouped)
        {
            var rows = new StackPanel();
            foreach (var item in tasks)
                rows.Children.Add(new ListBoxItem { DataContext = item, Height = 40 });
            Scroll = new ScrollViewer
            {
                Content = rows,
                Template = new FuncControlTemplate<ScrollViewer>((owner, scope) =>
                {
                    var presenter = new ScrollContentPresenter { Name = "PART_ContentPresenter", CanVerticallyScroll = true, Content = owner.Content };
                    scope.Register(presenter.Name, presenter);
                    presenter.Bind(ScrollContentPresenter.OffsetProperty, owner.GetObservable(ScrollViewer.OffsetProperty));
                    return presenter;
                })
            };
            ActiveList = new ListBox
            {
                ItemsSource = tasks,
                Template = new FuncControlTemplate<ListBox>((_, _) => grouped ? rows : Scroll)
            };
            ConnectSelection(ActiveList);
            if (grouped)
            {
                TaskGroupList = new ItemsControl { Template = new FuncControlTemplate<ItemsControl>((_, _) => ActiveList) };
                Scroll.Content = TaskGroupList;
                TaskGroupScrollViewer = Scroll;
                Child = Scroll;
            }
            else Child = TaskListBox = ActiveList;
        }

        internal void ConnectSelection(ListBox list) => list.SelectionChanged += SelectingItemsControl_OnSelectionChanged;
        internal void Select(DragItemViewModel item, Control? source) => SelectTaskForSettings(item, source);
        internal void CleanupSelection() => ClearUnsupportedSelection();
    }
}

sealed class TestRoot : Decorator, IRenderRoot, Avalonia.LogicalTree.ILogicalRoot
{
    public Size ClientSize => new(300, 200);
    public IRenderer Renderer { get; } = DispatchProxy.Create<IRenderer, NoOpRenderer>();
    public IHitTester HitTester => null!;
    public double RenderScaling => 1;
    public Point PointToClient(PixelPoint point) => new(point.X, point.Y);
    public PixelPoint PointToScreen(Point point) => new((int)point.X, (int)point.Y);
}

class NoOpRenderer : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method!.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
}
