using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service;
using Fcl.Sync.Service.Dashboard;
using Fcl.Sync.Service.GymMaster;
using Fcl.Sync.Service.Health;
using Fcl.Sync.Service.Persistence;
using Fcl.Sync.Service.Reconciliation;
using Fcl.Sync.Service.Webhooks;
using Fcl.Sync.Service.ZKBio;

LocalEnv.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "FCL GymMaster Sync";
});

builder.Services.AddOptions<GymMasterWebhookOptions>()
    .Bind(builder.Configuration.GetSection(GymMasterWebhookOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<GymMasterOptions>()
    .Bind(builder.Configuration.GetSection(GymMasterOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ZKBioOptions>()
    .Bind(builder.Configuration.GetSection(ZKBioOptions.SectionName));

builder.Services.AddOptions<ReconciliationOptions>()
    .Bind(builder.Configuration.GetSection(ReconciliationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SqliteOptions>()
    .Bind(builder.Configuration.GetSection(SqliteOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<HistoryRetentionOptions>()
    .Bind(builder.Configuration.GetSection(HistoryRetentionOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<AccessProviderOptions>()
    .Bind(builder.Configuration.GetSection(AccessProviderOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IAccessPolicy, GymAccessPolicy>();
builder.Services.AddHttpClient<IGymMasterClient, GymMasterClient>();
builder.Services.AddHttpClient<IZKBioClient, ZKBioClient>()
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ZKBioOptions>>().Value;
        var handler = new HttpClientHandler();

        if (options.AllowInvalidServerCertificate)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    });
builder.Services.AddSingleton<NoopAccessProvider>();
builder.Services.AddSingleton<ZKBioAccessProvider>();
builder.Services.AddSingleton<IAccessProvider, ConfiguredAccessProvider>();
builder.Services.AddSingleton<SqliteLocalSyncStore>();
builder.Services.AddSingleton<ILocalSyncStore>(sp => sp.GetRequiredService<SqliteLocalSyncStore>());
builder.Services.AddSingleton<ISyncDashboardStore>(sp => sp.GetRequiredService<SqliteLocalSyncStore>());
builder.Services.AddSingleton<GymMasterWebhookHandler>();
builder.Services.AddHostedService<HourlyReconciliationWorker>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "FCL Sync",
    runtime = ".NET 10",
    status = "running",
    dashboard = "/dashboard"
}));

app.MapHealthEndpoints();
app.MapSyncDashboardEndpoints();
app.MapGymMasterWebhookEndpoints();

app.Run();
