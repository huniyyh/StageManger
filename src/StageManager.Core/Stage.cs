namespace StageManager.Core;

/// <summary>A group of windows that are shown together in the center of the screen.</summary>
public sealed class Stage
{
    public Guid Id { get; } = Guid.NewGuid();
    public uint ProcessId { get; }
    public string Label { get; set; }
    public List<WindowId> Windows { get; } = new();

    /// <summary>The window that receives focus when the stage is activated.</summary>
    public WindowId? Primary { get; set; }

    public Stage(uint processId, string label)
    {
        ProcessId = processId;
        Label = label;
    }

    public override string ToString() => $"{Label} [{Windows.Count}]";
}
