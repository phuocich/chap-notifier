using ChapNotifier.Configs;
using HtmlAgilityPack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChapNotifier;

public class ChapNotifierService : BackgroundService
{
    private readonly ILogger<ChapNotifierService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ChapNotifierConfig _config;
    private readonly string ChapterLogFile;

    public ChapNotifierService(
        ILogger<ChapNotifierService> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<ChapNotifierConfig> config)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config.Value;

        // Consistent path for chapter_log.json
        ChapterLogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "chap_notifier", "chapter_log.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChapterLogFile)!);
            _logger.LogInformation("Chapter log file path: {0}", ChapterLogFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create directory for chapter log");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckNewChapters();
                _logger.LogInformation("Check completed, exiting.");
                Environment.Exit(0); // Exit after one run for GitHub Actions
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking new chapters");
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
        _logger.LogInformation("Checking for new chapters...");
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

        // Load existing notified chapters
        var notifiedChapters = new List<NotifiedChapter>();
        if (File.Exists(ChapterLogFile))
        {
            try
            {
                var json = File.ReadAllText(ChapterLogFile);
                notifiedChapters = JsonSerializer.Deserialize<List<NotifiedChapter>>(json) ?? new List<NotifiedChapter>();
                _logger.LogInformation("Loaded {0} notified chapters from file.", notifiedChapters.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read or parse chapter_log.json");
            }
        }
        else
        {
            _logger.LogInformation("No existing chapter_log.json found, starting fresh.");
        }

        var notifiedUrls = new HashSet<string>(notifiedChapters.Select(c => NormalizeUrl(c.Url)));
        var newChapters = new List<(string title, string url)>();

        foreach (var chapterNode in chapterNodes)
        {
            var aNode = chapterNode.Ancestors("a").FirstOrDefault();
            if (aNode == null) continue;

            var url = NormalizeUrl(aNode.GetAttributeValue("href", "").Trim());
            if (string.IsNullOrEmpty(url) || notifiedUrls.Contains(url))
            {
                _logger.LogDebug("Skipping URL: {0} (already notified or invalid)", url);
                continue;
            }

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

        // Add new chapters to notified list
        notifiedChapters.AddRange(newChapters.Select(c => new NotifiedChapter
        {
            Url = c.url,
            Title = c.title,
            NotifiedAt = DateTime.UtcNow
        }));

        foreach (var chap in newChapters.OrderBy(c => c.url))
        {
            _logger.LogInformation("Found new chapter: {0} ({1})", chap.title, chap.url);
            await SendTelegram($"😲 Có chap mới rồi nè!\n📚 {chap.title}\n🔗 {chap.url}");
            _logger.LogInformation("Notified chapter: {0}", chap.title);
        }

        // Save updated chapters to JSON
        try
        {
            File.WriteAllText(ChapterLogFile, JsonSerializer.Serialize(notifiedChapters, new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("Saved {0} chapters to chapter_log.json", notifiedChapters.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save chapter_log.json");
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
        else
        {
            _logger.LogInformation("Telegram message sent successfully");
        }
    }

    private string NormalizeUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        // Remove trailing slashes and query parameters for consistent comparison
        var uri = new Uri(url, UriKind.RelativeOrAbsolute);
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private class NotifiedChapter
    {
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime NotifiedAt { get; set; }
    }
}