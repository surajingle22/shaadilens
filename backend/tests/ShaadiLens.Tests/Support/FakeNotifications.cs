using ShaadiLens.Application.Abstractions;

namespace ShaadiLens.Tests.Support;

/// <summary>Records push messages instead of calling Google/Mozilla push services.</summary>
public sealed class FakePushSender : IPushSender
{
    public List<(string Subscription, PushMessage Message)> Sent { get; } = [];
    public PushResult NextResult { get; set; } = PushResult.Sent;
    public bool IsConfigured => true;
    public string PublicKey => "BFakePublicVapidKeyForTests";

    public Task<PushResult> SendAsync(string subscriptionJson, PushMessage message, CancellationToken ct = default)
    {
        if (NextResult == PushResult.Sent) Sent.Add((subscriptionJson, message));
        return Task.FromResult(NextResult);
    }
}

/// <summary>Records emails instead of talking SMTP.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public List<(string To, string Subject, string Text)> Sent { get; } = [];
    public bool IsConfigured => true;

    public Task SendAsync(string to, string subject, string textBody, string htmlBody, CancellationToken ct = default)
    {
        Sent.Add((to, subject, textBody));
        return Task.CompletedTask;
    }
}
