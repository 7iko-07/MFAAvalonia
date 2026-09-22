using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering;
using MFAAvalonia.Controls;
using MFAAvalonia.Extensions;
using System.Reflection;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var tests = new (string Name, Action Run)[]
{
    ("Hidden pages do not resolve services; first attachment resolves once", () =>
    {
        var calls = 0;
        var expected = new Border { DataContext = new object() };
        var provider = new TestServiceProvider(_ => { calls++; return expected; });
        MFAAvalonia.App.Services = provider;
        var extension = new ServiceProviderExtension { ServiceType = typeof(Border), Deferred = true };
        var page = (DeferredView)extension.ProvideValue(provider);
        Check(calls == 0 && page.Child == null, "Hidden page was eagerly created");
        var root = new TestRoot { Child = page };
        Check(calls == 1 && ReferenceEquals(page.Child, expected), "Page was not created on attachment");
        var model = expected.DataContext;
        root.Child = null;
        root.Child = page;
        Check(calls == 1 && ReferenceEquals(page.Child, expected), "Page was recreated after navigation");
        Check(ReferenceEquals(expected.DataContext, model), "Page state was lost");
        root.Child = null;
    }),
    ("Normal markup resolution remains eager", () =>
    {
        var calls = 0;
        var expected = new Border();
        var provider = new TestServiceProvider(_ => { calls++; return expected; });
        MFAAvalonia.App.Services = provider;
        var result = new ServiceProviderExtension { ServiceType = typeof(Border) }.ProvideValue(provider);
        Check(calls == 1 && ReferenceEquals(result, expected), "Non-deferred resolution changed");
    }),
    ("Deferred wrapper preserves stretch layout and inherited DataContext", () =>
    {
        var expected = new Border();
        var page = new DeferredView(() => expected);
        var model = new object();
        var root = new TestRoot { DataContext = model, Child = page };
        page.Measure(new Size(800, 600));
        page.Arrange(new Rect(0, 0, 800, 600));
        Check(expected.Bounds.Size == new Size(800, 600), "Deferred page no longer fills its host");
        Check(ReferenceEquals(expected.DataContext, model), "Inherited DataContext was lost");
        root.Child = null;
    }),
    ("A failed page factory can retry on the next attachment", () =>
    {
        var calls = 0;
        var page = new DeferredView(() =>
        {
            if (++calls == 1) throw new InvalidOperationException("Expected factory failure");
            return new Border();
        });
        var root = new TestRoot();
        try
        {
            root.Child = page;
            throw new Exception("Factory failure was swallowed");
        }
        catch (InvalidOperationException ex) when (ex.Message == "Expected factory failure") { }
        root.Child = null;
        root.Child = page;
        Check(calls == 2 && page.Child is Border, "Factory could not retry");
        root.Child = null;
    }),
    ("Deferred markup rejects non-control services without resolving them", () =>
    {
        var provider = new TestServiceProvider(_ => throw new Exception("Should not resolve"));
        MFAAvalonia.App.Services = provider;
        try
        {
            new ServiceProviderExtension { ServiceType = typeof(object), Deferred = true }.ProvideValue(provider);
            throw new Exception("Non-control service was accepted");
        }
        catch (InvalidOperationException) { }
    })
};

foreach (var (name, run) in tests)
{
    run();
    Console.WriteLine($"PASS: {name}");
}
Console.WriteLine($"Passed {tests.Length} first-screen lifecycle checks.");

sealed class TestServiceProvider(Func<Type, object> resolve) : IServiceProvider
{
    public object GetService(Type serviceType) => resolve(serviceType);
}

// A visual root without a native window, device connection or rendering backend.
sealed class TestRoot : Decorator, IRenderRoot
{
    public Size ClientSize => new(800, 600);
    public IRenderer Renderer { get; } = DispatchProxy.Create<IRenderer, NoOpRenderer>();
    public IHitTester HitTester => null!;
    public double RenderScaling => 1;
    public Point PointToClient(PixelPoint point) => new(point.X, point.Y);
    public PixelPoint PointToScreen(Point point) => new((int)point.X, (int)point.Y);
}

class NoOpRenderer : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var type = targetMethod!.ReturnType;
        return type != typeof(void) && type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

namespace MFAAvalonia
{
    // Keep markup-extension tests isolated from application startup and user configuration.
    internal static class App
    {
        public static IServiceProvider Services { get; set; } = null!;
    }
}
