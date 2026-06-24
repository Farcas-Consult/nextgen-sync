using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service.GymMaster;
using Fcl.Sync.Service.Health;
using Fcl.Sync.Service.Persistence;
using Fcl.Sync.Service.Reconciliation;
using Fcl.Sync.Service.Webhooks;
using Fcl.Sync.Service.ZKBio;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "FCL GymMaster Sync";
});

builder.Services.AddOptions<GymMasterWebhookOptions>()
    .Bind(builder.Configuration.GetSection(GymMasterWebhookOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ReconciliationOptions>()
    .Bind(builder.Configuration.GetSection(ReconciliationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SqliteOptions>()
    .Bind(builder.Configuration.GetSection(SqliteOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<AccessProviderOptions>()
    .Bind(builder.Configuration.GetSection(AccessProviderOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IAccessPolicy, GymAccessPolicy>();
builder.Services.AddSingleton<IGymMasterClient, PlaceholderGymMasterClient>();
builder.Services.AddSingleton<IZKBioClient, PlaceholderZKBioClient>();
builder.Services.AddSingleton<NoopAccessProvider>();
builder.Services.AddSingleton<ZKBioAccessProvider>();
builder.Services.AddSingleton<IAccessProvider, ConfiguredAccessProvider>();
builder.Services.AddSingleton<ILocalSyncStore, SqliteLocalSyncStore>();
builder.Services.AddSingleton<GymMasterWebhookHandler>();
builder.Services.AddHostedService<HourlyReconciliationWorker>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "FCL Sync",
    runtime = ".NET 10",
    status = "running"
}));

app.MapHealthEndpoints();
app.MapGymMasterWebhookEndpoints();

app.Run();
