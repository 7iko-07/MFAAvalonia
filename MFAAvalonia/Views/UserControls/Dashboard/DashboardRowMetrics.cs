using System;
using System.Linq;
using Avalonia.Controls;

namespace MFAAvalonia.Views.UserControls.Dashboard;

/// <summary>供卡片、拖动预览和分隔条共用的行坐标，支持固定高度与剩余空间分配。</summary>
internal sealed class DashboardRowMetrics
{
    private readonly double[] _offsets;
    private readonly double _spacing;

    public DashboardRowMetrics(int rows, double height, double spacing, string? rowHeights)
    {
        var definitions = string.IsNullOrWhiteSpace(rowHeights) ? null : RowDefinitions.Parse(rowHeights);
        var heights = Enumerable.Range(0, rows)
            .Select(row => definitions != null && row < definitions.Count
                ? definitions[row].Height : GridLength.Star).ToArray();
        var available = Math.Max(0, height - (rows - 1) * spacing);
        var fixedHeight = heights.Where(length => length.IsAbsolute).Sum(length => length.Value);
        var starWeight = heights.Where(length => length.IsStar).Sum(length => length.Value);
        var fixedScale = fixedHeight > available ? available / fixedHeight : 1;
        var starUnit = starWeight > 0 ? Math.Max(0, available - fixedHeight) / starWeight : 0;

        _spacing = spacing;
        _offsets = new double[rows + 1];
        for (var row = 0; row < rows; row++)
        {
            var length = heights[row];
            var rowHeight = length.IsAbsolute ? length.Value * fixedScale : length.Value * starUnit;
            _offsets[row + 1] = _offsets[row] + rowHeight + spacing;
        }
    }

    public double Offset(int row) => _offsets[row];

    public double Height(int row, int span) => Math.Max(0, _offsets[row + span] - _offsets[row] - _spacing);

    public int FindRow(double offset)
    {
        var nearest = 0;
        for (var row = 1; row < _offsets.Length; row++)
        {
            if (Math.Abs(_offsets[row] - offset) < Math.Abs(_offsets[nearest] - offset))
                nearest = row;
        }
        return nearest;
    }
}
