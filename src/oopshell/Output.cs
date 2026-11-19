using System.Drawing;
using oopshell.Protocol;

namespace oopshell;

/// <summary>A monitor. Properties update atomically; <see cref="Changed"/> fires after each update.</summary>
public sealed class Output
{
    private readonly ZxdgOutputV1? _xdgOutput;
    private Point _geometryPosition;

    internal Output(OopsApp app, GlobalInfo global)
    {
        Global = global;
        Proxy = app.Bind<WlOutput>(global);
        Proxy.Geometry += e =>
        {
            _geometryPosition = new Point(e.X, e.Y);
            Make = e.Make;
            Model = e.Model;
            Transform = e.Transform;
        };
        Proxy.Mode += e =>
        {
            if ((e.Flags & WlOutputMode.Current) == 0)
                return;
            PixelSize = new Size(e.Width, e.Height);
            RefreshMilliHz = e.Refresh;
        };
        Proxy.Scale += e => Scale = e.Factor;
        Proxy.Name += e => Name = e.Name;
        Proxy.Description += e => Description = e.Description;
        Proxy.Done += OnDone;

        if (app.TryGet<ZxdgOutputManagerV1>() is { } manager)
        {
            _xdgOutput = manager.GetXdgOutput(Proxy);
            _xdgOutput.LogicalPosition += e => LogicalPosition = new Point(e.X, e.Y);
            _xdgOutput.LogicalSize += e => LogicalSize = new Size(e.Width, e.Height);
            // Before xdg_output v3 the xdg_output has its own done event, sent after wl_output.done.
            if (_xdgOutput.BoundVersion < 3)
                _xdgOutput.Done += OnDone;
        }
    }

    internal GlobalInfo Global { get; }

    /// <summary>The raw <c>wl_output</c> (escape hatch).</summary>
    public WlOutput Proxy { get; }

    /// <summary>Connector-like name, e.g. <c>DP-1</c>.</summary>
    public string Name { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string Make { get; private set; } = "";
    public string Model { get; private set; } = "";
    /// <summary>Position in the compositor's logical space.</summary>
    public Point LogicalPosition { get; private set; }
    /// <summary>Size in logical pixels (after scale and transform).</summary>
    public Size LogicalSize { get; private set; }
    /// <summary>Size of the current mode in hardware pixels.</summary>
    public Size PixelSize { get; private set; }
    /// <summary>Integer scale factor.</summary>
    public int Scale { get; private set; } = 1;
    /// <summary>Rotation and flip of the screen (90° for a portrait monitor); <see cref="LogicalSize"/> already applies it.</summary>
    public WlOutputTransform Transform { get; private set; }
    /// <summary>Refresh rate of the current mode in millihertz (60000 = 60 Hz); 0 if unknown.</summary>
    public int RefreshMilliHz { get; private set; }

    /// <summary>True once the compositor sent the first complete description.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Raised after the output's properties changed.</summary>
    public event Action? Changed;

    /// <summary>Raised when the output is unplugged. The object is dead afterwards.</summary>
    public event Action? Removed;

    internal event Action<Output>? FirstDone;

    private void OnDone()
    {
        if (_xdgOutput is null)
        {
            LogicalPosition = _geometryPosition;
            var size = IsRotated(Transform) ? new Size(PixelSize.Height, PixelSize.Width) : PixelSize;
            LogicalSize = new Size(size.Width / Math.Max(Scale, 1), size.Height / Math.Max(Scale, 1));
        }
        bool first = !IsReady;
        IsReady = true;
        if (first)
            FirstDone?.Invoke(this);
        else
            Changed?.Invoke();
    }

    private static bool IsRotated(WlOutputTransform t) => ((uint)t & 1) == 1;

    internal void Remove()
    {
        Removed?.Invoke();
        _xdgOutput?.Dispose();
        Proxy.Dispose();
    }

    public override string ToString() => $"{Name} {PixelSize.Width}x{PixelSize.Height}@{RefreshMilliHz / 1000.0:0.##} scale {Scale}";
}

public sealed partial class OopsApp
{
    private readonly List<Output> _outputs = new();
    private readonly List<Output> _pendingOutputs = new();

    /// <summary>Outputs that are fully described. Ones still being announced are not listed yet.</summary>
    public IReadOnlyList<Output> Outputs => _outputs;

    public event Action<Output>? OutputAdded;
    public event Action<Output>? OutputRemoved;

    /// <summary>
    /// Calls <paramref name="perOutput"/> for every current and future output. Whatever it returns is disposed
    /// when that output goes away (or when the returned subscription is disposed).
    /// </summary>
    public IDisposable ForEachOutput(Func<Output, IDisposable?> perOutput)
    {
        var created = new Dictionary<Output, IDisposable?>();
        void Add(Output output) => created[output] = perOutput(output);
        void Remove(Output output)
        {
            if (created.Remove(output, out var resource))
                resource?.Dispose();
        }
        foreach (var output in _outputs)
            Add(output);
        OutputAdded += Add;
        OutputRemoved += Remove;
        return new Disposer(() =>
        {
            OutputAdded -= Add;
            OutputRemoved -= Remove;
            foreach (var resource in created.Values)
                resource?.Dispose();
            created.Clear();
        });
    }

    /// <summary>Finds the oopshell output for a raw <c>wl_output</c>.</summary>
    public Output? FindOutput(WlOutput? proxy) => _outputs.Find(o => o.Proxy == proxy);

    private void AddOutput(GlobalInfo global)
    {
        var output = new Output(this, global);
        _pendingOutputs.Add(output);
        output.FirstDone += o =>
        {
            _pendingOutputs.Remove(o);
            _outputs.Add(o);
            OutputAdded?.Invoke(o);
        };
    }

    private void RemoveOutput(GlobalInfo global)
    {
        var output = _outputs.Find(o => o.Global == global) ?? _pendingOutputs.Find(o => o.Global == global);
        if (output is null)
            return;
        bool announced = _outputs.Remove(output);
        _pendingOutputs.Remove(output);
        foreach (var surface in _surfaces)
            surface.OnOutputRemoved(output);
        if (announced)
            OutputRemoved?.Invoke(output);
        output.Remove();
    }
}
