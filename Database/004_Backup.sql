/* =============================================================================
   SqlRestoreManager - Módulo de backup
   Script: 004_Backup.sql  (idempotente)
   Cria dbo.BackupRun (execução) e dbo.BackupItem (cada banco da execução).
   ============================================================================= */
SET NOCOUNT ON;
GO

USE RestoreManager;
GO

IF OBJECT_ID(N'dbo.BackupRun', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BackupRun
    (
        Id              bigint IDENTITY(1,1) NOT NULL,
        RunId           uniqueidentifier     NOT NULL,
        [Trigger]       nvarchar(20)         NOT NULL,
        Status          nvarchar(50)         NOT NULL,
        CurrentStep     nvarchar(300)        NULL,
        PercentComplete int                  NOT NULL,
        TotalDatabases  int                  NOT NULL,
        SucceededCount  int                  NOT NULL,
        FailedCount     int                  NOT NULL,
        TotalSizeBytes  bigint               NOT NULL,
        NodeName        nvarchar(128)        NULL,
        StartedAt       datetime2(7)         NOT NULL,
        FinishedAt      datetime2(7)         NULL,
        ErrorMessage    nvarchar(4000)       NULL,
        CONSTRAINT PK_BackupRun PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UQ_BackupRun_RunId UNIQUE (RunId)
    );
END
GO

IF OBJECT_ID(N'dbo.BackupItem', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BackupItem
    (
        Id              bigint IDENTITY(1,1) NOT NULL,
        RunId           uniqueidentifier     NOT NULL,
        DatabaseName    nvarchar(128)        NOT NULL,
        Status          nvarchar(50)         NOT NULL,
        FilePath        nvarchar(512)        NULL,
        SizeBytes       bigint               NOT NULL,
        LogSizeBeforeMB int                  NULL,
        LogSizeAfterMB  int                  NULL,
        StartedAt       datetime2(7)         NOT NULL,
        FinishedAt      datetime2(7)         NULL,
        ErrorMessage    nvarchar(4000)       NULL,
        CONSTRAINT PK_BackupItem PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_BackupItem_BackupRun FOREIGN KEY (RunId)
            REFERENCES dbo.BackupRun (RunId) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.BackupRun') AND name = N'UX_BackupRun_RunId')
    CREATE UNIQUE NONCLUSTERED INDEX UX_BackupRun_RunId ON dbo.BackupRun (RunId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.BackupRun') AND name = N'IX_BackupRun_StartedAt')
    CREATE NONCLUSTERED INDEX IX_BackupRun_StartedAt ON dbo.BackupRun (StartedAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.BackupItem') AND name = N'IX_BackupItem_RunId')
    CREATE NONCLUSTERED INDEX IX_BackupItem_RunId ON dbo.BackupItem (RunId);
GO

/* ----------------------------------------------- Documentação das colunas */
DECLARE @props TABLE (TableName sysname NOT NULL, ColumnName sysname NULL, Description nvarchar(500) NOT NULL);
INSERT INTO @props VALUES
 (N'BackupRun',  NULL,               N'Execuções do backup (manual ou agendado).'),
 (N'BackupRun',  N'RunId',           N'Identificador da execução (SignalR e itens).'),
 (N'BackupRun',  N'Trigger',         N'Manual ou Scheduled.'),
 (N'BackupRun',  N'Status',          N'Running, Success, Warning ou Error.'),
 (N'BackupRun',  N'TotalDatabases',  N'Quantidade de bancos selecionados.'),
 (N'BackupRun',  N'SucceededCount',  N'Bancos copiados com sucesso.'),
 (N'BackupRun',  N'FailedCount',     N'Bancos com falha.'),
 (N'BackupRun',  N'TotalSizeBytes',  N'Soma dos arquivos gerados, em bytes.'),
 (N'BackupRun',  N'NodeName',        N'Instância do SqlRestoreManager que executou.'),
 (N'BackupItem', NULL,               N'Backup de um banco dentro da execução.'),
 (N'BackupItem', N'RunId',           N'Execução a que pertence.'),
 (N'BackupItem', N'FilePath',        N'Arquivo .bak gerado.'),
 (N'BackupItem', N'SizeBytes',       N'Tamanho do backup, em bytes.'),
 (N'BackupItem', N'LogSizeBeforeMB', N'Tamanho do log antes da limpeza.'),
 (N'BackupItem', N'LogSizeAfterMB',  N'Tamanho do log após a limpeza.');

DECLARE @tbl sysname, @col sysname, @desc nvarchar(500), @l2type varchar(128), @minor int, @major int;
DECLARE props CURSOR LOCAL FAST_FORWARD FOR SELECT TableName, ColumnName, Description FROM @props;
OPEN props;
FETCH NEXT FROM props INTO @tbl, @col, @desc;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @major = OBJECT_ID(N'dbo.' + QUOTENAME(@tbl));
    SET @l2type = CASE WHEN @col IS NULL THEN NULL ELSE 'COLUMN' END;
    SET @minor = CASE WHEN @col IS NULL THEN 0 ELSE COLUMNPROPERTY(@major, @col, 'ColumnId') END;

    IF EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE class = 1 AND major_id = @major AND minor_id = @minor AND name = N'MS_Description')
        EXEC sys.sp_updateextendedproperty
             @name = N'MS_Description', @value = @desc,
             @level0type = N'SCHEMA', @level0name = N'dbo',
             @level1type = N'TABLE',  @level1name = @tbl,
             @level2type = @l2type,   @level2name = @col;
    ELSE
        EXEC sys.sp_addextendedproperty
             @name = N'MS_Description', @value = @desc,
             @level0type = N'SCHEMA', @level0name = N'dbo',
             @level1type = N'TABLE',  @level1name = @tbl,
             @level2type = @l2type,   @level2name = @col;

    FETCH NEXT FROM props INTO @tbl, @col, @desc;
END
CLOSE props;
DEALLOCATE props;
GO

PRINT 'dbo.BackupRun e dbo.BackupItem prontas.';
GO
