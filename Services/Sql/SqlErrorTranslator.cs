using Microsoft.Data.SqlClient;

namespace SqlRestoreManager.Services.Sql;

/// <summary>
/// Converte exceções técnicas em mensagens acionáveis para o histórico/tela.
/// </summary>
public static class SqlErrorTranslator
{
    public static string Translate(Exception ex) => ex switch
    {
        RestoreValidationException v => v.Message,
        SqlException sql => TranslateSql(sql),
        InvalidDataException => "O arquivo .zip está corrompido ou não é um ZIP válido.",
        _ => ex.Message
    };

    private static string TranslateSql(SqlException ex)
    {
        var numbers = ex.Errors.Cast<SqlError>().Select(e => e.Number).ToHashSet();

        string? hint = null;
        if (numbers.Contains(3201))
            hint = "O SQL Server não conseguiu abrir o arquivo de backup. Verifique se a conta do serviço " +
                   "SQL Server tem leitura na pasta temporária e se Restore:SqlServerTempPath aponta para o " +
                   "caminho visto pelo servidor.";
        else if (numbers.Contains(3241) || numbers.Contains(3183))
            hint = "Arquivo de backup inválido, corrompido ou em formato não suportado.";
        else if (numbers.Contains(3169))
            hint = "O backup foi gerado em uma versão mais nova do SQL Server que a do servidor de destino.";
        else if (numbers.Contains(3156) || numbers.Contains(5133) || numbers.Contains(5120))
            hint = "Não foi possível criar os arquivos físicos. Verifique Restore:DataPath/LogPath e as " +
                   "permissões NTFS da conta do serviço SQL Server.";
        else if (numbers.Contains(1834))
            hint = "Um arquivo físico de destino já está em uso por outro banco.";
        else if (numbers.Contains(3101) || numbers.Contains(5061))
            hint = "O banco de destino está em uso e não pôde ser bloqueado.";
        else if (numbers.Contains(3257))
            hint = "Espaço em disco insuficiente no servidor SQL para o restore.";

        return hint is null ? ex.Message : $"{hint} Detalhe: {ex.Message}";
    }
}
