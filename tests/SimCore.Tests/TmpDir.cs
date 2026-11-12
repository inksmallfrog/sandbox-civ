namespace HexaCiv.Tests;

/// <summary>
/// 测试用临时目录。数据包与插件清单都要落盘，但绝不能写进仓库里那份真数据。
/// </summary>
internal sealed class TmpDir : IDisposable
{
    public string Path { get; }

    public TmpDir(string tag)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hexaciv-test",
            tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Write(string relative, string content)
    {
        string full = System.IO.Path.Combine(Path, relative);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string Read(string relative) => File.ReadAllText(System.IO.Path.Combine(Path, relative));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { /* 临时目录留给人查现场 */ }
    }
}
