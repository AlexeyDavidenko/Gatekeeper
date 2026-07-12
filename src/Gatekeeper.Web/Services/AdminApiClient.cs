namespace Gatekeeper.Web.Services;

using System.Net;
using System.Net.Http.Json;
using Gatekeeper.Contracts;

public enum DecisionOutcome { Applied, AlreadyDecided }

/// <summary>The web's only door to data. Every call carries the configured tenant id.</summary>
public sealed class AdminApiClient(HttpClient http, IConfiguration config)
{
    private long TenantId => long.Parse(config["Tenant:Id"]!);

    public async Task<IReadOnlyList<ApplicationSummary>> GetPendingAsync(CancellationToken ct = default) =>
        await GetByStatusAsync("AwaitingReview", ct);

    public async Task<IReadOnlyList<ApplicationSummary>> GetByStatusAsync(string status, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Get($"/applications?status={status}"), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<List<ApplicationSummary>>(ct) ?? [];
    }

    public async Task<ApplicationCard?> GetCardAsync(long id, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Get($"/applications/{id}"), ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<ApplicationCard>(ct);
    }

    public async Task<DashboardSummary> GetDashboardAsync(CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Get("/dashboard"), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<DashboardSummary>(ct)
            ?? new DashboardSummary(0, 0, 0, 0, 0, 0, 0, 0, []);
    }

    public async Task<IReadOnlyList<ModerationLogEntry>> GetAuditLogAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Get($"/moderation?includeArchived={includeArchived}"), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<List<ModerationLogEntry>>(ct) ?? [];
    }

    public async Task<int> ArchiveModerationActionsAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Post("/moderation/archive", new ArchiveModerationActionsRequest(olderThan)), ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<ArchiveModerationActionsResponse>(ct);
        return body?.ArchivedCount ?? 0;
    }

    public async Task UnarchiveModerationActionAsync(long id, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Post($"/moderation/{id}/unarchive"), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task RecordVisitAsync(RecordSiteVisitRequest request, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Post("/site-visits", request), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<SiteVisitDto>> GetSiteVisitsAsync(CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Get("/site-visits"), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<List<SiteVisitDto>>(ct) ?? [];
    }

    public async Task<IReadOnlyList<QuestionDto>> GetQuestionsAsync(CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Get("/questions"), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<List<QuestionDto>>(ct) ?? [];
    }

    public async Task<QuestionDto?> GetQuestionAsync(long id, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Get($"/questions/{id}"), ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<QuestionDto>(ct);
    }

    public async Task CreateQuestionAsync(
        string type, string promptText, bool isRequired, IReadOnlyList<string>? options, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(
            Post("/questions", new CreateQuestionRequest(type, promptText, isRequired, options)), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task EditQuestionAsync(
        long id, string type, string promptText, bool isRequired, IReadOnlyList<string>? options, bool isActive,
        CancellationToken ct = default)
    {
        var msg = new HttpRequestMessage(HttpMethod.Put, $"/questions/{id}")
        {
            Headers = { { "X-Tenant-Id", TenantId.ToString() } },
            Content = JsonContent.Create(new EditQuestionRequest(type, promptText, isRequired, options, isActive)),
        };
        using var res = await http.SendAsync(msg, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task MoveQuestionAsync(long id, bool up, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Post($"/questions/{id}/{(up ? "move-up" : "move-down")}"), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task DeactivateQuestionAsync(long id, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Post($"/questions/{id}/deactivate"), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<DecisionOutcome> DecideAsync(
        long id, uint rowVersion, bool approve, string? reason, long actingUserId, string? actingUserName,
        CancellationToken ct = default)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, $"/applications/{id}/decision")
        {
            Headers = { { "X-Tenant-Id", TenantId.ToString() } },
            Content = JsonContent.Create(new DecideRequest(approve, reason, actingUserId, actingUserName, "Web")),
        };
        msg.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        using var res = await http.SendAsync(msg, ct);
        return res.StatusCode switch
        {
            HttpStatusCode.NoContent => DecisionOutcome.Applied,
            HttpStatusCode.Conflict => DecisionOutcome.AlreadyDecided,  // Telegram-side already decided
            _ => throw new HttpRequestException($"Decision failed: {(int)res.StatusCode}"),
        };
    }

    private HttpRequestMessage Get(string path) =>
        new(HttpMethod.Get, path) { Headers = { { "X-Tenant-Id", TenantId.ToString() } } };

    private HttpRequestMessage Post(string path, object? body = null) => new(HttpMethod.Post, path)
    {
        Headers = { { "X-Tenant-Id", TenantId.ToString() } },
        Content = body is null ? null : JsonContent.Create(body),
    };
}
