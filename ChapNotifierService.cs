namespace ChapNotifier;

using ChapNotifier.Configs;
using HtmlAgilityPack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System.Net.Http;

public class ChapNotifierService : BackgroundService
{
    private readonly ILogger<ChapNotifierService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ChapNotifierConfig _config;

    private const string ChapterLogFile = "chapter_log.txt";

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
                await CheckNewChapters();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking new chapter");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task<string> GetHtmlWithPlaywright()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        // ✅ Set user agent here
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36"
        });

        var page = await context.NewPageAsync();

        await page.GotoAsync(_config.TargetUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60000
        });
        return await page.ContentAsync();
    }

    private async Task CheckNewChapters()
    {
        var html = await GetHtmlWithPlaywright();

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

        var chapterNodes = htmlDoc.DocumentNode
            .SelectNodes("//a[contains(@class, 'text-muted')]//span[contains(@class, 'chapter-title')]")
            ?.Take(10)
            .ToList();

        if (chapterNodes == null || chapterNodes.Count == 0)
        {
            _logger.LogWarning("No chapter titles found.");
            return;
        }

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

        foreach (var chap in newChapters.OrderBy(c => c.url))
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

