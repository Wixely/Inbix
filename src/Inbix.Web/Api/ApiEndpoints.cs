using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Core.Validation;
using Inbix.Web.Diagnostics;
using Microsoft.Extensions.Options;

namespace Inbix.Web.Api;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapInbixApi(this IEndpointRouteBuilder app)
    {
        // Exempt from the cookie fallback policy; ApiKeyMiddleware enforces cookie-or-key instead.
        var api = app.MapGroup("/api").WithTags("Inbix").AllowAnonymous();

        // --- Aliases ---
        api.MapGet("/aliases", async (IAliasRepository repo, CancellationToken ct) =>
            Results.Ok((await repo.ListAsync(ct)).Select(AliasDto.From)));

        api.MapGet("/aliases/{id:long}", async (long id, IAliasRepository repo, CancellationToken ct) =>
            await repo.GetByIdAsync(id, ct) is { } a ? Results.Ok(AliasDto.From(a)) : Results.NotFound());

        api.MapPost("/aliases", async (
            CreateAliasRequest req, IAliasRepository repo, IAuditRepository audit,
            IOptions<InbixOptions> options, CancellationToken ct) =>
        {
            var error = AliasRules.ValidateLocalPart(req.LocalPart);
            if (error is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["localPart"] = [error] });

            var domains = options.Value.Domains;
            var domain = (req.Domain ?? domains.FirstOrDefault())?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(domain))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["domain"] = ["No domain supplied and no default domain configured."] });

            if (domains.Length > 0 && !domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["domain"] = [$"Domain '{domain}' is not served by this instance."] });

            var localPart = AliasRules.Normalize(req.LocalPart);
            try
            {
                var created = await repo.CreateAsync(localPart, domain, req.Notes, ct);
                await audit.WriteAsync(new AuditEntry { Action = "alias.create", TargetType = "alias", TargetId = created.Id.ToString(), CreatedAt = DateTimeOffset.UtcNow }, ct);
                return Results.Created($"/api/aliases/{created.Id}", AliasDto.From(created));
            }
            catch (Exception ex) when (ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict($"Alias {localPart}@{domain} already exists.");
            }
        });

        api.MapPatch("/aliases/{id:long}", async (
            long id, UpdateAliasRequest req, IAliasRepository repo, IAuditRepository audit, CancellationToken ct) =>
        {
            var updated = await repo.UpdateAsync(id, req.Enabled, req.Notes, ct);
            if (updated is null) return Results.NotFound();
            await audit.WriteAsync(new AuditEntry { Action = "alias.update", TargetType = "alias", TargetId = id.ToString(), CreatedAt = DateTimeOffset.UtcNow }, ct);
            return Results.Ok(AliasDto.From(updated));
        });

        api.MapGet("/aliases/{id:long}/messages", async (
            long id, IMessageRepository repo, int? limit, int? offset, CancellationToken ct) =>
        {
            var messages = await repo.ListByAliasAsync(id, Math.Clamp(limit ?? 50, 1, 200), Math.Max(0, offset ?? 0), ct);
            return Results.Ok(messages.Select(MessageSummaryDto.From));
        });

        // --- Messages ---
        api.MapGet("/messages/{id:long}", async (long id, IMessageRepository repo, CancellationToken ct) =>
        {
            var message = await repo.GetByIdAsync(id, ct);
            if (message is null) return Results.NotFound();
            var body = await repo.GetBodyAsync(id, ct);
            var attachments = await repo.ListAttachmentsAsync(id, ct);
            return Results.Ok(new MessageDetailDto(
                MessageSummaryDto.From(message), body?.TextBody, body?.HtmlBody, message.ParseError,
                attachments.Select(AttachmentDto.From).ToList()));
        });

        api.MapGet("/messages/{id:long}/raw", async (long id, IMessageRepository repo, IRawMessageStore store, CancellationToken ct) =>
        {
            var message = await repo.GetByIdAsync(id, ct);
            if (message is null || string.IsNullOrEmpty(message.RawStoragePath)) return Results.NotFound();
            var stream = await store.OpenReadAsync(message.RawStoragePath, ct);
            return Results.Stream(stream, "message/rfc822", $"message-{id}.eml");
        });

        api.MapGet("/messages/{id:long}/attachments", async (long id, IMessageRepository repo, CancellationToken ct) =>
            Results.Ok((await repo.ListAttachmentsAsync(id, ct)).Select(AttachmentDto.From)));

        api.MapGet("/attachments/{attachmentId:long}/content", async (
            long attachmentId, IMessageRepository repo, IRawMessageStore store, CancellationToken ct) =>
        {
            var attachment = await repo.GetAttachmentAsync(attachmentId, ct);
            if (attachment is null) return Results.NotFound();
            var stream = await store.OpenReadAsync(attachment.StoragePath, ct);
            return Results.Stream(stream, attachment.ContentType ?? "application/octet-stream", attachment.Filename);
        });

        // --- Audit ---
        api.MapGet("/audit", async (IAuditRepository repo, int? limit, int? offset, CancellationToken ct) =>
            Results.Ok(await repo.ListAsync(Math.Clamp(limit ?? 100, 1, 500), Math.Max(0, offset ?? 0), ct)));

        // --- Diagnostics ---
        api.MapGet("/diagnostics", async (DiagnosticsService diagnostics, CancellationToken ct) =>
            Results.Ok(await diagnostics.RunAllAsync(ct)));

        // --- Backups ---
        api.MapGet("/backups", (IBackupService backup) => Results.Ok(backup.ListBackups()));

        api.MapPost("/backups", async (IBackupService backup, IAuditRepository audit, CancellationToken ct) =>
        {
            var info = await backup.CreateBackupAsync(ct);
            await audit.WriteAsync(new AuditEntry
            {
                Action = "backup.create", TargetType = "backup", TargetId = info.FileName,
                Actor = "api", CreatedAt = DateTimeOffset.UtcNow
            }, ct);
            return Results.Ok(info);
        });

        return app;
    }
}
