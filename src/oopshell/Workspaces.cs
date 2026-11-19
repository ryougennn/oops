using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using oopshell.Protocol;

namespace oopshell;

/// <summary>A compositor workspace, tag or virtual desktop (ext-workspace-v1).</summary>
/// <remarks>
/// State changes arrive in batches; <see cref="Changed"/> is raised after each. Requests such as
/// <see cref="Activate"/> take effect when the compositor applies them, and the new state arrives as a change.
/// </remarks>
public sealed class Workspace
{
    private readonly OopsApp _app;
    internal readonly ExtWorkspaceHandleV1 _handle;
    internal WorkspaceGroup? _group;

    internal Workspace(OopsApp app, ExtWorkspaceHandleV1 handle)
    {
        _app = app;
        _handle = handle;
        handle.IdEvent += e => Id = e.Id;
        handle.Name += e => Name = e.Name;
        handle.Coordinates += e => Coordinates = MemoryMarshal.Cast<byte, uint>(e.Coordinates).ToArray();
        handle.State += e => State = (uint)e.State;
        handle.Capabilities += e => Abilities = (uint)e.Capabilities;
    }

    /// <summary>An id that is stable across sessions, or "" if the compositor has none.</summary>
    public string Id { get; private set; } = "";
    /// <summary>The name to show ("1", "web"…).</summary>
    public string Name { get; private set; } = "";
    /// <summary>The position in the compositor's layout: one number for a row, two for a grid, empty if unordered.</summary>
    public IReadOnlyList<uint> Coordinates { get; private set; } = [];
    internal uint State { get; private set; }
    internal uint Abilities { get; private set; }

    /// <summary>True if the workspace is currently shown.</summary>
    public bool IsActive => (State & (uint)ExtWorkspaceHandleV1State.Active) != 0;
    /// <summary>True if something on the workspace wants attention.</summary>
    public bool IsUrgent => (State & (uint)ExtWorkspaceHandleV1State.Urgent) != 0;
    /// <summary>True if the compositor suggests not showing the workspace, as for a scratchpad.</summary>
    public bool IsHidden => (State & (uint)ExtWorkspaceHandleV1State.Hidden) != 0;

    /// <summary>The outputs the workspace's group spans, usually one.</summary>
    public IReadOnlyList<Output> Outputs => _group?.Outputs ?? [];

    public bool CanActivate => (Abilities & (uint)ExtWorkspaceHandleV1WorkspaceCapabilities.Activate) != 0;
    public bool CanDeactivate => (Abilities & (uint)ExtWorkspaceHandleV1WorkspaceCapabilities.Deactivate) != 0;
    public bool CanRemove => (Abilities & (uint)ExtWorkspaceHandleV1WorkspaceCapabilities.Remove) != 0;

    /// <summary>Raised when the name, state, position or outputs change.</summary>
    public event Action? Changed;

    internal void RaiseChanged() => Changed?.Invoke();

    /// <summary>Shows this workspace.</summary>
    public void Activate()
    {
        _handle.Activate();
        _app.CommitWorkspaces();
    }

    /// <summary>Hides this workspace, where the compositor allows several active workspaces (tags).</summary>
    public void Deactivate()
    {
        _handle.Deactivate();
        _app.CommitWorkspaces();
    }

    /// <summary>Asks the compositor to remove this workspace.</summary>
    public void Remove()
    {
        _handle.Remove();
        _app.CommitWorkspaces();
    }

    public override string ToString() => $"{Name}{(IsActive ? " (active)" : "")}";
}

/// <summary>An ext-workspace group: workspaces that share outputs. Exposed only through <see cref="Workspace.Outputs"/>.</summary>
internal sealed class WorkspaceGroup
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
            if (app.FindOutput(e.Output) is { } output && !Outputs.Contains(output))
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
    private ExtWorkspaceManagerV1? _workspaceManager;
    private ObservableCollection<Workspace>? _workspaces;
    private ReadOnlyObservableCollection<Workspace>? _workspacesView;
    private readonly List<WorkspaceGroup> _workspaceGroups = new();
    private readonly List<Workspace> _pendingWorkspaces = new();
    private bool _workspacesBound;

    /// <summary>The compositor's workspaces in layout order, or null without ext-workspace-v1.</summary>
    /// <remarks>Bound on first access; the collection fills asynchronously.</remarks>
    public ReadOnlyObservableCollection<Workspace>? Workspaces
    {
        get
        {
            if (!_workspacesBound)
            {
                _workspacesBound = true;
                BindWorkspaces();
            }
            return _workspacesView;
        }
    }

    /// <summary>Raised after each batch of workspace additions, removals or changes.</summary>
    public event Action? WorkspacesChanged;

    private void BindWorkspaces()
    {
        if (TryGet<ExtWorkspaceManagerV1>() is not { } manager)
            return;
        _workspaceManager = manager;
        _workspaces = new ObservableCollection<Workspace>();
        _workspacesView = new ReadOnlyObservableCollection<Workspace>(_workspaces);
        manager.WorkspaceGroup += e =>
        {
            var group = new WorkspaceGroup(this, e.WorkspaceGroup);
            _workspaceGroups.Add(group);
            e.WorkspaceGroup.WorkspaceEnter += w =>
            {
                if (FindWorkspace(w.Workspace) is { } workspace)
                    workspace._group = group;
            };
            e.WorkspaceGroup.WorkspaceLeave += w =>
            {
                if (FindWorkspace(w.Workspace) is { } workspace && workspace._group == group)
                    workspace._group = null;
            };
            e.WorkspaceGroup.Removed += () =>
            {
                _workspaceGroups.Remove(group);
                group.Handle.Dispose();
            };
        };
        manager.Workspace += e =>
        {
            var workspace = new Workspace(this, e.Workspace);
            _pendingWorkspaces.Add(workspace);
            e.Workspace.Removed += () =>
            {
                _pendingWorkspaces.Remove(workspace);
                _workspaces.Remove(workspace);
                workspace._handle.Dispose();
            };
        };
        // Events up to done form one atomic batch.
        manager.Done += () =>
        {
            foreach (var workspace in _pendingWorkspaces)
                _workspaces.Add(workspace);
            _pendingWorkspaces.Clear();
            var ordered = _workspaces.OrderBy(w => w.Outputs.FirstOrDefault()?.Name ?? "", StringComparer.Ordinal)
                .ThenBy(w => w.Coordinates.Count > 0 ? w.Coordinates[0] : uint.MaxValue)
                .ThenBy(w => w.Coordinates.Count > 1 ? w.Coordinates[1] : 0).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int at = _workspaces.IndexOf(ordered[i]);
                if (at != i)
                    _workspaces.Move(at, i);
            }
            foreach (var workspace in _workspaces)
                workspace.RaiseChanged();
            WorkspacesChanged?.Invoke();
        };
    }

    private Workspace? FindWorkspace(ExtWorkspaceHandleV1 handle) =>
        _workspaces?.FirstOrDefault(w => w._handle == handle) ?? _pendingWorkspaces.FirstOrDefault(w => w._handle == handle);

    internal void CommitWorkspaces() => _workspaceManager?.Commit();

    /// <summary>Asks the compositor for a new workspace named <paramref name="name"/>.</summary>
    /// <param name="name">The workspace name.</param>
    /// <param name="output">The output to create it on, or null for the first group that allows creation.</param>
    /// <returns>False if the compositor does not let clients create workspaces there.</returns>
    public bool CreateWorkspace(string name, Output? output = null)
    {
        _ = Workspaces;
        var group = _workspaceGroups.FirstOrDefault(g => g.CanCreate && (output is null || g.Outputs.Contains(output)));
        if (group is null)
            return false;
        group.Handle.CreateWorkspace(name);
        CommitWorkspaces();
        return true;
    }
}
