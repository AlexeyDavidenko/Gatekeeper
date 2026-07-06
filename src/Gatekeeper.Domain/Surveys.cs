namespace Gatekeeper.Domain;

/// <summary>
/// A live, editable survey question. Admins edit text, reorder (Position) and soft-delete
/// (IsActive). History is preserved on the answer side via snapshots, not here.
/// ConfigJson holds type-specific settings (choice options, captcha config, validation).
/// </summary>
public sealed class Question : AggregateRoot
{
    public int Position { get; private set; }
    public QuestionType Type { get; private set; }
    public string PromptText { get; private set; } = default!;
    public bool IsRequired { get; private set; }
    public string? ConfigJson { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Question() { }

    public static Question Create(
        int position, QuestionType type, string promptText, bool isRequired, string? configJson, DateTimeOffset now) => new()
    {
        Position = position,
        Type = type,
        PromptText = promptText,
        IsRequired = isRequired,
        ConfigJson = configJson,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Edit(string promptText, bool isRequired, string? configJson, DateTimeOffset now)
    {
        PromptText = promptText;
        IsRequired = isRequired;
        ConfigJson = configJson;
        UpdatedAt = now;
    }

    public void MoveTo(int position, DateTimeOffset now)
    {
        Position = position;
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }
}
