using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Tests.Support;

/// <summary>A throwaway data directory per test, deleted on dispose.</summary>
public sealed class TempData : IDisposable
{
    public TempData()
    {
        Root = Path.Combine(Path.GetTempPath(), "WingetNudge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new DataPaths(Path.Combine(Root, "data"));
    }

    public string Root { get; }

    public DataPaths Paths { get; }

    public string WriteFile(string relativePath, string content)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Root);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
