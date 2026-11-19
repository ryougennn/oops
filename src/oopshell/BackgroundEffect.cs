using System.Drawing;
using oopshell.Protocol;

namespace oopshell;

public abstract partial class Surface
{
    private Region? _blurRegion;
    private bool _blurDirty;
    private ExtBackgroundEffectSurfaceV1? _backgroundEffect;

    /// <summary>
    /// Where the compositor should blur what is behind this surface (ext-background-effect), in logical
    /// coordinates; <see cref="Region.All"/> for the whole surface, null for none. Draw translucent content
    /// over it. Ignored where the compositor cannot blur (<see cref="OopsApp.CanBlurBehind"/>).
    /// </summary>
    public Region? BlurRegion
    {
        get => _blurRegion;
        set
        {
            _blurRegion = value;
            _blurDirty = true;
            Invalidate(Rectangle.Empty);
        }
    }

    /// <summary>Sends a changed blur region with the next commit.</summary>
    private void ApplyBlurRegion()
    {
        if (!_blurDirty)
            return;
        _blurDirty = false;
        if (App.BackgroundEffects is not { } manager)
            return;
        if (_backgroundEffect is null)
        {
            if (_blurRegion is null)
                return;
            _backgroundEffect = manager.GetBackgroundEffect(Proxy);
        }
        using var region = _blurRegion?.Create(App.Compositor);
        _backgroundEffect.SetBlurRegion(region);
    }
}

public sealed partial class OopsApp
{
    /// <summary>The ext-background-effect manager, bound at startup when advertised.</summary>
    internal ExtBackgroundEffectManagerV1? BackgroundEffects { get; private set; }

    /// <summary>The compositor can blur behind surfaces (<see cref="Surface.BlurRegion"/>). It may change at runtime.</summary>
    public bool CanBlurBehind { get; private set; }

    /// <summary><see cref="CanBlurBehind"/> changed (e.g. blur was turned off in the compositor's settings).</summary>
    public event Action? CanBlurBehindChanged;

    private void BindBackgroundEffects()
    {
        if (TryGet<ExtBackgroundEffectManagerV1>() is not { } manager)
            return;
        BackgroundEffects = manager;
        manager.Capabilities += e =>
        {
            bool can = (e.Flags & ExtBackgroundEffectManagerV1Capability.Blur) != 0;
            if (can == CanBlurBehind)
                return;
            CanBlurBehind = can;
            CanBlurBehindChanged?.Invoke();
        };
    }
}
