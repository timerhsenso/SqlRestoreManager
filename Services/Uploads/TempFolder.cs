namespace SqlRestoreManager.Services.Uploads;

public static class TempFolder
{
    public static bool TryDelete(string? folder, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return false;

        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível remover a pasta temporária {Folder}.", folder);
            return false;
        }
    }
}
