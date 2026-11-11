using Microsoft.Data.Sqlite;
using Screenager.Data;

namespace Screenager.Tests;

/// <summary>A throwaway directory holding a fresh database and config file; deleted on dispose.</summary>
public sealed class TempEnv : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "screenager-tests", Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "screenager.db");

    public TempEnv() => Directory.CreateDirectory(Dir);

    public Database OpenDb() => new(DbPath);

    /// <summary>Write an INI config and load it through the real parser.</summary>
    public Config Config(string ini)
    {
        var path = Path.Combine(Dir, $"{Guid.NewGuid():N}.cfg");
        File.WriteAllText(path, ini);
        return Screenager.Config.Load(path);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // release file handles so the directory can be removed
        try { Directory.Delete(Dir, recursive: true); }
        catch { /* best effort */ }
    }
}
