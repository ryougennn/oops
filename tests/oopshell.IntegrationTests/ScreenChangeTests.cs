namespace oopshell.IntegrationTests;

/// <summary>
/// Outputs going away, down to none, alone and while locked: where shells most often crash (a laptop lid,
/// a dock, a monitor that sleeps). Each test starts its own sway, since it removes every output.
/// </summary>
public sealed class ScreenChangeTests
{
    [Fact]
    public void Shell_survives_losing_every_output_and_comes_back()
    {
        using var sway = new SwayFixture();
        using var app = sway.Connect();
        var bars = new Dictionary<string, LayerSurface>();
        using var perOutput = app.ForEachOutput(output =>
        {
            var bar = new LayerSurface(app, output, Layer.Top, "bar") { Anchor = Anchor.Top | Anchor.Left | Anchor.Right, Size = new(0, 20) };
            Draw.SolidColor(bar, 0xFF203040);
            bars[output.Name] = bar;
            return new Disposer(() =>
            {
                bars.Remove(output.Name);
                bar.Dispose();
            });
        });
        app.RunUntil(() => bars.Count == 1 && bars.Values.Single().IsConfigured, what: "bar");

        sway.Swaymsg("output", "HEADLESS-1", "unplug");
        app.RunUntil(() => app.Outputs.Count == 0 && bars.Count == 0, what: "no outputs");
        app.RunFor(200); // nothing renders, nothing throws

        sway.Swaymsg("create_output");
        app.RunUntil(() => bars.Count == 1 && bars.Values.Single().IsConfigured, what: "bar on the new output");
        Assert.Equal(app.Outputs.Single().LogicalSize.Width, bars.Values.Single().LogicalSize.Width);
        Assert.Null(app.ConnectionError);
    }

    [Fact]
    public void A_lock_follows_outputs_leaving_and_arriving_and_stays_locked()
    {
        using var sway = new SwayFixture();
        using var app = sway.Connect();
        var surfaces = new List<LockSurface>();
        var lockTask = app.LockAsync(surface =>
        {
            surfaces.Add(surface);
            Draw.SolidColor(surface, 0xFF000080);
        });
        var session = app.Complete(lockTask);
        string first = surfaces[0].Output.Name;

        // Unplug the only output while locked: its surface goes, the session stays locked.
        sway.Swaymsg("output", first, "unplug");
        app.RunUntil(() => app.Outputs.Count == 0, what: "no outputs");
        app.RunUntil(() => session.Surfaces.Count == 0, what: "lock surface removed");
        Assert.True(session.IsLocked);

        // A new output while locked gets a lock surface of its own.
        sway.Swaymsg("create_output");
        app.RunUntil(() => session.Surfaces.Count == 1 && session.Surfaces.Single().IsConfigured, what: "lock surface on the new output");
        Assert.NotEqual(first, session.Surfaces.Single().Output.Name);
        Assert.True(session.IsLocked);

        session.Unlock();
        app.Display.Roundtrip();
        Assert.False(session.IsLocked);
        Assert.Null(app.ConnectionError);
    }
}
