using System.Collections.Concurrent;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClawTray.Services.VoiceAssistant;

public sealed class VoiceAssistantChatTurnClient : IVoiceAssistantChatTurnClient, IDisposable
{
    private const int CanceledIdentityLimit = 32;
    private readonly OpenClawChatDataProvider _provider;
    private readonly ConcurrentDictionary<string, ResponseIdentity> _responses = new(StringComparer.Ordinal);
    private readonly object _canceledGate = new();
    private readonly object _readinessGate = new();
    private readonly Queue<string> _canceledOrder = new();
    private readonly HashSet<string> _canceled = new(StringComparer.Ordinal);
    private string? _lastReadySessionKey;

    public VoiceAssistantChatTurnClient(OpenClawChatDataProvider provider)
    {
        _provider = provider;
        _provider.Changed += OnProviderChanged;
        _lastReadySessionKey = _provider.GetVoiceAssistantReadySessionKey();
        _provider.VoiceAssistantResponseObserved += OnResponseObserved;
    }

    public event Action? ReadinessChanged;

    public string? GetReadySessionKey() => _provider.GetVoiceAssistantReadySessionKey();

    public Task<VoiceAssistantTurnReceipt> SendAsync(
        string sessionKey,
        string request,
        CancellationToken cancellationToken) =>
        _provider.SendVoiceAssistantMessageAsync(sessionKey, request, cancellationToken);

    public async Task CancelAsync(
        VoiceAssistantTurnReceipt receipt,
        CancellationToken cancellationToken)
    {
        RememberCanceled(receipt.LocalMessageId);
        _responses.TryRemove(receipt.LocalMessageId, out _);
        await _provider.CancelVoiceAssistantTurnAsync(receipt, cancellationToken).ConfigureAwait(false);
    }

    public bool IsResponseForTurn(
        VoiceAssistantTurnReceipt receipt,
        OpenClawNotification notification)
    {
        if (!_responses.TryGetValue(receipt.LocalMessageId, out var response) ||
            !string.Equals(response.SessionKey, notification.SessionKey, StringComparison.Ordinal))
        {
            return false;
        }

        bool matches;
        if (!string.IsNullOrWhiteSpace(response.GatewayMessageId) &&
            response.GatewaySequence is { } gatewaySequence)
        {
            matches = string.Equals(
                    response.GatewayMessageId,
                    notification.OpenClawId,
                    StringComparison.Ordinal) &&
                gatewaySequence == notification.OpenClawSeq;
        }
        else if (!string.IsNullOrWhiteSpace(notification.OpenClawId) ||
                 notification.OpenClawSeq is not null)
        {
            matches = false;
        }
        else
        {
            matches = string.Equals(
                response.ResponseText,
                notification.FullMessage ?? notification.Message,
                StringComparison.Ordinal);
        }

        if (matches)
            _responses.TryRemove(receipt.LocalMessageId, out _);
        return matches;
    }

    private void OnResponseObserved(OpenClawChatDataProvider.VoiceAssistantResponseIdentity response)
    {
        lock (_canceledGate)
        {
            if (_canceled.Contains(response.LocalMessageId))
                return;
        }

        _responses[response.LocalMessageId] = new ResponseIdentity(
            response.SessionKey,
            response.GatewayMessageId,
            response.GatewaySequence,
            response.ResponseText);
    }

    private void OnProviderChanged(object? sender, ChatDataChangedEventArgs args)
    {
        var readySessionKey = _provider.GetVoiceAssistantReadySessionKey();
        lock (_readinessGate)
        {
            if (string.Equals(_lastReadySessionKey, readySessionKey, StringComparison.Ordinal))
                return;

            _lastReadySessionKey = readySessionKey;
        }

        ReadinessChanged?.Invoke();
    }

    private void RememberCanceled(string localMessageId)
    {
        lock (_canceledGate)
        {
            if (!_canceled.Add(localMessageId))
                return;

            _canceledOrder.Enqueue(localMessageId);
            while (_canceledOrder.Count > CanceledIdentityLimit)
                _canceled.Remove(_canceledOrder.Dequeue());
        }
    }

    public void Dispose()
    {
        _provider.Changed -= OnProviderChanged;
        _provider.VoiceAssistantResponseObserved -= OnResponseObserved;
        _responses.Clear();
        lock (_canceledGate)
        {
            _canceled.Clear();
            _canceledOrder.Clear();
        }
    }

    private sealed record ResponseIdentity(
        string SessionKey,
        string? GatewayMessageId,
        int? GatewaySequence,
        string ResponseText);
}
