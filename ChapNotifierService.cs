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

        // ✅ Get all <a> links inside each chapter card
        var anchorNodes = htmlDoc.DocumentNode
            .SelectNodes("//div[contains(@class,'chapter-card-desktop')]/a[contains(@class,'chapter-link-desktop')]")
            ?.Take(15) // only take the latest few chapters
            .ToList();

        if (anchorNodes == null || anchorNodes.Count == 0)
        {
            _logger.LogWarning("No chapter titles found.");
            return;
        }

        // ✅ Read already notified chapter URLs from log file
        var notifiedUrls = File.Exists(ChapterLogFile)
            ? new HashSet<string>(File.ReadAllLines(ChapterLogFile))
            : new HashSet<string>();

        var newChapters = new List<(string title, string url, string number)>();

        foreach (var a in anchorNodes)
        {
            var url = a.GetAttributeValue("href", "").Trim();
            if (string.IsNullOrEmpty(url) || notifiedUrls.Contains(url)) continue;

            // ✅ Extract chapter title and number from HTML
            var titleNode = a.SelectSingleNode(".//div[contains(@class,'chapter-title')]");
            var numberNode = a.SelectSingleNode(".//div[contains(@class,'chapter-number')]");
            var title = HtmlEntity.DeEntitize(titleNode?.InnerText?.Trim() ?? "");
            var number = HtmlEntity.DeEntitize(numberNode?.InnerText?.Trim() ?? "");

            if (string.IsNullOrWhiteSpace(title)) continue;

            newChapters.Add((title, url, number));
        }

        if (newChapters.Count == 0)
        {
            _logger.LogInformation("No new chapters found.");
            return;
        }

        // ✅ Notify for each new chapter found
        foreach (var chap in newChapters)
        {
            var msg = $"⭐️ Có chap mới rồi nè!\n📚 {chap.number}: {chap.title}\n🔗 {chap.url}";
            await SendTelegram(msg);

            // ✅ Save URL to log file to avoid duplicate notifications
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

