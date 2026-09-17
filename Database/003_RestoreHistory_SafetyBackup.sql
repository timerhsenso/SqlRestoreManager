/* =============================================================================
   SqlRestoreManager - Fase 4 (verificação e backup de segurança)
   Script: 003_RestoreHistory_SafetyBackup.sql  (idempotente)
   ============================================================================= */
SET NOCOUNT ON;
GO

USE RestoreManager;
GO

IF COL_LENGTH(N'dbo.RestoreHistory', N'SafetyBackupPath') IS NULL
    ALTER TABLE dbo.RestoreHistory ADD SafetyBackupPath nvarchar(512) NULL;
GO

DECLARE @props TABLE (ColumnName sysname NOT NULL, Description nvarchar(500) NOT NULL);
INSERT INTO @props (ColumnName, Description) VALUES
 (N'Status',           N'Queued, Preparing, Validating, Verifying, SafetyBackup, Restoring, PostRestoring, Success, Warning, Canceled, Error ou Interrupted.'),
 (N'SafetyBackupPath', N'Caminho do BACKUP COPY_ONLY gerado antes de sobrescrever o banco.');

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

PRINT 'dbo.RestoreHistory atualizada (backup de seguranca).';
GO
