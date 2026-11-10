using FileTabs;

namespace FileTabs.Tests;

public sealed class SearchQueryBuilderTests
{
    [Fact]
    public void TextOnlyLeavesQueryUnchanged()
    {
        Assert.Equal("report", SearchQueryBuilder.Build("  report  ", "all", null));
    }

    [Fact]
    public void TypeFilterAppendsExtensionClause()
    {
        Assert.Equal(
            "a ext:doc;docx;xls;xlsx;ppt;pptx;pdf;txt;md;rtf;csv",
            SearchQueryBuilder.Build("a", "document", null));
    }

    [Fact]
    public void FolderFilterUsesFolderClause()
    {
        Assert.Equal("a folder:", SearchQueryBuilder.Build("a", "folder", null));
    }

    [Fact]
    public void ScopeAddsQuotedPath()
    {
        Assert.Equal(
            "a path:\"C:\\Users\\Administrator\\Desktop\"",
            SearchQueryBuilder.Build("a", "all", "C:\\Users\\Administrator\\Desktop"));
    }

    [Fact]
    public void DriveRootKeepsTrailingBackslash()
    {
        Assert.Equal("a path:\"C:\\\"", SearchQueryBuilder.Build("a", "all", "C:\\"));
    }

    [Fact]
    public void TypeAndScopeKeepTextTypeScopeOrder()
    {
        Assert.Equal(
            "a ext:jpg;jpeg;png;gif;bmp;webp;ico;tif;tiff;svg path:\"D:\\Images\"",
            SearchQueryBuilder.Build("a", "image", "D:\\Images"));
    }

    [Fact]
    public void UnknownTypeTagIsIgnored()
    {
        Assert.Equal("a", SearchQueryBuilder.Build("a", "unknown", null));
    }
}
