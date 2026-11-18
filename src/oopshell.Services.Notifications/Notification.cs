namespace oopshell.Services.Notifications;

public enum Urgency : byte
{
    Low = 0,
    Normal = 1,
    Critical = 2,
}

/// <summary>Why a notification closed (values from the spec's NotificationClosed signal).</summary>
public enum CloseReason : uint
{
    Expired = 1,
    Dismissed = 2,
    ClosedByCall = 3,
    Undefined = 4,
}

/// <summary>A button the user can press on a notification.</summary>
public readonly record struct NotificationAction(string Key, string Label);

/// <summary>Raw RGB(A) pixels from the <c>image-data</c> hint (or the deprecated <c>icon_data</c>).</summary>
public sealed record NotificationImage(int Width, int Height, int RowStride, bool HasAlpha, int BitsPerSample, int Channels, byte[] Data);

/// <summary>A notification shown by the server. Properties change when an app replaces it (<see cref="Changed"/>).</summary>
public sealed class Notification
{
    internal Notification(uint id) => Id = id;

    public uint Id { get; }
    public string AppName { get; internal set; } = "";
    /// <summary>Icon name or file URI/path, as sent by the app.</summary>
    public string AppIcon { get; internal set; } = "";
    public string Summary { get; internal set; } = "";
    /// <summary>Body text; may contain the spec's simple markup (b, i, u, a, img) when the app expects it.</summary>
    public string Body { get; internal set; } = "";
    public IReadOnlyList<NotificationAction> Actions { get; internal set; } = [];
    public Urgency Urgency { get; internal set; } = Urgency.Normal;
    public string? Category { get; internal set; }
    public string? DesktopEntry { get; internal set; }
    public NotificationImage? Image { get; internal set; }
    /// <summary>The <c>image-path</c> hint, if any.</summary>
    public string? ImagePath { get; internal set; }
    public bool Resident { get; internal set; }
    public bool Transient { get; internal set; }
    /// <summary>Requested display time: null = server default, <see cref="TimeSpan.Zero"/> = never expires.</summary>
    public TimeSpan? ExpireTimeout { get; internal set; }

    public bool IsClosed { get; internal set; }

    /// <summary>The app replaced the contents (same id).</summary>
    public event Action? Changed;

    /// <summary>The notification closed (expired, dismissed, or closed by the app).</summary>
    public event Action<CloseReason>? Closed;

    internal void RaiseChanged() => Changed?.Invoke();
    internal void RaiseClosed(CloseReason reason) => Closed?.Invoke(reason);

    public override string ToString() => $"#{Id} {AppName}: {Summary}";
}
