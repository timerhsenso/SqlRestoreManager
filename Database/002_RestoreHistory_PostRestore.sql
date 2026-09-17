/* =============================================================================
   SqlRestoreManager - Fase 4 (pós-restore)
   Script: 002_RestoreHistory_PostRestore.sql
   Objetivo: adicionar colunas do pós-restore em dbo.RestoreHistory (idempotente).
   ============================================================================= */
SET NOCOUNT ON;
GO

USE RestoreManager;
GO

IF COL_LENGTH(N'dbo.RestoreHistory', N'LogSizeBeforeMB') IS NULL
    ALTER TABLE dbo.RestoreHistory ADD LogSizeBeforeMB int NULL;
IF COL_LENGTH(N'dbo.RestoreHistory', N'LogSizeAfterMB') IS NULL
    ALTER TABLE dbo.RestoreHistory ADD LogSizeAfterMB int NULL;
IF COL_LENGTH(N'dbo.RestoreHistory', N'PostRestoreLog') IS NULL
    ALTER TABLE dbo.RestoreHistory ADD PostRestoreLog nvarchar(max) NULL;
GO

DECLARE @props TABLE (ColumnName sysname NOT NULL, Description nvarchar(500) NOT NULL);
INSERT INTO @props (ColumnName, Description) VALUES
 (N'Status',          N'Queued, Preparing, Validating, Restoring, PostRestoring, Success, Warning, Error ou Interrupted.'),
 (N'LogSizeBeforeMB', N'Tamanho total dos arquivos de log logo após o RESTORE, em MB.'),
 (N'LogSizeAfterMB',  N'Tamanho total dos arquivos de log após o pós-restore, em MB.'),
 (N'PostRestoreLog',  N'Resultado de cada etapa do pós-restore ([OK]/[ALERTA]).');

DECLARE @col sysname, @desc nvarchar(500), @minor int;
DECLARE @tableId int = OBJECT_ID(N'dbo.RestoreHistory');

DECLARE props CURSOR LOCAL FAST_FORWARD FOR SELECT ColumnName, Description FROM @props;
OPEN props;
FETCH NEXT FROM props INTO @col, @desc;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @minor = COLUMNPROPERTY(@tableId, @col, 'ColumnId');

    IF EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE class = 1 AND major_id = @tableId AND minor_id = @minor AND name = N'MS_Description')
        EXEC sys.sp_updateextendedproperty
             @name = N'MS_Description', @value = @desc,
             @level0type = N'SCHEMA', @level0name = N'dbo',
             @level1type = N'TABLE',  @level1name = N'RestoreHistory',
             @level2type = N'COLUMN', @level2name = @col;
    ELSE
        EXEC sys.sp_addextendedproperty
             @name = N'MS_Description', @value = @desc,
             @level0type = N'SCHEMA', @level0name = N'dbo',
             @level1type = N'TABLE',  @level1name = N'RestoreHistory',
             @level2type = N'COLUMN', @level2name = @col;

    FETCH NEXT FROM props INTO @col, @desc;
END
CLOSE props;
DEALLOCATE props;
GO

PRINT 'dbo.RestoreHistory atualizada (pós-restore).';
GO
