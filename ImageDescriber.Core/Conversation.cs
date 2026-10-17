namespace ImageDescriber.Core;

/// <summary>
/// One image plus the back-and-forth about it. Follow-up questions resend the
/// whole history (including the image) so the model remembers what it saw.
/// </summary>
public sealed class Conversation
{
    private readonly List<ChatMessage> _messages = new();
    private readonly OllamaClient _client;
    private readonly string _model;

    public string Source { get; }

    public Conversation(OllamaClient client, string model, string imageBase64, string source,
        DescriptionStyle style = DescriptionStyle.Sensual)
    {
        _client = client;
        _model = model;
        Source = source;
        _messages.Add(new ChatMessage("system", PromptText.For(style)));
        _messages.Add(new ChatMessage("user", PromptText.FirstQuestion, new[] { imageBase64 }));
    }

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Gets the first description. Call once, right after creating the conversation.</summary>
    public Task<string> DescribeAsync(CancellationToken ct = default) => SendAsync(ct);

    public Task<string> AskAsync(string question, CancellationToken ct = default)
    {
        _messages.Add(new ChatMessage("user", question.Trim()));
        return SendAsync(ct);
    }

    private async Task<string> SendAsync(CancellationToken ct)
    {
        try
        {
            var reply = await _client.ChatAsync(_model, _messages, ct);
            if (string.IsNullOrWhiteSpace(reply)) reply = "The model gave an empty answer. Try again.";
            _messages.Add(new ChatMessage("assistant", reply));
            return reply;
        }
        catch
        {
            // Drop an unanswered question so the history stays valid for the next try.
            if (_messages.Count > 2 && _messages[^1].Role == "user") _messages.RemoveAt(_messages.Count - 1);
            throw;
        }
    }
}
