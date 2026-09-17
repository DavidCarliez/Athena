using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Profiles;

internal sealed class TelegramApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private long _nextUpdateId;

    public TelegramApiClient(
        string botToken,
        string apiBase,
        string userAgent,
        string proxyHost,
        string proxyPort,
        string proxyUser,
        string proxyPassword)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new ArgumentException("A Telegram bot token is required.", nameof(botToken));
        }

        _endpoint = $"{apiBase.TrimEnd('/')}/bot{botToken}/";
        _httpClient = new HttpClient(CreateHandler(proxyHost, proxyPort, proxyUser, proxyPassword))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }
    }

    public async Task SendTextAsync(string chatId, string text, CancellationToken cancellationToken)
    {
        var request = new TelegramSendMessageRequest
        {
            ChatId = chatId,
            Text = text,
            DisableWebPagePreview = true
        };

        await PostAsync<JsonElement>("sendMessage", request, 30, cancellationToken);
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var request = new TelegramGetUpdatesRequest
        {
            Offset = _nextUpdateId,
            Timeout = Math.Max(1, timeoutSeconds),
            AllowedUpdates = ["message"]
        };

        TelegramUpdate[] updates = await PostAsync<TelegramUpdate[]>(
            "getUpdates",
            request,
            request.Timeout + 30,
            cancellationToken) ?? [];

        if (updates.Length > 0)
        {
            _nextUpdateId = updates.Max(update => update.UpdateId) + 1;
        }

        return updates;
    }

    private async Task<T?> PostAsync<T>(
        string method,
        object request,
        int requestTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));
            using var content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync(
                _endpoint + method,
                content,
                timeout.Token);
            string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            TelegramApiResponse<T>? telegramResponse = JsonSerializer.Deserialize<TelegramApiResponse<T>>(
                responseBody,
                JsonOptions);

            if (response.StatusCode == HttpStatusCode.TooManyRequests &&
                telegramResponse?.Parameters?.RetryAfter is int retryAfter)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retryAfter, 1, 60)), cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode || telegramResponse?.Ok != true)
            {
                throw new HttpRequestException(
                    telegramResponse?.Description ?? $"Telegram returned HTTP {(int)response.StatusCode}.");
            }

            return telegramResponse.Result;
        }

        throw new HttpRequestException("Telegram rate limit retries were exhausted.");
    }

    private static HttpMessageHandler CreateHandler(
        string proxyHost,
        string proxyPort,
        string proxyUser,
        string proxyPassword)
    {
        var handler = new HttpClientHandler();
        if (string.IsNullOrWhiteSpace(proxyHost))
        {
            return handler;
        }

        string address = proxyHost;
        if (!string.IsNullOrWhiteSpace(proxyPort))
        {
            address = $"{proxyHost.TrimEnd('/')}:{proxyPort}";
        }

        var proxy = new WebProxy(address);
        if (!string.IsNullOrWhiteSpace(proxyUser))
        {
            proxy.Credentials = new NetworkCredential(proxyUser, proxyPassword);
        }

        handler.Proxy = proxy;
        handler.UseProxy = true;
        return handler;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal sealed class TelegramApiResponse<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public TelegramResponseParameters? Parameters { get; set; }
}

internal sealed class TelegramResponseParameters
{
    [JsonPropertyName("retry_after")]
    public int? RetryAfter { get; set; }
}

internal sealed class TelegramGetUpdatesRequest
{
    [JsonPropertyName("offset")]
    public long Offset { get; set; }

    [JsonPropertyName("timeout")]
    public int Timeout { get; set; }

    [JsonPropertyName("allowed_updates")]
    public string[] AllowedUpdates { get; set; } = [];
}

internal sealed class TelegramSendMessageRequest
{
    [JsonPropertyName("chat_id")]
    public string ChatId { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("disable_web_page_preview")]
    public bool DisableWebPagePreview { get; set; }
}

internal sealed class TelegramUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; set; }

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; set; }
}

internal sealed class TelegramMessage
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("from")]
    public TelegramUser? From { get; set; }
}

internal sealed class TelegramUser
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("is_bot")]
    public bool IsBot { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}
