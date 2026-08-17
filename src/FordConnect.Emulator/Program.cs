using Aspire.Hosting.FordConnect.Emulator;

// A thin host. Everything is in the hosting package so the emulator ships as a NuGet
// package as well as an image — the same split MicrosoftTenant.Emulator uses.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFordConnectEmulator(builder.Configuration);

var app = builder.Build();
app.MapFordConnectEmulator();
app.MapFordConnectPortal();
app.Run();
