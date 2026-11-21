using MFAAvalonia.Views.UserControls.Dashboard;

static void Equal(double actual, double expected, string label)
{
    if (Math.Abs(actual - expected) > 0.0001)
        throw new Exception($"{label}: expected {expected}, got {actual}");
}

const string defaults = "49,49,*,*,*,*,90,90";
foreach (var height in new[] { 400.0, 600, 900 })
{
    var metrics = new DashboardRowMetrics(8, height, 10, defaults);
    Equal(metrics.Height(0, 2), 108, "Two-line description stays compact");
    Equal(metrics.Height(6, 2), 190, "Four-line log stays compact");
    Equal(metrics.Height(0, 8), height, "Rows fill available height");
    Equal(metrics.Height(2, 4), height - 318, "Settings receive remaining height");
    for (var row = 0; row <= 8; row++)
        Equal(metrics.FindRow(metrics.Offset(row)), row, "Drag/splitter boundary round trip");
    Equal(metrics.FindRow(metrics.Offset(2) + 1), 2, "Small drag stays on same boundary");
    Console.WriteLine($"PASS fixed description/log, elastic settings and drag boundaries at height={height}");
}

var legacy = new DashboardRowMetrics(8, 650, 10, null);
for (var row = 0; row < 8; row++)
{
    Equal(legacy.Offset(row), row * 82.5, "Legacy equal-row offsets");
    Equal(legacy.Height(row, 1), 72.5, "Legacy equal-row heights");
}
Console.WriteLine("PASS legacy layouts retain equal rows");

var weighted = new DashboardRowMetrics(4, 360, 10, "50,*,2*,40");
Equal(weighted.Height(1, 1), 80, "Single star weight");
Equal(weighted.Height(2, 1), 160, "Double star weight");
Equal(weighted.Height(0, 4), 360, "Weighted rows fill space");
Console.WriteLine("PASS weighted remaining-space rows");

var tiny = new DashboardRowMetrics(8, 200, 10, defaults);
Equal(tiny.Height(0, 8), 200, "Short viewport does not overflow");
for (var row = 0; row < 8; row++)
    if (tiny.Height(row, 1) < 0) throw new Exception("Negative row height");
Console.WriteLine("PASS constrained viewports do not overflow or produce negative heights");
