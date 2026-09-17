using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlRestoreManager.Configuration;
using SqlRestoreManager.Data;
using SqlRestoreManager.Services.Backup;
using SqlRestoreManager.Services.Jobs;
using SqlRestoreManager.Services.Notifications;
using SqlRestoreManager.Services.Sql;
using SqlRestoreManager.Services.Uploads;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- MVC / SignalR
builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();

// ---------------------------------------------------------------- Opções
builder.Services
    .AddOptions<RestoreOptions>()
    .Bind(builder.Configuration.GetSection(RestoreOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<RestoreOptions>, RestoreOptionsValidator>();

builder.Services
    .AddOptions<BackupOptions>()
    .Bind(builder.Configuration.GetSection(BackupOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BackupOptions>, BackupOptionsValidator>();

// Exceção em BackgroundService não derruba a aplicação; tempo extra no shutdown.
builder.Services.Configure<HostOptions>(o =>
{
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
    o.ShutdownTimeout = TimeSpan.FromSeconds(60);
});

// ---------------------------------------------------------------- Dados
var historyConnection = builder.Configuration.GetConnectionString("HistoryConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:HistoryConnection não configurada.");

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(historyConnection, sql => sql.EnableRetryOnFailure(3)));

// ---------------------------------------------------------------- Serviços
builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddSingleton(sp => new DatabasePolicy(
    sp.GetRequiredService<IOptions<RestoreOptions>>().Value,
    sp.GetRequiredService<SqlConnectionFactory>().HistoryDatabaseName));
builder.Services.AddSingleton<DatabaseCatalog>();
builder.Services.AddSingleton<BackupPathMapper>();
builder.Services.AddSingleton<SqlRestoreService>();
builder.Services.AddSingleton<PostRestoreService>();
builder.Services.AddSingleton<LogMaintenance>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<BackupNotifier>();
builder.Services.AddSingleton<BackupRunner>();
builder.Services.AddSingleton<BackupExtractor>();
builder.Services.AddSingleton<BackupLibrary>();
builder.Services.AddSingleton<BackupUploadService>();
builder.Services.AddSingleton<RestoreNotifier>();
builder.Services.AddSingleton<RestoreQueue>();
builder.Services.AddSingleton<RestoreJobRegistry>();
builder.Services.AddScoped<RestoreJobProcessor>();
builder.Services.AddHostedService<RestoreWorker>();
builder.Services.AddHostedService<RestoreMaintenanceService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// O schema do histórico é criado por Database/001_RestoreHistory.sql (sem EnsureCreated).

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// FASE 2: app.UseAuthentication(); app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Restore}/{action=Index}/{id?}");

app.MapHub<RestoreHub>("/restoreHub");

app.Run();
