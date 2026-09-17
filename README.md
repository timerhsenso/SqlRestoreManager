# SQL Restore Manager

Aplicação ASP.NET Core 8 MVC para restaurar bancos SQL Server pelo navegador
(bancos de clientes para testes/validação — **não usar contra bancos de produção**).

## Status

- **Fase 1 (atual)** – correções de funcionamento: upload grande em streaming, progresso real,
  restore seguro (OFFLINE + rollback para ONLINE), lock entre instâncias, worker resiliente,
  reconciliação de jobs interrompidos, correção de XSS.
- Fase 2 – autenticação/autorização, antiforgery, auditoria, Serilog, secrets.
- Fase 3 – Clean Architecture, AdminLTE/DataTables, testes.
- Fase 4 – restore a partir de pasta do servidor, upload em chunks (> 4 GB), pós-restore etc.

## Como funciona

1. O navegador envia o `.bak`/`.zip` (streaming) para `Restore:TempPath\<jobId>\`.
2. O job entra na fila em memória (um restore por vez; um job por banco).
3. O worker extrai o `.zip` (se houver), converte o caminho para a visão do SQL Server
   (`Restore:SqlServerTempPath`) e executa `RESTORE HEADERONLY`/`FILELISTONLY`.
4. `sp_getapplock` impede restores simultâneos do mesmo banco (inclusive dev x IIS).
5. Banco de destino → `OFFLINE WITH ROLLBACK IMMEDIATE` → `RESTORE ... WITH REPLACE, MOVE ...`.
   Se o restore falhar logo no início, o banco volta para `ONLINE`.
6. Progresso lido de `sys.dm_exec_requests` pelo SPID da conexão do restore, enviado via SignalR
   apenas para quem iniciou o job.

## Configuração (`Restore`)

| Chave | Descrição |
|---|---|
| `TempPath` | Pasta onde **a aplicação** grava os uploads (local ou UNC). |
| `SqlServerTempPath` | A mesma pasta vista **pelo SQL Server**. Vazio = igual a `TempPath`. |
| `DataPath` / `LogPath` | Pastas de .mdf/.ldf **no servidor SQL**. Vazio = padrão da instância. |
| `MaxUploadMB` | Limite de upload (1–4000; o IIS não aceita mais que ~4 GB). |
| `MaxExtractedGB` | Limite do .bak descompactado de um .zip. |
| `ProgressPollSeconds` | Intervalo de leitura do progresso. |
| `TempRetentionHours` | Idade mínima para limpeza de pastas temporárias órfãs. |
| `NodeName` | Nome da instância no histórico. Vazio = nome da máquina. |
| `BlockedDatabases` | Bancos que não podem ser restaurados. Aceita `*` e `?` (ex.: `PROD_*`). |
| `ShowBlockedDatabases` | `true` = bloqueados aparecem desabilitados; `false` = ficam ocultos. |

A lista de destino vem de `sys.databases` do servidor. Bancos de sistema (`master`, `model`, `msdb`, `tempdb`, `distribution`, `SSISDB`) nunca aparecem, e o banco de histórico (`HistoryConnection`) aparece sempre bloqueado. O banco de destino precisa já existir no servidor e é revalidado no upload e novamente no início do restore.

A aplicação **não sobe** se a configuração for inválida (`ValidateOnStart`).

## Banco de histórico

Execute `Database/001_RestoreHistory.sql` (idempotente) antes do primeiro uso e a cada atualização.
Não há mais `EnsureCreated()`.

## Ambiente de desenvolvimento (Visual Studio → SQL na rede)

O upload é gravado pela sua máquina, mas quem lê o `.bak` é o serviço do SQL Server remoto.
Por isso, use um compartilhamento da pasta temporária do servidor:

1. No servidor SQL, crie `D:\RestoreManager\Temp` e compartilhe como `\\SERVIDOR-SQL\RestoreTemp`.
2. Permissões:
   - seu usuário Windows: **modificar** no compartilhamento e no NTFS;
   - conta do serviço SQL Server (ex.: `NT Service\MSSQLSERVER`): **leitura** no NTFS.
3. `appsettings.Development.json`:
   ```json
   "Restore": {
     "TempPath": "\\\\SERVIDOR-SQL\\RestoreTemp",
     "SqlServerTempPath": "D:\\RestoreManager\\Temp"
   }
   ```
4. Seu login SQL (Integrated Security) precisa de `dbcreator` (ou `sysadmin`) e `VIEW SERVER STATE`.

## Produção (IIS na mesma máquina do SQL Server)

1. Instale o **ASP.NET Core 8 Hosting Bundle** e habilite o recurso **WebSocket Protocol** do IIS.
2. Publique (`dotnet publish -c Release`). O `web.config` do projeto já libera uploads de até ~4 GB.
3. Deixe `SqlServerTempPath` vazio (`TempPath` local).
4. **App Pool** (essencial para restores longos não serem abortados):
   - .NET CLR version: *No Managed Code*;
   - Start Mode: `AlwaysRunning`;
   - Idle Time-out (minutes): `0`;
   - Regular Time Interval (minutes): `0` (sem reciclagem periódica);
   - no site: Preload Enabled = `True`.
5. Permissões:
   - `IIS APPPOOL\<pool>`: modificar em `TempPath`;
   - conta do serviço SQL Server: leitura em `TempPath`, modificar em `DataPath`/`LogPath`;
   - login SQL `IIS APPPOOL\<pool>`: `dbcreator` + `VIEW SERVER STATE` (ou `sysadmin`)
     e `db_datareader`/`db_datawriter` no banco `RestoreManager`.

## Limitações conhecidas (Fase 1)

- **Sem autenticação.** Restrinja por rede/firewall até a Fase 2.
- Upload limitado a ~4 GB pelo IIS; use `.zip` para backups maiores (o limite do .bak extraído é `MaxExtractedGB`).
- Fila em memória: se o App Pool reiniciar, jobs em andamento viram `Interrupted`.
  Se isso ocorrer durante o RESTORE, o banco pode ficar em `RESTORING` — basta restaurar novamente.
- Apenas backups FULL (diferencial/log não suportados).
