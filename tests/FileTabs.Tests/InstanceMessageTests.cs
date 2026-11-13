using FileTabs;

namespace FileTabs.Tests;

public sealed class InstanceMessageTests
{
    [Fact]
    public void PlainPathMeansNewTab()
    {
        Assert.True(InstanceMessage.TryParse(@"C:\work", out var path, out var inPlace));
        Assert.Equal(@"C:\work", path);
        Assert.False(inPlace);
    }

    [Fact]
    public void NavigatePrefixMeansInPlace()
    {
        var message = InstanceMessage.ForInPlaceNavigation(@"C:\work\project");
        Assert.True(InstanceMessage.TryParse(message, out var path, out var inPlace));
        Assert.Equal(@"C:\work\project", path);
        Assert.True(inPlace);
    }

    [Fact]
    public void EmptyOrNullLinesAreRejected()
    {
        Assert.False(InstanceMessage.TryParse(null, out _, out _));
        Assert.False(InstanceMessage.TryParse("", out _, out _));
        Assert.False(InstanceMessage.TryParse("nav|", out _, out _));
    }

    [Fact]
    public void PrefixInsideThePathIsNotMistakenForIt()
    {
        var message = InstanceMessage.ForNewTab(@"C:\a\nav|b");
        Assert.True(InstanceMessage.TryParse(message, out var path, out var inPlace));
        Assert.Equal(@"C:\a\nav|b", path);
        Assert.False(inPlace);
    }
}
