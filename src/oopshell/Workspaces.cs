using System.Collections.ObjectModel;
using oopshell.Protocol;

namespace oopshell;

/// <summary>
/// A workspace (or tag, or virtual desktop) of the compositor, from ext-workspace-v1. Its state changes in
/// batches; <see cref="Changed"/> fires after each. Requests (<see cref="Activate"/>…) take effect when the
/// compositor applies them; the new state arrives as a change.
/// </summary>
public sealed class Workspace
{
    readonly OopsApp app;
    internal readonly ExtWorkspaceHandleV1 Handle;
    internal WorkspaceGroup? Group;

    internal Workspace(OopsApp app, ExtWorkspaceHandleV1 handle)
    {
        this.app = app;
        Handle = handle;
        handle.IdEvent += e => Id = e.Id;
        handle.Name += e => Name = e.Name;
        handle.Coordinates += e => Coordinates = ReadCoordinates(e.Coordinates);
        handle.State += e => State = (uint)e.State;
        handle.Capabilities += e => Abilities = (uint)e.Capabilities;
    }

    static uint[] ReadCoordinates(ReadOnlySpan<byte> bytes) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes).ToArray();

    /// <summary>A stable id across sessions, if the compositor has one ("" otherwise).</summary>
    public string Id { get; private set; } = "";
    /// <summary>The name to show ("1", "web"…).</summary>
    public string Name { get; private set; } = "";
    /// <summary>Its position in the compositor's layout (one number for a row, two for a grid; empty if unordered).</summary>
    public IReadOnlyList<uint> Coordinates { get; private set; } = [];
    internal uint State { get; private set; }
    internal uint Abilities { get; private set; }

    /// <summary>Shown on its output(s) now.</summary>
    public bool IsActive => (State & (uint)ExtWorkspaceHandleV1State.Active) != 0;
    /// <summary>Something on it wants attention.</summary>
    public bool IsUrgent => (State & (uint)ExtWorkspaceHandleV1State.Urgent) != 0;
    /// <summary>The compositor suggests not showing it (e.g. a scratchpad).</summary>
    public bool IsHidden => (State & 4) != 0;

    /// <summary>The outputs its group spans (usually one).</summary>
    public IReadOnlyList<Output> Outputs => Group?.Outputs ?? [];

    public bool CanActivate => (Abilities & (uint)ExtWorkspaceHandleV1WorkspaceCapabilities.Activate) != 0;
    public bool CanDeactivate => (Abilities & (uint)ExtWorkspaceHandleV1WorkspaceCapabilities.Deactivate) != 0;
    public bool CanRemove => (Abilities & (uint)ExtWorkspaceHandleV1WorkspaceCapabilities.Remove) != 0;

    /// <summary>The workspace changed (name, state, position or outputs).</summary>
    public event Action? Changed;

    internal void RaiseChanged() => Changed?.Invoke();

    /// <summary>Shows this workspace.</summary>
    public void Activate()
    {
        Handle.Activate();
        app.CommitWorkspaces();
    }

    /// <summary>Hides it, where the compositor allows several active workspaces (tags).</summary>
    public void Deactivate()
    {
        Handle.Deactivate();
        app.CommitWorkspaces();
    }

    /// <summary>Asks the compositor to remove it.</summary>
    public void Remove()
    {
        Handle.Remove();
        app.CommitWorkspaces();
    }

    public override string ToString() => $"{Name}{(IsActive ? " (active)" : "")}";
}

/// <summary>Workspaces that share outputs (ext-workspace's group). Internal: exposed as <see cref="Workspace.Outputs"/>.</summary>
sealed class WorkspaceGroup
{
    public readonly ExtWorkspaceGroupHandleV1 Handle;
    public readonly List<Output> Outputs = new();
    public bool CanCreate;

    public WorkspaceGroup(OopsApp app, ExtWorkspaceGroupHandleV1 handle)
    {
        Handle = handle;
        handle.Capabilities += e => CanCreate = ((uint)e.Capabilities & (uint)ExtWorkspaceGroupHandleV1GroupCapabilities.CreateWorkspace) != 0;
        handle.OutputEnter += e =>
        {
            var proxy = e.Output;
            if (app.Outputs.FirstOrDefault(o => o.Proxy == proxy) is { } output && !Outputs.Contains(output))
                Outputs.Add(output);
        };
        handle.OutputLeave += e =>
        {
            var proxy = e.Output;
            Outputs.RemoveAll(o => o.Proxy == proxy);
        };
    }
}

public sealed partial class OopsApp
{
    ExtWorkspaceManagerV1? workspaceManager;
    ObservableCollection<Workspace>? workspaces;
    ReadOnlyObservableCollection<Workspace>? workspacesView;
    readonly List<WorkspaceGroup> workspaceGroups = new();
    readonly List<Workspace> pendingWorkspaces = new();
    bool workspacesBound;

    /// <summary>
    /// The compositor's workspaces in layout order, or null if it offers no ext-workspace-v1 (sway, for one,
    /// does not; niri does). Fills asynchronously once accessed for the first time.
    /// </summary>
    public ReadOnlyObservableCollection<Workspace>? Workspaces
    {
        get
        {
            if (!workspacesBound)
            {
                workspacesBound = true;
                BindWorkspaces();
            }
            return workspacesView;
        }
    }

    /// <summary>Workspaces were added, removed or changed (after each batch of changes from the compositor).</summary>
    public event Action? WorkspacesChanged;

    void BindWorkspaces()
    {
        if (TryGet<ExtWorkspaceManagerV1>() is not { } manager)
            return;
        workspaceManager = manager;
        workspaces = new ObservableCollection<Workspace>();
        workspacesView = new ReadOnlyObservableCollection<Workspace>(workspaces);
        manager.WorkspaceGroup += e =>
        {
            var group = new WorkspaceGroup(this, e.WorkspaceGroup);
            workspaceGroups.Add(group);
            e.WorkspaceGroup.WorkspaceEnter += w =>
            {
                if (FindWorkspace(w.Workspace) is { } workspace)
                    workspace.Group = group;
            };
            e.WorkspaceGroup.WorkspaceLeave += w =>
            {
                if (FindWorkspace(w.Workspace) is { } workspace && workspace.Group == group)
                    workspace.Group = null;
            };
            e.WorkspaceGroup.Removed += () =>
            {
                workspaceGroups.Remove(group);
                group.Handle.Dispose();
            };
        };
        manager.Workspace += e =>
        {
            var workspace = new Workspace(this, e.Workspace);
            pendingWorkspaces.Add(workspace);
            e.Workspace.Removed += () =>
            {
                pendingWorkspaces.Remove(workspace);
                workspaces.Remove(workspace);
                workspace.Handle.Dispose();
            };
        };
        // Everything above is one atomic batch, ended by done.
        manager.Done += () =>
        {
            foreach (var workspace in pendingWorkspaces)
                workspaces.Add(workspace);
            pendingWorkspaces.Clear();
            var ordered = workspaces.OrderBy(w => w.Outputs.FirstOrDefault()?.Name ?? "", StringComparer.Ordinal)
                .ThenBy(w => w.Coordinates.Count > 0 ? w.Coordinates[0] : uint.MaxValue)
                .ThenBy(w => w.Coordinates.Count > 1 ? w.Coordinates[1] : 0).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int at = workspaces.IndexOf(ordered[i]);
                if (at != i)
                    workspaces.Move(at, i);
            }
            foreach (var workspace in workspaces)
                workspace.RaiseChanged();
            WorkspacesChanged?.Invoke();
        };
    }

    Workspace? FindWorkspace(ExtWorkspaceHandleV1 handle) =>
        workspaces?.FirstOrDefault(w => w.Handle == handle) ?? pendingWorkspaces.FirstOrDefault(w => w.Handle == handle);

    internal void CommitWorkspaces() => workspaceManager?.Commit();

    /// <summary>
    /// Asks the compositor for a new workspace named <paramref name="name"/> on <paramref name="output"/> (default:
    /// the first output that allows it). False if the compositor does not let clients create workspaces there.
    /// </summary>
    public bool CreateWorkspace(string name, Output? output = null)
    {
        _ = Workspaces;
        var group = workspaceGroups.FirstOrDefault(g => g.CanCreate && (output is null || g.Outputs.Contains(output)));
        if (group is null)
            return false;
        group.Handle.CreateWorkspace(name);
        CommitWorkspaces();
        return true;
    }
}
