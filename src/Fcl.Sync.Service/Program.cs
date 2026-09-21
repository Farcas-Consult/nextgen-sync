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
using Fcl.Sync.Service.BioStar;

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

builder.Services.AddOptions<BioStarOptions>()
    .Bind(builder.Configuration.GetSection(BioStarOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options =>
        !string.Equals(builder.Configuration[$"{AccessProviderOptions.SectionName}:Type"], "BioStar", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(options.BaseUrl),
        "Selected BioStar provider requires BioStar:BaseUrl.")
    .Validate(options =>
        !string.Equals(builder.Configuration[$"{AccessProviderOptions.SectionName}:Type"], "BioStar", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(options.LoginId) && !string.IsNullOrWhiteSpace(options.Password),
        "Selected BioStar provider requires BioStar:LoginId and BioStar:Password.")
    .Validate(options =>
        !string.Equals(builder.Configuration[$"{AccessProviderOptions.SectionName}:Type"], "BioStar", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(options.UserGroupId) && !string.IsNullOrWhiteSpace(options.AccessGroupId),
        "Selected BioStar provider requires BioStar:UserGroupId and BioStar:AccessGroupId.")
    .Validate(options =>
        !string.Equals(builder.Configuration[$"{AccessProviderOptions.SectionName}:Type"], "BioStar", StringComparison.OrdinalIgnoreCase) ||
        options.ExpiryDateTime > options.StartDateTime && options.ExpiryDateTime > DateTimeOffset.UtcNow.AddMonths(6),
        "BioStar:ExpiryDateTime must be later than StartDateTime and at least six months in the future.")
    .Validate(options =>
        !string.Equals(builder.Configuration[$"{AccessProviderOptions.SectionName}:Type"], "BioStar", StringComparison.OrdinalIgnoreCase) ||
        options.CompanyIds.Count == 1 && options.CompanyIds.Contains(3),
        "This BioStar installation requires BioStar:CompanyIds to contain exactly company 3.")
    .ValidateOnStart();

builder.Services.AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection(DashboardOptions.SectionName));

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
builder.Services.AddHttpClient<IBioStarClient, BioStarClient>()
    .ConfigurePrimaryHttpMessageHandler(BioStarHttpMessageHandlerFactory.Create);
builder.Services.AddSingleton<NoopAccessProvider>();
builder.Services.AddSingleton<ZKBioAccessProvider>();
builder.Services.AddSingleton<BioStarAccessProvider>();
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
