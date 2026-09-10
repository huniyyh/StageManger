namespace StageManager.Core;

/// <summary>A group of windows that are shown together in the center of the screen.</summary>
public sealed class Stage
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The process the stage was created for; used for its label.</summary>
    public uint ProcessId { get; }

    /// <summary>Every process with a window in the stage. New windows of any of them join this stage.</summary>
    public HashSet<uint> ProcessIds { get; } = new();

    public string Label { get; set; }
    public List<WindowId> Windows { get; } = new();

    /// <summary>The window that receives focus when the stage is activated.</summary>
    public WindowId? Primary { get; set; }

    public Stage(uint processId, string label)
    {
        ProcessId = processId;
        ProcessIds.Add(processId);
        Label = label;
    }

    public override string ToString() => $"{Label} [{Windows.Count}]";
}
