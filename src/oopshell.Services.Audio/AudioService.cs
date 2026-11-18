using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace oopshell.Services.Audio;

/// <summary>An output device, as last reported. A new snapshot replaces it on every change.</summary>
/// <param name="Name">The server's name for it, e.g. <c>alsa_output.pci-0000_00_1f.3.analog-stereo</c>.</param>
/// <param name="Description">The name for people, e.g. "Built-in Audio Analog Stereo".</param>
/// <param name="Volume">Average channel volume: 1 is 100 %, above 1 is amplified.</param>
/// <param name="Muted">Muted.</param>
/// <param name="Channels">Number of channels (setting the volume sets them all).</param>
public sealed record AudioSink(string Name, string Description, double Volume, bool Muted, int Channels);

/// <summary>
/// The output volume through the PulseAudio API (which PipeWire also serves): the default sink and the
/// others, live, with volume and mute control.
/// <code>
/// using var audio = await AudioService.StartAsync(app);
/// audio.Changed += () => Redraw(audio.DefaultSink);
/// audio.SetVolume(audio.DefaultSink!, 0.5);
/// </code>
/// libpulse runs its own thread; every change is applied and reported on the UI thread.
/// </summary>
public sealed unsafe partial class AudioService : IDisposable
{
    const string Lib = "libpulse.so.0";
    const int PA_CONTEXT_READY = 4, PA_CONTEXT_FAILED = 5, PA_CONTEXT_TERMINATED = 6;
    const int PA_SUBSCRIPTION_MASK_SINK = 1, PA_SUBSCRIPTION_MASK_SERVER = 128;
    const uint PA_VOLUME_NORM = 0x10000;
    // Field offsets on x86_64/aarch64 (LP64), checked against <pulse/introspect.h> with offsetof.
    const int SinkName = 0, SinkDescription = 16, SinkVolume = 172, SinkMute = 304, ServerDefaultSink = 48;

    [LibraryImport(Lib)] private static partial nint pa_threaded_mainloop_new();
    [LibraryImport(Lib)] private static partial nint pa_threaded_mainloop_get_api(nint m);
    [LibraryImport(Lib)] private static partial int pa_threaded_mainloop_start(nint m);
    [LibraryImport(Lib)] private static partial void pa_threaded_mainloop_stop(nint m);
    [LibraryImport(Lib)] private static partial void pa_threaded_mainloop_free(nint m);
    [LibraryImport(Lib)] private static partial void pa_threaded_mainloop_lock(nint m);
    [LibraryImport(Lib)] private static partial void pa_threaded_mainloop_unlock(nint m);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial nint pa_context_new(nint api, string name);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial int pa_context_connect(nint c, string? server, int flags, nint api);
    [LibraryImport(Lib)] private static partial void pa_context_disconnect(nint c);
    [LibraryImport(Lib)] private static partial void pa_context_unref(nint c);
    [LibraryImport(Lib)] private static partial int pa_context_get_state(nint c);
    [LibraryImport(Lib)] private static partial void pa_context_set_state_callback(nint c, delegate* unmanaged[Cdecl]<nint, nint, void> cb, nint userdata);
    [LibraryImport(Lib)] private static partial void pa_context_set_subscribe_callback(nint c, delegate* unmanaged[Cdecl]<nint, int, uint, nint, void> cb, nint userdata);
    [LibraryImport(Lib)] private static partial nint pa_context_subscribe(nint c, int mask, nint cb, nint userdata);
    [LibraryImport(Lib)] private static partial nint pa_context_get_server_info(nint c, delegate* unmanaged[Cdecl]<nint, byte*, nint, void> cb, nint userdata);
    [LibraryImport(Lib)] private static partial nint pa_context_get_sink_info_list(nint c, delegate* unmanaged[Cdecl]<nint, byte*, int, nint, void> cb, nint userdata);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial nint pa_context_set_sink_volume_by_name(nint c, string name, byte* volume, nint cb, nint userdata);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial nint pa_context_set_sink_mute_by_name(nint c, string name, int mute, nint cb, nint userdata);
    [LibraryImport(Lib)] private static partial byte* pa_cvolume_set(byte* volume, uint channels, uint v);
    [LibraryImport(Lib)] private static partial void pa_operation_unref(nint o);

    readonly OopsApp app;
    readonly nint mainloop;
    readonly nint context;
    GCHandle self;
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Filled on the libpulse thread while a sink list is being reported, handed to the UI thread at the end.
    List<AudioSink> incoming = new();
    string? defaultName;
    bool disposed;

    AudioService(OopsApp app, string? server)
    {
        this.app = app;
        self = GCHandle.Alloc(this);
        mainloop = pa_threaded_mainloop_new();
        if (mainloop == 0)
            throw new InvalidOperationException("libpulse could not create a main loop.");
        context = pa_context_new(pa_threaded_mainloop_get_api(mainloop), "oopshell");
        nint userdata = GCHandle.ToIntPtr(self);
        pa_context_set_state_callback(context, &OnState, userdata);
        pa_context_set_subscribe_callback(context, &OnEvent, userdata);
        if (pa_context_connect(context, server, 0, 0) < 0 || pa_threaded_mainloop_start(mainloop) < 0)
            ready.TrySetException(new InvalidOperationException("Could not connect to the sound server."));
    }

    /// <summary>Every output device.</summary>
    public IReadOnlyList<AudioSink> Sinks { get; private set; } = [];

    /// <summary>The output new sound goes to, if known.</summary>
    public AudioSink? DefaultSink { get; private set; }

    /// <summary>Volume, mute or the default output changed (on the UI thread).</summary>
    public event Action? Changed;

    /// <summary>Sets all channels of <paramref name="sink"/> to <paramref name="volume"/> (clamped to 0..1.5).</summary>
    public void SetVolume(AudioSink sink, double volume)
    {
        uint raw = (uint)(Math.Clamp(volume, 0, 1.5) * PA_VOLUME_NORM);
        var cvolume = stackalloc byte[132]; // pa_cvolume: channels, then 32 values
        pa_cvolume_set(cvolume, (uint)Math.Max(1, sink.Channels), raw);
        Locked(() => pa_context_set_sink_volume_by_name(context, sink.Name, cvolume, 0, 0));
    }

    public void SetMuted(AudioSink sink, bool muted) => Locked(() => pa_context_set_sink_mute_by_name(context, sink.Name, muted ? 1 : 0, 0, 0));

    delegate nint Operation();

    void Locked(Operation operation)
    {
        if (disposed)
            return;
        pa_threaded_mainloop_lock(mainloop);
        try
        {
            if (operation() is var op and not 0)
                pa_operation_unref(op);
        }
        finally
        {
            pa_threaded_mainloop_unlock(mainloop);
        }
    }

    static AudioService? From(nint userdata) => GCHandle.FromIntPtr(userdata).Target as AudioService;

    /// <summary>Asks for the default sink and the sink list (libpulse thread, main loop locked).</summary>
    void Refresh()
    {
        nint userdata = GCHandle.ToIntPtr(self);
        if (pa_context_get_server_info(context, &OnServerInfo, userdata) is var a and not 0)
            pa_operation_unref(a);
        if (pa_context_get_sink_info_list(context, &OnSink, userdata) is var b and not 0)
            pa_operation_unref(b);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnState(nint context, nint userdata)
    {
        if (From(userdata) is not { } service)
            return;
        switch (pa_context_get_state(context))
        {
            case PA_CONTEXT_READY:
                if (pa_context_subscribe(context, PA_SUBSCRIPTION_MASK_SINK | PA_SUBSCRIPTION_MASK_SERVER, 0, 0) is var op and not 0)
                    pa_operation_unref(op);
                service.Refresh();
                break;
            case PA_CONTEXT_FAILED or PA_CONTEXT_TERMINATED:
                service.ready.TrySetException(new InvalidOperationException("Could not connect to the sound server."));
                break;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnEvent(nint context, int type, uint index, nint userdata) => From(userdata)?.Refresh();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnServerInfo(nint context, byte* info, nint userdata)
    {
        if (From(userdata) is not { } service || info == null)
            return;
        string? name = Marshal.PtrToStringUTF8(*(nint*)(info + ServerDefaultSink));
        service.app.Post(() =>
        {
            service.defaultName = name;
            service.Publish();
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnSink(nint context, byte* info, int eol, nint userdata)
    {
        if (From(userdata) is not { } service)
            return;
        if (eol == 0 && info != null)
        {
            byte channels = info[SinkVolume];
            var values = (uint*)(info + SinkVolume + 4);
            double sum = 0;
            for (int i = 0; i < channels; i++)
                sum += values[i];
            service.incoming.Add(new AudioSink(
                Marshal.PtrToStringUTF8(*(nint*)(info + SinkName)) ?? "",
                Marshal.PtrToStringUTF8(*(nint*)(info + SinkDescription)) ?? "",
                channels == 0 ? 0 : sum / channels / PA_VOLUME_NORM,
                *(int*)(info + SinkMute) != 0,
                channels));
            return;
        }
        var sinks = service.incoming;
        service.incoming = new List<AudioSink>();
        service.app.Post(() =>
        {
            service.Sinks = sinks;
            service.Publish();
            service.ready.TrySetResult();
        });
    }

    void Publish()
    {
        DefaultSink = Sinks.FirstOrDefault(s => s.Name == defaultName) ?? Sinks.FirstOrDefault();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (mainloop != 0)
        {
            pa_threaded_mainloop_lock(mainloop);
            pa_context_disconnect(context);
            pa_context_unref(context);
            pa_threaded_mainloop_unlock(mainloop);
            pa_threaded_mainloop_stop(mainloop);
            pa_threaded_mainloop_free(mainloop);
        }
        self.Free();
    }
}

// Not in the unsafe part: async methods cannot be compiled in an unsafe context.
public sealed partial class AudioService
{
    /// <summary>
    /// Connects to the sound server (<paramref name="server"/> null: the default, as <c>pactl</c> would) and
    /// reads the sinks. Throws <see cref="InvalidOperationException"/> if there is none or libpulse is missing.
    /// </summary>
    public static async Task<AudioService> StartAsync(OopsApp app, string? server = null)
    {
        AudioService service;
        try
        {
            service = new AudioService(app, server);
        }
        catch (DllNotFoundException e)
        {
            throw new InvalidOperationException("libpulse is not installed.", e);
        }
        try
        {
            await service.ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e)
        {
            service.Dispose();
            throw e as InvalidOperationException ?? new InvalidOperationException("The sound server did not answer.", e);
        }
        return service;
    }
}
