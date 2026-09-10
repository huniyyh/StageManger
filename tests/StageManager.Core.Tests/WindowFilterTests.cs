using StageManager.Core;

namespace StageManager.Core.Tests;

public class WindowFilterTests
{
    private static WindowProbe Probe(
        bool visible = true, bool child = false, bool tool = false, bool app = false, bool noActivate = false,
        bool cloaked = false, bool rootOwner = true, bool own = false,
        string title = "Document - Editor", string cls = "Chrome_WidgetWin_1", string process = "chrome")
        => new(visible, child, tool, app, noActivate, cloaked, rootOwner, own, title, cls, process);

    [Fact]
    public void NormalAppWindow_IsManageable() => Assert.True(WindowFilter.IsManageable(Probe()));

    [Fact]
    public void ToolWindow_IsExcluded_UnlessItAsksForTaskbarPresence()
    {
        Assert.False(WindowFilter.IsManageable(Probe(tool: true)));
        Assert.True(WindowFilter.IsManageable(Probe(tool: true, app: true)));
    }

    [Fact]
    public void NoActivateWindow_IsExcluded_UnlessItAsksForTaskbarPresence()
    {
        Assert.False(WindowFilter.IsManageable(Probe(noActivate: true)));
        Assert.True(WindowFilter.IsManageable(Probe(noActivate: true, app: true)));
    }

    [Fact]
    public void CloakedWindow_IsExcluded() => Assert.False(WindowFilter.IsManageable(Probe(cloaked: true)));

    [Fact]
    public void InvisibleOrChildWindow_IsExcluded()
    {
        Assert.False(WindowFilter.IsManageable(Probe(visible: false)));
        Assert.False(WindowFilter.IsManageable(Probe(child: true)));
    }

    [Fact]
    public void OwnedWindow_IsExcluded() => Assert.False(WindowFilter.IsManageable(Probe(rootOwner: false)));

    [Fact]
    public void UntitledWindow_IsExcluded()
    {
        Assert.False(WindowFilter.IsManageable(Probe(title: "")));
        Assert.False(WindowFilter.IsManageable(Probe(title: "   ")));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Windows.UI.Core.CoreWindow")]
    public void ShellClasses_AreExcluded(string cls) => Assert.False(WindowFilter.IsManageable(Probe(cls: cls)));

    [Theory]
    [InlineData("TextInputHost")]
    [InlineData("StartMenuExperienceHost")]
    [InlineData("searchhost")]
    public void ShellProcesses_AreExcluded(string process) => Assert.False(WindowFilter.IsManageable(Probe(process: process)));

    [Fact]
    public void OwnProcess_IsExcluded() => Assert.False(WindowFilter.IsManageable(Probe(own: true)));
}
