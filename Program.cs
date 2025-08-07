using ChapNotifier;
using ChapNotifier.Configs;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddHostedService<ChapNotifierService>();
builder.Services.Configure<ChapNotifierConfig>(
    builder.Configuration.GetSection(nameof(ChapNotifierConfig)));

var host = builder.Build();
host.Run();
