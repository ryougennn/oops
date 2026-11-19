using oopshell.Protocol;

namespace oopshell;

/// <summary>When a frame actually reached the screen (<c>wp_presentation</c>), or that it never did.</summary>
/// <param name="Discarded">True if the frame was replaced before being shown; the other values are then zero.</param>
/// <param name="Timestamp">CLOCK_MONOTONIC time the frame turned visible (when the clock is monotonic, which is typical).</param>
/// <param name="Refresh">Predicted time to the next refresh; zero if unknown.</param>
/// <param name="Sequence">Vertical retrace counter, if the output has one.</param>
/// <param name="Flags">How the frame was presented (vsync, zero-copy…).</param>
public readonly record struct PresentationInfo(bool Discarded, TimeSpan Timestamp, TimeSpan Refresh, ulong Sequence, WpPresentationFeedbackKind Flags);

public abstract partial class Surface
{
    private Action<PresentationInfo>? _presented;

    /// <summary>
    /// Frame timing from presentation-time, one per presented frame. Subscribing makes every commit request
    /// feedback; nothing is requested while nobody listens. Never raised if the compositor lacks wp_presentation.
    /// </summary>
    public event Action<PresentationInfo>? Presented
    {
        add => _presented += value;
        remove => _presented -= value;
    }

    private void RequestPresentationFeedback()
    {
        if (_presented is null || App.TryGet<WpPresentation>() is not { } presentation)
            return;
        var feedback = presentation.Feedback(Proxy);
        feedback.Presented += e =>
        {
            ulong seconds = ((ulong)e.TvSecHi << 32) | e.TvSecLo;
            var timestamp = TimeSpan.FromSeconds(seconds) + TimeSpan.FromTicks(e.TvNsec / 100);
            _presented?.Invoke(new PresentationInfo(false, timestamp, TimeSpan.FromTicks(e.Refresh / 100),
                ((ulong)e.SeqHi << 32) | e.SeqLo, e.Flags));
            feedback.Dispose();
        };
        feedback.Discarded += () =>
        {
            _presented?.Invoke(new PresentationInfo(true, default, default, 0, default));
            feedback.Dispose();
        };
    }
}
