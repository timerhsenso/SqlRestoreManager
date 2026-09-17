using SqlRestoreManager.Services.Sql;

namespace SqlRestoreManager.Models;

public sealed class RestoreIndexViewModel
{
    public IReadOnlyList<DatabaseInfo> Databases { get; init; } = [];
    public int MaxUploadMB { get; init; }
    public string? LoadError { get; init; }
}
