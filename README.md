# SQL Restore Manager

Ferramenta web para restaurar bancos SQL Server pelo navegador, sem abrir sessão no servidor.

Feita para o cenário de quem recebe backups de clientes e precisa subi-los em um ambiente de
homologação para testes e validações: cada cliente gera o backup de um jeito, e a ferramenta
se encarrega de aceitar o que vier, restaurar com segurança e deixar o banco pronto para uso.

> **Atenção:** esta ferramenta executa `RESTORE` e altera bancos. Ela **não possui autenticação**
> até o momento. Mantenha o acesso restrito por rede, VPN ou firewall e **nunca** aponte a
> whitelist para bancos de produção.

## Recursos

**Envio do backup**
- Upload pelo navegador em streaming, com barra de progresso (até ~4 GB, limite do IIS).
- Ou restauração a partir de uma pasta do servidor, sem limite de tamanho.
- Formatos `.bak`, `.zip` e, com o 7‑Zip instalado, `.rar` e `.7z`.

**Escolha do destino**
- Lista os bancos reais do servidor (`sys.databases`), nunca os de sistema.
- Bloqueio configurável por nome ou padrão (`PROD_*`), exibido desabilitado ou oculto.
- Opção de criar um banco novo digitando o nome.
- Mostra as conexões ativas que serão encerradas antes do restore.

**Execução**
- Fila em memória, um restore por vez, com lock entre instâncias (`sp_getapplock`).
- `RESTORE VERIFYONLY` antes de sobrescrever (usa `CHECKSUM` quando o backup tiver).
- Backup `COPY_ONLY` opcional do banco atual, com retenção automática.
- Banco em `OFFLINE WITH ROLLBACK IMMEDIATE`, `RESTORE ... WITH REPLACE` e `MOVE` automático
  de todos os arquivos (dados, log, FILESTREAM e full‑text).
- Progresso real lido de `sys.dm_exec_requests`, transmitido por SignalR.
- Cancelamento do job na fila ou em andamento.

**Pós-restore automático**
- Remove replicação herdada do servidor de origem.
- Coloca o banco em `RECOVERY SIMPLE`.
- Reduz os arquivos de log e define crescimento fixo.
- Corrige usuários órfãos.
- Executa scripts `.sql` seus, globais ou por banco.

**Histórico**
- Registro completo de cada job, com etapas, alertas e tamanho do log antes e depois.
- Busca, filtro por status, paginação e exportação CSV.
- Reconciliação automática de jobs interrompidos por reciclagem do App Pool.

**Interface**
- CSS próprio, modal e toasts sem bibliotecas; cliente SignalR servido localmente.
- Nenhuma requisição para a internet: funciona em rede fechada.

## Requisitos

- .NET 8 (ASP.NET Core) — IIS com o *Hosting Bundle* em produção.
- SQL Server 2016 ou superior.
- Login SQL com permissão para `RESTORE`/`ALTER DATABASE` e `VIEW SERVER STATE`
  (`sysadmin` é o mais simples em servidor dedicado de homologação; necessário para
  `sp_removedbreplication` e `DBCC SHRINKFILE` do pós-restore).
- 7‑Zip, opcional, para `.rar` e `.7z`.

## Instalação

1. **Banco de histórico** — execute, em ordem, os scripts de `Database/`:
   `001_RestoreHistory.sql`, `002_RestoreHistory_PostRestore.sql`,
   `003_RestoreHistory_SafetyBackup.sql`. São idempotentes.
2. **Pastas** — crie a pasta temporária e, se for usar, a de backups e a de backups de segurança.
   A conta do serviço SQL Server precisa de leitura na pasta temporária e de escrita nas pastas
   de dados e log.
3. **Configuração** — ajuste `appsettings.json`. Em desenvolvimento, use
   `appsettings.Development.json` e *User Secrets* para as connection strings.
4. **IIS** — publique e configure o App Pool: *No Managed Code*, `Start Mode = AlwaysRunning`,
   `Idle Time-out = 0` e sem reciclagem periódica. Sem isso, restores longos podem ser abortados.

O `web.config` do projeto já libera uploads de até ~4 GB (o padrão do IIS é ~28 MB).

## Configuração (`Restore`)

| Chave | Descrição |
|---|---|
| `TempPath` | Pasta onde **a aplicação** grava os uploads (local ou UNC). |
| `SqlServerTempPath` | A mesma pasta vista **pelo SQL Server**. Vazio = igual a `TempPath`. |
| `LibraryPath` / `SqlServerLibraryPath` | Pasta de backups copiados manualmente (aplicação / SQL Server). |
| `DataPath` / `LogPath` | Pastas dos `.mdf`/`.ldf` no servidor. Vazio = padrão da instância. |
| `MaxUploadMB` | Limite do upload pelo navegador (1–4000). |
| `MaxExtractedGB` | Limite do `.bak` descompactado. |
| `SevenZipPath` | Caminho do `7z.exe`. Habilita `.rar` e `.7z`. |
| `BlockedDatabases` | Bancos que não podem ser restaurados. Aceita `*` e `?`. |
| `ShowBlockedDatabases` | Bloqueados aparecem desabilitados (`true`) ou ocultos (`false`). |
| `AllowNewDatabases` | Permite criar banco novo pelo nome digitado. |
| `ProgressPollSeconds` | Intervalo de leitura do progresso. |
| `TempRetentionHours` | Idade mínima para limpeza de pastas temporárias órfãs. |
| `NodeName` | Identificação da instância no histórico. Vazio = nome da máquina. |

**`Restore:PreRestore`**

| Chave | Padrão | Descrição |
|---|---|---|
| `VerifyBackup` | `true` | `RESTORE VERIFYONLY` antes de sobrescrever. |
| `SafetyBackup` | `false` | `BACKUP ... WITH COPY_ONLY` do banco atual. |
| `SafetyBackupPath` | vazio | Pasta do backup de segurança, vista pelo SQL Server. |
| `SafetyBackupCompression` | `true` | Usa `COMPRESSION`, com fallback automático. |
| `SafetyBackupRetentionDays` | `7` | Retenção dos backups de segurança. 0 = nunca apagar. |

**`Restore:PostRestore`**

| Chave | Padrão | Descrição |
|---|---|---|
| `Enabled` | `true` | Liga/desliga todo o pós-restore. |
| `RemoveReplication` | `true` | `sp_removedbreplication` quando o banco vem com replicação. |
| `SetRecoverySimple` | `true` | `RECOVERY SIMPLE`. |
| `ShrinkLog` | `true` | `CHECKPOINT` + `DBCC SHRINKFILE` nos arquivos de log. |
| `LogTargetSizeMB` | `512` | Tamanho alvo do log. |
| `LogGrowthMB` | `256` | Crescimento fixo do log. 0 = não altera. |
| `FixOrphanUsers` | `true` | `ALTER USER ... WITH LOGIN` para usuários com login de mesmo nome. |
| `ScriptsPath` | vazio | Pasta com `.sql` (raiz = todos os bancos; subpasta = só aquele banco). |
| `CommandTimeoutMinutes` | `30` | Timeout de cada comando do pós-restore. |

Configuração inválida impede a aplicação de subir (`ValidateOnStart`), com a mensagem do que corrigir.

## Aplicação em outra máquina (desenvolvimento)

Quando a aplicação roda fora do servidor SQL, quem grava o arquivo é a aplicação, mas quem o lê é o
serviço do SQL Server. Compartilhe a pasta temporária e informe os dois caminhos:

```json
"Restore": {
  "TempPath": "\\\\SERVIDOR\\RestoreTemp",
  "SqlServerTempPath": "C:\\RestoreManager\\Temp"
}
```

## O que é aceito

Backups **FULL**, com ou sem checksum, com ou sem compressão, com vários conjuntos no mesmo arquivo
(usa o FULL mais recente), com qualquer nome lógico de arquivo e com múltiplos arquivos de dados e log.

Recusa, com mensagem explicando: backup diferencial ou de log isolado, backup gerado em versão do
SQL Server mais nova que a do destino e arquivo compactado com mais de um `.bak`.

## Estrutura

```
Configuration/   opções e validação no startup
Controllers/     Restore (tela e API) e History
Data/            DbContext do histórico
Database/        scripts SQL (fonte da verdade do schema)
Models/          entidades, status e view models
Services/Sql/    catálogo de bancos, restore, pós-restore
Services/Jobs/   fila, worker, processador e manutenção
Services/Uploads/ upload em streaming, extração e pasta de backups
Views/ wwwroot/  interface (sem dependências externas)
```

## Roadmap

- Autenticação e autorização (Windows/AD ou Identity), antiforgery e auditoria de usuário.
- Logging estruturado com Serilog e segredos fora do `appsettings`.
- Upload em chunks para arquivos acima de 4 GB.
- Jobs persistentes, no lugar da fila em memória.
