namespace ChapNotifier;

using ChapNotifier.Configs;
using HtmlAgilityPack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Text.RegularExpressions;

public class ChapNotifierService : BackgroundService
{
    private readonly ILogger<ChapNotifierService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ChapNotifierConfig _config;

    private readonly string ChapterLogFile = "chapter_log.txt";

    public ChapNotifierService(
        ILogger<ChapNotifierService> logger, 
        IHttpClientFactory httpClientFactory,
        IOptions<ChapNotifierConfig> config)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckNewChapter();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking new chapter");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task CheckNewChapter()
    {
        var httpClient = _httpClientFactory.CreateClient();
        var html = await httpClient.GetStringAsync(_config.TargetUrl);

        var htmlDoc = new HtmlAgilityPack.HtmlDocument();
        htmlDoc.LoadHtml(html);

        // Get top 10 chapter nodes
        var chapterNodes = htmlDoc.DocumentNode
            .SelectNodes("//a[contains(@class, 'text-muted')]//span[contains(@class, 'chapter-title')]")
            ?.Take(10)
            .ToList();

        if (chapterNodes == null || chapterNodes.Count == 0)
        {
            _logger.LogWarning("No chapter titles found.");
            return;
        }

        // Load already notified URLs
        var notifiedUrls = File.Exists(ChapterLogFile)
            ? new HashSet<string>(File.ReadAllLines(ChapterLogFile))
            : new HashSet<string>();

        var newChapters = new List<(string title, string url)>();

        foreach (var chapterNode in chapterNodes)
        {
            var aNode = chapterNode.Ancestors("a").FirstOrDefault();
            if (aNode == null) continue;

            var url = aNode.GetAttributeValue("href", "").Trim();
            if (string.IsNullOrEmpty(url) || notifiedUrls.Contains(url)) continue;

            var title = HtmlEntity.DeEntitize(
                chapterNode.ChildNodes
                    .Where(n => n.NodeType == HtmlNodeType.Text)
                    .Select(n => n.InnerText)
                    .Aggregate("", (acc, val) => acc + val)
                    .Trim()
            );

            newChapters.Add((title, url));
        }

        if (newChapters.Count == 0)
        {
            _logger.LogInformation("No new chapters found.");
            return;
        }

        foreach (var chap in newChapters.OrderBy(c => c.url)) // notify in order
        {
            await SendTelegram($"😲 Có chap mới rồi nè!\n📚 {chap.title}\n🔗 {chap.url}");
            File.AppendAllText(ChapterLogFile, chap.url + Environment.NewLine);
            _logger.LogInformation("Notified chapter: {0}", chap.title);
        }
    }



    private async Task SendTelegram(string message)
    {
        var url = $"https://api.telegram.org/bot{_config.BotToken}/sendMessage";
        var data = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("chat_id", _config.ChatId),
            new KeyValuePair<string, string>("text", message)
        });

        var httpClient = _httpClientFactory.CreateClient();
        var response = await httpClient.PostAsync(url, data);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to send Telegram message: {status}", response.StatusCode);
        }
    }
}

