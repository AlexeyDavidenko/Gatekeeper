namespace Gatekeeper.Bot;

using System.Net;
using System.Net.Http.Json;
using Contracts;

/// <summary>
/// The bot's only door to data. Tenant-scoped routes carry X-Tenant-Id; /internal routes don't.
/// The internal API key is set once as a default header (see Program.cs).
/// </summary>
public sealed class GatekeeperApiClient(HttpClient http) : IGatekeeperApiClient
{
    public async Task<NextQuestionDto> CreateApplicationAsync(long tenantId, CreateApplicationRequest request, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Build(HttpMethod.Post, "/applications", tenantId, request), ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<NextQuestionDto>(ct))!;
    }

    public async Task<NextQuestionDto?> SubmitAnswerAsync(long tenantId, SubmitAnswerRequest request, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Build(HttpMethod.Post, "/applications/answers", tenantId, request), ct);
        if (res.StatusCode == HttpStatusCode.Conflict) return null;   // survey not started yet — see StartSurveyAsync
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<NextQuestionDto>(ct))!;
    }

    public async Task DecideAsync(long tenantId, long applicationId, uint rowVersion, DecideRequest request, CancellationToken ct = default)
    {
        var msg = Build(HttpMethod.Post, $"/applications/{applicationId}/decision", tenantId, request);
        msg.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");
        using var res = await http.SendAsync(msg, ct);
        // 409 = a Telegram-side decision already won the race; benign by design.
        if (res.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.Conflict))
            res.EnsureSuccessStatusCode();
    }

    public async Task CancelApplicationAsync(long tenantId, long applicationId, CancelApplicationRequest request, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Build(HttpMethod.Post, $"/applications/{applicationId}/cancel", tenantId, request), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<long> ModerateAsync(long tenantId, long telegramUserId, ModerationRequest request, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Build(HttpMethod.Post, $"/users/{telegramUserId}/moderation", tenantId, request), ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<ModerateResponse>(ct))!.Id;
    }

    public async Task HandleAdminCallbackAsync(long tenantId, AdminCallbackRequest request, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Build(HttpMethod.Post, "/applications/callback", tenantId, request), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<(long TenantId, string Role)?> ResolveTenantByChatAsync(long chatId, CancellationToken ct = default)
    {
        using var res = await http.GetAsync($"/internal/tenants/resolve?chatId={chatId}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        var dto = await res.Content.ReadFromJsonAsync<TenantResolution>(ct);
        return dto is null ? null : (dto.TenantId, dto.Role);
    }

    public async Task<IReadOnlyList<PendingCommand>> GetPendingTelegramCommandsAsync(int batch, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<PendingCommand>>($"/internal/telegram-commands/pending?batch={batch}", ct) ?? [];

    public async Task<IReadOnlyList<InProgressApplicant>> GetInProgressApplicantsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<InProgressApplicant>>("/internal/applications/in-progress", ct) ?? [];

    public async Task AckTelegramCommandAsync(long tenantId, long commandId, CommandResult result, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync($"/internal/telegram-commands/{tenantId}/{commandId}/result", result, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task SetUserLanguageAsync(long tenantId, long telegramUserId, string languageCode, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(
            Build(HttpMethod.Post, $"/users/{telegramUserId}/language", tenantId, new SetLanguageRequest(languageCode)), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<NextQuestionDto> StartSurveyAsync(long tenantId, long telegramUserId, CancellationToken ct = default)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, $"/users/{telegramUserId}/start-survey")
        {
            Headers = { { "X-Tenant-Id", tenantId.ToString() } },
        };
        using var res = await http.SendAsync(msg, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<NextQuestionDto>(ct))!;
    }

    public async Task<DashboardSummary> GetDashboardAsync(long tenantId, CancellationToken ct = default)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, "/dashboard") { Headers = { { "X-Tenant-Id", tenantId.ToString() } } };
        using var res = await http.SendAsync(msg, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<DashboardSummary>(ct))!;
    }

    public async Task<IReadOnlyList<ApplicationSummary>> GetApplicationsByStatusAsync(
        long tenantId, string status, CancellationToken ct = default)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, $"/applications?status={status}")
        {
            Headers = { { "X-Tenant-Id", tenantId.ToString() } },
        };
        using var res = await http.SendAsync(msg, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<List<ApplicationSummary>>(ct) ?? [];
    }

    public async Task<IReadOnlyList<ApplicationSummary>> SearchApplicationsAsync(
        long tenantId, string query, CancellationToken ct = default)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, $"/applications/search?q={Uri.EscapeDataString(query)}")
        {
            Headers = { { "X-Tenant-Id", tenantId.ToString() } },
        };
        using var res = await http.SendAsync(msg, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<List<ApplicationSummary>>(ct) ?? [];
    }

    public async Task<LatestApplicationStatusDto?> GetLatestApplicationStatusAsync(long telegramUserId, CancellationToken ct = default)
    {
        using var res = await http.GetAsync($"/internal/applications/by-user/{telegramUserId}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<LatestApplicationStatusDto>(ct);
    }

    private static HttpRequestMessage Build<T>(HttpMethod method, string path, long tenantId, T body) =>
        new(method, path)
        {
            Headers = { { "X-Tenant-Id", tenantId.ToString() } },
            Content = JsonContent.Create(body),
        };

    private sealed record TenantResolution(long TenantId, string Role);
}
