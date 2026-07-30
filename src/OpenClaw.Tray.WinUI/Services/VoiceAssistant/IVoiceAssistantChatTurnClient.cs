using OpenClaw.Shared;

namespace OpenClawTray.Services.VoiceAssistant;

public enum VoiceAssistantSendDisposition
{
    Direct,
    Queued
}

public sealed record VoiceAssistantTurnReceipt(
    VoiceAssistantSendDisposition Disposition,
    string SessionKey,
    string LocalMessageId,
    string SendRunId,
    int? PreSendSequence);

public interface IVoiceAssistantChatTurnClient
{
    string? GetReadySessionKey();

    Task<VoiceAssistantTurnReceipt> SendAsync(
        string sessionKey,
        string request,
        CancellationToken cancellationToken);

    Task CancelAsync(VoiceAssistantTurnReceipt receipt, CancellationToken cancellationToken);

    bool IsResponseForTurn(VoiceAssistantTurnReceipt receipt, OpenClawNotification notification);
}

public interface IVoiceAssistantSpeaker
{
    Task SpeakAsync(string text, CancellationToken cancellationToken);
}
