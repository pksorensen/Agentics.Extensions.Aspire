using Aspire.Hosting.Foundry.Emulator;

// A thin host. Everything is in the hosting package so the emulator ships as a NuGet
// package as well as an image — the same split MicrosoftTenant.Emulator uses.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFoundryEmulator(builder.Configuration);

var app = builder.Build();
app.MapFoundryEmulator();
app.Run();

public partial class Program;
