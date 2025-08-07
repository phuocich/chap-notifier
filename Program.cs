using ChapNotifier;
using ChapNotifier.Configs;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddHostedService<ChapNotifierService>();
builder.Services.Configure<ChapNotifierConfig>(
    builder.Configuration.GetSection(nameof(ChapNotifierConfig)));

builder.Services.PostConfigure<ChapNotifierConfig>(config =>
{
    config.TargetUrl = Environment.GetEnvironmentVariable("TARGET_URL") ?? config.TargetUrl;
    config.BotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? config.BotToken;
    config.ChatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ?? config.ChatId;
});

var host = builder.Build();
host.Run();
