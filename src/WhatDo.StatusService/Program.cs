using WhatDo.StatusService.Components;
using WhatDo.StatusService.Endpoints;
using WhatDo.StatusService.Mqtt;
using WhatDo.StatusService.Push;
using WhatDo.StatusService.Status;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<IInstanceStore, InMemoryInstanceStore>();
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection(MqttOptions.SectionName));
builder.Services.AddSingleton<MqttConnectionService>();
builder.Services.AddSingleton<IManagedMqttClientAccessor>(sp => sp.GetRequiredService<MqttConnectionService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttConnectionService>());
builder.Services.AddSingleton<IMqttPublisher, HiveMqPublisher>();
builder.Services.AddHostedService<StaleInstanceSweepService>();
builder.Services.Configure<PushOptions>(builder.Configuration.GetSection(PushOptions.SectionName));
builder.Services.AddSingleton<IPushSubscriptionStore, InMemoryPushSubscriptionStore>();
builder.Services.AddSingleton<IPushSender, WebPushSender>();
builder.Services.AddSingleton<IPushNotifier, PushNotifier>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapStatusEventEndpoints();
app.MapPushEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program
{
    // Marker partial class so WebApplicationFactory<Program> can find this entry point in tests.
}
