namespace FileTabs;

/// <summary>
/// 把搜索框文字、类型筛选和范围拼成 Everything 查询。
/// 不依赖 WPF 控件，筛选组合可以直接用单测覆盖。
/// </summary>
public static class SearchQueryBuilder
{
    public static string Build(string text, string typeTag, string? scopePath)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());

        if (TypeClause(typeTag) is { } typeClause)
        {
            parts.Add(typeClause);
        }

        if (!string.IsNullOrWhiteSpace(scopePath))
        {
            parts.Add($"path:\"{scopePath}\"");
        }

        return string.Join(" ", parts);
    }

    private static string? TypeClause(string tag) => tag switch
    {
        "document" => "ext:doc;docx;xls;xlsx;ppt;pptx;pdf;txt;md;rtf;csv",
        "image" => "ext:jpg;jpeg;png;gif;bmp;webp;ico;tif;tiff;svg",
        "video" => "ext:mp4;mkv;avi;mov;wmv;flv;webm;m4v",
        "audio" => "ext:mp3;flac;wav;aac;m4a;ogg;wma",
        "archive" => "ext:zip;rar;7z;tar;gz;bz2;xz;iso",
        "folder" => "folder:",
        _ => null,
    };
}
