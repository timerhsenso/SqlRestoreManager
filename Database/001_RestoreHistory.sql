/* =============================================================================
   SqlRestoreManager - Fase 1
   Script: 001_RestoreHistory.sql
   Objetivo: criar/atualizar dbo.RestoreHistory (idempotente, pode rodar N vezes).
   Substitui o EnsureCreated() da versão anterior.
   Executar no SQL Server que hospeda o banco de histórico (HistoryConnection).
   ============================================================================= */
SET NOCOUNT ON;
GO

IF DB_ID(N'RestoreManager') IS NULL
    CREATE DATABASE RestoreManager;
GO

USE RestoreManager;
GO

/* ---------------------------------------------------------------- Tabela */
IF OBJECT_ID(N'dbo.RestoreHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RestoreHistory
    (
        Id                   bigint IDENTITY(1,1) NOT NULL,
        JobId                uniqueidentifier     NOT NULL,
        OriginalFileName     nvarchar(260)        NOT NULL,
        TargetDatabase       nvarchar(128)        NOT NULL,
        SourceDatabase       nvarchar(128)        NULL,
        Status               nvarchar(50)         NOT NULL,
        CurrentStep          nvarchar(200)        NULL,
        PercentComplete      int                  NOT NULL,
        FileSizeBytes        bigint               NOT NULL,
        DisconnectedSessions int                  NOT NULL,
        BackupPosition       int                  NULL,
        BackupFinishDate     datetime2(7)         NULL,
        BackupServerName     nvarchar(128)        NULL,
        NodeName             nvarchar(128)        NULL,
        StartedAt            datetime2(7)         NOT NULL,
        FinishedAt           datetime2(7)         NULL,
        ErrorMessage         nvarchar(4000)       NULL,
        CONSTRAINT PK_RestoreHistory PRIMARY KEY CLUSTERED (Id)
    );
END
GO

/* ------------------------------- Colunas novas (tabela criada pelo MVP) */
IF COL_LENGTH(N'dbo.RestoreHistory', N'CurrentStep') IS NULL
    ALTER TABLE dbo.RestoreHistory ADD CurrentStep nvarchar(200) NULL;
IF COL_LENGTH(N'dbo.RestoreHistory', N'BackupPosition') IS NULL
    ALTER TABLE dbo.RestoreHistory ADD BackupPosition int NULL;
IF COL_LENGTH(N'dbo.RestoreHistory', N'BackupFinishDate') IS NULL
    ALTER TABLE dbo.RestoreHistory ADD BackupFinishDate datetime2(7) NULL;
IF COL_LENGTH(N'dbo.RestoreHistory', N'BackupServerName') IS NULL
    ALTER TABLE dbo.RestoreHistory ADD BackupServerName nvarchar(128) NULL;
IF COL_LENGTH(N'dbo.RestoreHistory', N'NodeName') IS NULL
    ALTER TABLE dbo.RestoreHistory ADD NodeName nvarchar(128) NULL;
GO

/* ---------------------------------------------------------------- Índices */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.RestoreHistory') AND name = N'UX_RestoreHistory_JobId')
    CREATE UNIQUE NONCLUSTERED INDEX UX_RestoreHistory_JobId
        ON dbo.RestoreHistory (JobId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.RestoreHistory') AND name = N'IX_RestoreHistory_StartedAt')
    CREATE NONCLUSTERED INDEX IX_RestoreHistory_StartedAt
        ON dbo.RestoreHistory (StartedAt);
GO

/* ------------------------- Jobs do MVP que ficaram "ativos" para sempre */
UPDATE dbo.RestoreHistory
   SET Status       = N'Interrupted',
       CurrentStep  = N'Interrompido',
       FinishedAt   = COALESCE(FinishedAt, SYSDATETIME()),
       ErrorMessage = COALESCE(ErrorMessage, N'Job da versão anterior encerrado na migração para a Fase 1.')
 WHERE NodeName IS NULL
   AND Status IN (N'Queued', N'Preparing', N'Restoring');
GO

/* ------------------------------------------------ Extended Properties */
DECLARE @props TABLE (ColumnName sysname NULL, Description nvarchar(500) NOT NULL);
INSERT INTO @props (ColumnName, Description) VALUES
 (NULL,                   N'Histórico de restores executados pelo SqlRestoreManager.'),
 (N'Id',                  N'Identificador sequencial.'),
 (N'JobId',               N'Identificador do job (usado na fila, SignalR e pasta temporária).'),
 (N'OriginalFileName',    N'Nome do arquivo enviado pelo usuário (apenas exibição).'),
 (N'TargetDatabase',      N'Banco de destino (whitelist Restore:AllowedDatabases).'),
 (N'SourceDatabase',      N'Nome do banco de origem lido do RESTORE HEADERONLY.'),
 (N'Status',              N'Queued, Preparing, Validating, Restoring, Success, Error ou Interrupted.'),
 (N'CurrentStep',         N'Descrição da etapa atual/última etapa.'),
 (N'PercentComplete',     N'Percentual geral do job (0-100).'),
 (N'FileSizeBytes',       N'Tamanho do arquivo enviado, em bytes.'),
 (N'DisconnectedSessions',N'Quantidade de sessões encerradas antes do restore.'),
 (N'BackupPosition',      N'Posição (FILE) do backup set FULL restaurado.'),
 (N'BackupFinishDate',    N'Data de término do backup na origem.'),
 (N'BackupServerName',    N'Servidor SQL onde o backup foi gerado.'),
 (N'NodeName',            N'Instância do SqlRestoreManager que processou o job.'),
 (N'StartedAt',           N'Data/hora (local) de criação do job.'),
 (N'FinishedAt',          N'Data/hora (local) de término do job.'),
 (N'ErrorMessage',        N'Mensagem de erro/interrupção.');

DECLARE @col sysname, @desc nvarchar(500), @level2type varchar(128), @minor int;
DECLARE @tableId int = OBJECT_ID(N'dbo.RestoreHistory');

DECLARE props CURSOR LOCAL FAST_FORWARD FOR SELECT ColumnName, Description FROM @props;
OPEN props;
FETCH NEXT FROM props INTO @col, @desc;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @level2type = CASE WHEN @col IS NULL THEN NULL ELSE 'COLUMN' END;
    SET @minor = CASE WHEN @col IS NULL THEN 0 ELSE COLUMNPROPERTY(@tableId, @col, 'ColumnId') END;

    IF EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE class = 1 AND major_id = @tableId AND minor_id = @minor AND name = N'MS_Description')
        EXEC sys.sp_updateextendedproperty
             @name = N'MS_Description', @value = @desc,
             @level0type = N'SCHEMA', @level0name = N'dbo',
             @level1type = N'TABLE',  @level1name = N'RestoreHistory',
             @level2type = @level2type, @level2name = @col;
    ELSE
        EXEC sys.sp_addextendedproperty
             @name = N'MS_Description', @value = @desc,
             @level0type = N'SCHEMA', @level0name = N'dbo',
             @level1type = N'TABLE',  @level1name = N'RestoreHistory',
             @level2type = @level2type, @level2name = @col;

    FETCH NEXT FROM props INTO @col, @desc;
END
CLOSE props;
DEALLOCATE props;
GO

/* ------------------------------------------------------------ Permissões
   Ajuste a conta conforme o ambiente (App Pool / usuário de dev):

   CREATE USER [IIS APPPOOL\SqlRestoreManager] FOR LOGIN [IIS APPPOOL\SqlRestoreManager];
   ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\SqlRestoreManager];
   ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\SqlRestoreManager];
*/
PRINT 'dbo.RestoreHistory pronta.';
GO
