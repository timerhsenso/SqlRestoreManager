using Microsoft.AspNetCore.Http;

namespace SqlRestoreManager.Services.Sql;

/// <summary>
/// Erro de regra/validação com mensagem segura para exibir ao usuário.
/// </summary>
public sealed class RestoreValidationException : Exception
{
    public RestoreValidationException(string message, int statusCode = StatusCodes.Status400BadRequest)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
