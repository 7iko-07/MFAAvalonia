using CommunityToolkit.Mvvm.ComponentModel;
using MFAAvalonia.Helper.ValueType;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.ViewModels.Pages;

namespace MFAAvalonia.ViewModels.Other;

public partial class TaskItemGroupViewModel : ObservableObject, IDisposable
{
    public TaskQueueViewModel? Owner { get; private set; }
    public bool IsUngrouped { get; private set; }
    public bool ShowGroup => !IsUngrouped || Items.Count > 0;
    public bool CanEdit => Owner?.Idle == true;
    private IEnumerable<DragItemViewModel> SelectionItems => Owner != null && !IsUngrouped
        ? Owner.TaskItemGroups.Where(group => !group.IsUngrouped && group.Name == Name).SelectMany(group => group.Items)
        : Items;
    public bool CanSelect => CanEdit && SelectionItems.Any(item => !item.IsResourceOptionItem);
    public bool? SelectionState
    {
        get
        {
            var tasks = SelectionItems.Where(item => !item.IsResourceOptionItem).ToList();
            var eligible = tasks.Where(item => item.InterfaceItem?.ExcludeFromSelectAll != true).ToList();
            if (tasks.Count == 0 || tasks.All(item => !item.IsChecked)) return false;
            if (tasks.All(item => item.IsChecked) || eligible.Count > 0 && eligible.All(item => item.IsChecked)) return true;
            return null;
        }
    }

    [RelayCommand]
    private void ToggleSelection()
    {
        if (!CanSelect) return;
        var select = SelectionState != true;
        foreach (var item in SelectionItems.Where(item => !item.IsResourceOptionItem))
            item.IsChecked = select && item.InterfaceItem?.ExcludeFromSelectAll != true;
    }
    private readonly System.Collections.Generic.HashSet<DragItemViewModel> _observedItems = [];

    public void Attach(TaskQueueViewModel owner, bool isUngrouped)
    {
        Owner = owner;
        IsUngrouped = isUngrouped;
        owner.PropertyChanged += OnOwnerChanged;
        Items.CollectionChanged += OnItemsChanged;
        ObserveItems();
    }

    [RelayCommand] private void Delete() => Owner?.DeleteTaskGroup(this);

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TaskQueueViewModel.Idle)) return;
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(SelectionState));
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Owner != null) Owner.RefreshTaskGroupSelection(this);
        else ObserveItems();
    }
    internal void RefreshSelection() => ObserveItems();
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(SelectionState));
    private void ObserveItems()
    {
        OnPropertyChanged(nameof(ShowGroup));
        foreach (var item in _observedItems) item.PropertyChanged -= OnItemChanged;
        _observedItems.Clear();
        foreach (var item in SelectionItems)
            if (_observedItems.Add(item)) item.PropertyChanged += OnItemChanged;
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(SelectionState));
    }

    public void Dispose()
    {
        if (Owner != null) Owner.PropertyChanged -= OnOwnerChanged;
        Items.CollectionChanged -= OnItemsChanged;
        foreach (var item in _observedItems) item.PropertyChanged -= OnItemChanged;
        _observedItems.Clear();
    }
    public string Name { get; set; } = string.Empty;

    [ObservableProperty] private string _label = string.Empty;

    [ObservableProperty] private string _description = string.Empty;

    [ObservableProperty] private bool _hasDescription;

    [ObservableProperty] private string _icon = string.Empty;

    [ObservableProperty] private bool _hasIcon;

    [ObservableProperty] private bool _isExpanded = true;

    partial void OnIsExpandedChanged(bool value) => Owner?.SynchronizeTaskGroupExpansion(this, value);

    public ObservableCollection<DragItemViewModel> Items { get; } = [];

    public void ResetItems(IEnumerable<DragItemViewModel> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
    }
}
