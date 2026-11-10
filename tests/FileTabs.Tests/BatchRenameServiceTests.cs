using System.IO;
using FileTabs;

namespace FileTabs.Tests;

public sealed class BatchRenameServiceTests : IDisposable
{
    private static readonly DateTime FixedNow = new(2026, 11, 10, 12, 0, 0);
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"filetabs-rename-{Guid.NewGuid():N}");

    public BatchRenameServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; a leftover temp directory is harmless.
        }
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, name);
        return path;
    }

    private RenamePlan Plan(IReadOnlyList<string> paths, string template,
        int start = 1, int digits = 3, bool preserveExtension = true)
        => BatchRenameService.CreatePlan(
            paths, template, start, digits, preserveExtension, FixedNow);

    [Fact]
    public void RendersVariablesAndKeepsExtension()
    {
        var a = CreateFile("a.txt");
        var b = CreateFile("b.md");

        var plan = Plan([a, b], "{原名}-{序号}-{日期}");

        Assert.False(plan.HasErrors);
        Assert.Equal(2, plan.RenameCount);
        Assert.Equal("a-001-20261110.txt", plan.Items[0].NewName);
        Assert.Equal("b-002-20261110.md", plan.Items[1].NewName);
    }

    [Fact]
    public void DoesNotAppendExtensionWhenTemplateHandlesIt()
    {
        var file = CreateFile("report.txt");

        var plan = Plan([file], "{原名}.{扩展名}");

        Assert.False(plan.HasErrors);
        Assert.Equal("report.txt", plan.Items[0].NewName);
        Assert.False(plan.Items[0].Changed);
    }

    [Fact]
    public void OriginalFileNameVariableAlreadyCarriesExtension()
    {
        var file = CreateFile("report.txt");

        var plan = Plan([file], "2026-{原文件名}");

        Assert.Equal("2026-report.txt", plan.Items[0].NewName);
    }

    [Fact]
    public void DuplicateTargetsAreRejected()
    {
        var a = CreateFile("a.txt");
        var b = CreateFile("b.txt");

        var plan = Plan([a, b], "same-name");

        Assert.Equal(2, plan.Items.Count(item => item.Error is not null));
        Assert.Contains("重复", plan.Items[0].Error);
        Assert.Contains("重复", plan.Items[1].Error);
    }

    [Fact]
    public void ExistingTargetIsRejected()
    {
        var file = CreateFile("a.txt");
        CreateFile("taken.txt");

        var plan = Plan([file], "taken");

        Assert.Equal("目标名称已存在", plan.Items[0].Error);
    }

    [Fact]
    public void CaseOnlyRenameIsAllowed()
    {
        var file = CreateFile("report.txt");

        var plan = Plan([file], "Report");

        Assert.Null(plan.Items[0].Error);
        Assert.True(plan.Items[0].Changed);
        Assert.Equal("Report.txt", plan.Items[0].NewName);
    }

    [Fact]
    public void InvalidCharactersAndTrailingDotsAreRejected()
    {
        var file = CreateFile("a.txt");

        var plan = Plan([file], "bad/name", preserveExtension: false);
        Assert.Contains("非法字符", plan.Items[0].Error);

        var plan2 = Plan([file], "{原名}.", preserveExtension: false);
        Assert.Contains("点结尾", plan2.Items[0].Error);
    }

    [Fact]
    public void MissingItemIsReported()
    {
        var missing = Path.Combine(_root, "gone.txt");

        var plan = Plan([missing], "x");

        Assert.Equal("项目已不存在", plan.Items[0].Error);
    }

    [Fact]
    public void DirectoriesUseTheirFullNameAsOriginal()
    {
        var folder = Path.Combine(_root, "docs");
        Directory.CreateDirectory(folder);

        var plan = Plan([folder], "archive-{序号}");

        Assert.Null(plan.Items[0].Error);
        Assert.Equal("archive-001", plan.Items[0].NewName);
        Assert.Equal("docs", plan.Items[0].OldName);
    }

    [Fact]
    public void EmptyTemplateRejectsEveryItem()
    {
        var file = CreateFile("a.txt");

        var plan = Plan([file], "  ");

        Assert.All(plan.Items, item => Assert.Equal("请输入命名模板", item.Error));
    }

    [Fact]
    public void ReservedDeviceNameIsRejected()
    {
        var file = CreateFile("a.txt");

        var plan = Plan([file], "CON", preserveExtension: false);

        Assert.Contains("保留名称", plan.Items[0].Error);
    }

    [Fact]
    public void ReservedDeviceNameIsRejectedWithExtension()
    {
        var file = CreateFile("a.txt");

        var plan = Plan([file], "CON.foo.bar", preserveExtension: false);

        Assert.Contains("保留名称", plan.Items[0].Error);
    }
}
