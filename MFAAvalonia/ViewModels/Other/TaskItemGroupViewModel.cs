using CommunityToolkit.Mvvm.ComponentModel;
using MFAAvalonia.Helper.ValueType;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MFAAvalonia.ViewModels.Other;

public partial class TaskItemGroupViewModel : ObservableObject
{
    public string Name { get; set; } = string.Empty;

    [ObservableProperty] private string _label = string.Empty;

    [ObservableProperty] private string _description = string.Empty;

    [ObservableProperty] private bool _hasDescription;

    [ObservableProperty] private string _icon = string.Empty;

    [ObservableProperty] private bool _hasIcon;

    [ObservableProperty] private bool _isExpanded = true;

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
