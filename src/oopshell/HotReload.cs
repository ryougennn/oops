using System.Diagnostics;
using System.Drawing;

[assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(oopshell.HotReload.Handler))]

namespace oopshell.HotReload
{
    /// <summary>
    /// Called by .NET Hot Reload (<c>dotnet watch</c>) after code changes are applied, possibly on another thread.
    /// Inert under NativeAOT, where hot reload does not exist.
    /// </summary>
    internal static class Handler
    {
        public static void ClearCache(Type[]? updatedTypes)
        {
            if (OopsApp.Current is { } app)
                app.Post(() => app.OnHotReloadCacheClear(updatedTypes));
        }

        public static void UpdateApplication(Type[]? updatedTypes)
        {
            if (OopsApp.Current is { } app)
                app.Post(() => app.OnHotReloaded(updatedTypes));
        }
    }
}

namespace oopshell
{
    public sealed partial class OopsApp
    {
        static readonly TimeSpan ReloadFlashDuration = TimeSpan.FromMilliseconds(600);
        long reloadFlashUntil;

        /// <summary>
        /// Raised on the UI thread after a hot reload applied code changes (with the updated types, if known).
        /// Rebuild cached state here (menus built from data, layout caches…). Every surface is invalidated
        /// right afterwards, so edited drawing code shows up immediately.
        /// </summary>
        public event Action<Type[]?>? HotReloaded;

        /// <summary>Raised on the UI thread when hot reload asks caches to be cleared (before <see cref="HotReloaded"/>).</summary>
        public event Action<Type[]?>? HotReloadCacheClear;

        internal void OnHotReloadCacheClear(Type[]? types) => HotReloadCacheClear?.Invoke(types);

        internal void OnHotReloaded(Type[]? types)
        {
            HotReloaded?.Invoke(types);
            if (Options.DevOverlay)
            {
                reloadFlashUntil = Stopwatch.GetTimestamp() + (long)(ReloadFlashDuration.TotalSeconds * Stopwatch.Frequency);
                // Redraw once more when the flash ends, so the marker disappears.
                SetTimer(ReloadFlashDuration, null, InvalidateAll);
            }
            InvalidateAll();
        }

        void InvalidateAll()
        {
            foreach (var surface in surfaces)
                surface.Invalidate();
        }

        /// <summary>The corner marker to draw this frame (buffer pixels), or empty when no reload flash is active.</summary>
        internal Rectangle ReloadFlashRect(Size bufferSize, double scale)
        {
            if (!Options.DevOverlay || Stopwatch.GetTimestamp() > reloadFlashUntil)
                return Rectangle.Empty;
            int size = (int)Math.Ceiling(10 * scale);
            return new Rectangle(bufferSize.Width - size - 2, 2, size, size);
        }
    }
}
