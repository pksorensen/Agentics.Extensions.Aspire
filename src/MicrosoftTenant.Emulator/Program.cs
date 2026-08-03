using Aspire.Hosting.AzureResourceManager.Emulator;
using Aspire.Hosting.MicrosoftGraph.Emulator;
using Aspire.Hosting.MicrosoftTenant.Emulator;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMicrosoftTenantEmulator(builder.Configuration);
builder.Services.AddMicrosoftGraphEmulator();
builder.Services.AddAzureResourceManagerEmulator(builder.Configuration);

var app = builder.Build();
app.MapMicrosoftTenantEmulator();
app.MapMicrosoftGraphEmulator();
app.MapAzureResourceManagerEmulator();
app.MapMicrosoftTenantPortal();
app.Run();
