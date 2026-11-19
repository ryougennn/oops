namespace oopshell.IntegrationTests;

/// <summary>What a shell sees when the compositor goes away. Each test starts (and ends) its own sway.</summary>
public sealed class ConnectionTests
{
    [Fact]
    public void Losing_the_compositor_ends_Run_with_an_error_and_cleanup_still_works()
    {
        using var sway = new SwayFixture();
        var app = sway.Connect();
        var layer = new LayerSurface(app, app.Outputs[0], Layer.Top, "goodbye") { Anchor = Anchor.Top | Anchor.Left | Anchor.Right, Size = new(0, 30) };
        var ipc = IpcServer.Listen(app, Path.Combine(sway.RuntimeDir, "goodbye.sock"), _ => null);
        layer.Render += frame => { };
        // A graceful exit (layers are closed first). sway leaves before answering, so don't wait for a reply.
        app.SetTimer(TimeSpan.FromMilliseconds(300), null, () => System.Diagnostics.Process.Start("swaymsg", ["-s", sway.SwaySock!, "exit"])!.Dispose());
        app.SetTimer(TimeSpan.FromSeconds(10), null, () => app.Quit(99));

        int code = app.Run();

        Assert.Equal(1, code);
        Assert.IsType<oopshell.Protocol.WaylandConnectionException>(app.ConnectionError);
        // A shell's `using` blocks run after this point: none of it may throw on the dead connection.
        layer.Dispose();
        ipc.Dispose();
        app.Dispose();
    }

    [Fact]
    public void A_handled_exception_keeps_the_app_running()
    {
        using var sway = new SwayFixture();
        using var app = sway.Connect();
        var errors = new List<string>();
        app.UnhandledException += e =>
        {
            errors.Add(e.Exception.Message);
            e.Handled = true;
        };
        using var bar = new LayerSurface(app, app.Outputs[0], Layer.Top, "broken") { Anchor = Anchor.Top | Anchor.Left | Anchor.Right, Size = new(0, 20) };
        int renders = 0;
        bar.Render += frame =>
        {
            renders++;
            throw new InvalidOperationException("render");
        };
        app.SetTimer(TimeSpan.FromMilliseconds(20), null, () => throw new InvalidOperationException("timer"));
        app.RunUntil(() => errors.Contains("timer") && errors.Contains("render"), what: "both errors reported");
        Assert.True(renders >= 1);
        Assert.Null(app.ConnectionError);
    }

    [Fact]
    public void Connecting_without_a_compositor_names_the_socket_and_the_error()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"no-compositor-{Guid.NewGuid():N}");
        var e = Assert.Throws<oopshell.Protocol.WaylandConnectionException>(() => OopsApp.Connect(new OopsOptions { Display = missing }));
        Assert.Contains(missing, e.Message);
        Assert.Contains("No such file or directory", e.Message);
    }
}
