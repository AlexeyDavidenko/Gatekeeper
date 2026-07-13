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
    public string? PromptTextEn { get; private set; }
    public bool IsRequired { get; private set; }
    public string? ConfigJson { get; private set; }
    public string? ConfigJsonEn { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Question() { }

    public static Question Create(
        int position, QuestionType type, string promptText, bool isRequired, string? configJson, DateTimeOffset now,
        string? promptTextEn = null, string? configJsonEn = null) => new()
    {
        Position = position,
        Type = type,
        PromptText = promptText,
        PromptTextEn = promptTextEn,
        IsRequired = isRequired,
        ConfigJson = configJson,
        ConfigJsonEn = configJsonEn,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Edit(
        QuestionType type, string promptText, bool isRequired, string? configJson, DateTimeOffset now,
        string? promptTextEn = null, string? configJsonEn = null)
    {
        Type = type;
        PromptText = promptText;
        PromptTextEn = promptTextEn;
        IsRequired = isRequired;
        ConfigJson = configJson;
        ConfigJsonEn = configJsonEn;
        UpdatedAt = now;
    }

    /// <summary>Applicant-facing text for the resolved bot language ("ru"/"en") — falls back to the
    /// (always-present) Russian text when no English variant has been authored yet.</summary>
    public string PromptTextFor(string lang) =>
        lang == "en" && !string.IsNullOrWhiteSpace(PromptTextEn) ? PromptTextEn : PromptText;

    /// <summary>Applicant-facing choice options for the resolved bot language. All-or-nothing: a
    /// partially-translated option list would desync the positional index SingleChoice/MultiChoice
    /// answers are keyed by (see SubmitAnswerHandler), so any count mismatch falls back to the
    /// Russian list entirely rather than risk misaligned labels.</summary>
    public IReadOnlyList<string>? OptionsFor(string lang)
    {
        var ru = ChoiceOptions.ParseQuestionOptions(ConfigJson);
        if (lang != "en") return ru;
        var en = ChoiceOptions.ParseQuestionOptions(ConfigJsonEn);
        return en is { Count: > 0 } && en.Count == (ru?.Count ?? 0) ? en : ru;
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

    public void Activate(DateTimeOffset now)
    {
        IsActive = true;
        UpdatedAt = now;
    }
}

/// <summary>
/// (De)serializes the two JSON shapes that live in Question.ConfigJson (the available option
/// labels for SingleChoice/MultiChoice) and Answer.OptionsJson (the indices the applicant picked
/// into that list) — kept next to Question since both are only meaningful in terms of it.
/// </summary>
public static class ChoiceOptions
{
    public static string? SerializeQuestionOptions(IReadOnlyList<string>? options) =>
        options is not { Count: > 0 } ? null : System.Text.Json.JsonSerializer.Serialize(options);

    public static IReadOnlyList<string>? ParseQuestionOptions(string? configJson) =>
        string.IsNullOrEmpty(configJson) ? null : System.Text.Json.JsonSerializer.Deserialize<List<string>>(configJson);

    public static string SerializeSelectedIndices(IReadOnlyList<int> indices) =>
        System.Text.Json.JsonSerializer.Serialize(indices);

    public static IReadOnlyList<int> ParseSelectedIndices(string? optionsJson) =>
        string.IsNullOrEmpty(optionsJson)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<int>>(optionsJson) ?? [];
}
