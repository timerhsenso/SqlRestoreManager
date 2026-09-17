using SqlRestoreManager.Services.Sql;
using SqlRestoreManager.Services.Uploads;

namespace SqlRestoreManager.Models;

public sealed class RestoreIndexViewModel
{
    public IReadOnlyList<DatabaseInfo> Databases { get; init; } = [];
    public int MaxUploadMB { get; init; }
    public bool LibraryEnabled { get; init; }
    public bool AllowNewDatabases { get; init; }
    public IReadOnlyList<string> AllowedExtensions { get; init; } = [];
    public string? LoadError { get; init; }
}
