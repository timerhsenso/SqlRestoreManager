namespace SqlRestoreManager.Models;

public sealed class HistoryViewModel
{
    public IReadOnlyList<RestoreHistory> Items { get; init; } = [];
    public string? Query { get; init; }
    public string? Status { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public int Total { get; init; }
    public string NodeName { get; init; } = string.Empty;

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
    public int FirstRow => Total == 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int LastRow => Math.Min(Page * PageSize, Total);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;

    public static readonly (string Value, string Label)[] StatusFilters =
    [
        ("", "Todos os status"),
        ("active", "Em andamento"),
        (RestoreStatus.Success, "Concluído"),
        (RestoreStatus.Warning, "Concluído com alertas"),
        (RestoreStatus.Error, "Erro"),
        (RestoreStatus.Canceled, "Cancelado"),
        (RestoreStatus.Interrupted, "Interrompido")
    ];
}
