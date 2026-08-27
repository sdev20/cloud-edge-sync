using Microsoft.Extensions.Options;
using SyncService.DomainServices;
using SyncService.DomainServices.BusinessLogic;
using SyncService.DomainServices.BusinessLogic.Core;
using SyncService.Infrastructure.Client;
using SyncService.Infrastructure.Client.Configuration;
using SyncService.Infrastructure.Client.Core;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.Configure<InstrumentConfiguration>(
    builder.Configuration.GetSection(nameof(InstrumentConfiguration)));

builder.Services.AddSingleton<InMemoryDataStore>();
builder.Services.AddScoped<IInstrumentService, InstrumentService>();

builder.Services.AddHttpClient<ISyncToInstrument, SyncToInstrument>((serviceProvider, client) =>
{
    var instrumentConfiguration = serviceProvider.GetRequiredService<IOptions<InstrumentConfiguration>>().Value;
    if (!string.IsNullOrWhiteSpace(instrumentConfiguration.InstrumentUri))
    {
        client.BaseAddress = new Uri(instrumentConfiguration.InstrumentUri);
    }
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "SyncService.WebAPI v1");
    });
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();