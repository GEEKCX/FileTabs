using FileTabs;

namespace FileTabs.Tests;

public sealed class ExplorerCaptureServiceTests
{
    private const string ControlPanelRoot = "::{26EE0668-A00A-44D7-9371-BEB064C98683}";

    [Fact]
    public void PowerOptionsCompositePathIsControlPanel()
    {
        // The exact path captured from a real Win11 Power Options window
        // (control panel root, category index, item CLSID).
        Assert.True(ExplorerCaptureService.IsControlPanelPath(
            $"{ControlPanelRoot}\\2\\::{{025A5937-A6BE-4686-A844-36FE4BEC8B6D}}"));
    }

    [Fact]
    public void ControlPanelRootItselfIsControlPanel()
    {
        Assert.True(ExplorerCaptureService.IsControlPanelPath(ControlPanelRoot));
    }

    [Fact]
    public void CaseInsensitiveGuidIsControlPanel()
    {
        Assert.True(ExplorerCaptureService.IsControlPanelPath(
            ControlPanelRoot.ToLowerInvariant() + @"\3\::{025A5937-A6BE-4686-A844-36FE4BEC8B6D}"));
    }

    [Fact]
    public void FilesystemFolderIsNotControlPanel()
    {
        Assert.False(ExplorerCaptureService.IsControlPanelPath(@"D:\Projects"));
    }

    [Fact]
    public void ThisPcIsNotControlPanel()
    {
        Assert.False(ExplorerCaptureService.IsControlPanelPath(
            "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"));
    }

    [Fact]
    public void NullAndEmptyAreNotControlPanel()
    {
        Assert.False(ExplorerCaptureService.IsControlPanelPath(null!));
        Assert.False(ExplorerCaptureService.IsControlPanelPath(string.Empty));
    }
}
