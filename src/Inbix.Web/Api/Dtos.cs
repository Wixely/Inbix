using Inbix.Core.Domain;

namespace Inbix.Web.Api;

public sealed record CreateAliasRequest(string LocalPart, string? Domain, string? Notes);
public sealed record UpdateAliasRequest(bool? Enabled, string? Notes);

public sealed record AliasDto(long Id, string LocalPart, string Domain, string Address, bool Enabled,
    DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt, string? Notes, long? IdentityId)
{
    public static AliasDto From(Alias a) =>
        new(a.Id, a.LocalPart, a.Domain, a.Address, a.Enabled, a.CreatedAt, a.DisabledAt, a.Notes, a.IdentityId);
}

public sealed record MessageSummaryDto(long Id, long AliasId, string Recipient, string? Sender, string? Subject,
    DateTimeOffset ReceivedAt, long SizeBytes, bool Parsed)
{
    public static MessageSummaryDto From(Message m) =>
        new(m.Id, m.AliasId, m.Recipient, m.Sender, m.Subject, m.ReceivedAt, m.SizeBytes, m.Parsed);
}

public sealed record AttachmentDto(long Id, string? Filename, string? ContentType, long? SizeBytes, string? Sha256)
{
    public static AttachmentDto From(Attachment a) =>
        new(a.Id, a.Filename, a.ContentType, a.SizeBytes, a.Sha256);
}

public sealed record MessageDetailDto(MessageSummaryDto Message, string? TextBody, string? HtmlBody,
    string? ParseError, IReadOnlyList<AttachmentDto> Attachments);

// --- Blacklist rules + junk ---

public sealed record CreateRuleRequest(string? Name, RuleTarget Target, RuleMatch MatchType,
    string Pattern, RuleAction Action, bool? Enabled);

public sealed record UpdateRuleRequest(string? Name, RuleTarget Target, RuleMatch MatchType,
    string Pattern, RuleAction Action, bool Enabled);

public sealed record SweepPreviewRequest(RuleTarget Target, RuleMatch MatchType, string Pattern);

public sealed record RuleDto(long Id, string? Name, RuleTarget Target, RuleMatch MatchType,
    string Pattern, RuleAction Action, bool Enabled, DateTimeOffset CreatedAt)
{
    public static RuleDto From(BlacklistRule r) =>
        new(r.Id, r.Name, r.Target, r.MatchType, r.Pattern, r.Action, r.Enabled, r.CreatedAt);
}

public sealed record SweepCandidateDto(long Id, long AliasId, string? Sender, string Recipient,
    string? Subject, DateTimeOffset ReceivedAt, bool Parsed)
{
    public static SweepCandidateDto From(SweepCandidate c) =>
        new(c.Id, c.AliasId, c.Sender, c.Recipient, c.Subject, c.ReceivedAt, c.Parsed);
}

public sealed record SweepPreviewDto(int Count, IReadOnlyList<SweepCandidateDto> Sample);

public sealed record JunkItemDto(long Id, string? Sender, string? Subject, string Recipient,
    DateTimeOffset ReceivedAt, bool Parsed, DateTimeOffset? JunkedAt, bool JunkManual,
    long? JunkRuleId, string? JunkRuleName)
{
    public static JunkItemDto From(JunkItem j) =>
        new(j.Id, j.Sender, j.Subject, j.Recipient, j.ReceivedAt, j.Parsed,
            j.JunkedAt, j.JunkManual, j.JunkRuleId, j.JunkRuleName);
}

// --- Identities ---

public sealed record GenerateIdentityRequest(bool? Uk, bool? Us);

/// <summary>Body for linking an alias to an identity (null IdentityId unlinks the alias).</summary>
public sealed record LinkAliasRequest(long? IdentityId);

/// <summary>Body for create (POST) and update (PATCH) — same editable shape.</summary>
public sealed record SaveIdentityRequest(
    string Country, string? Title, string? Gender,
    string FirstName, string LastName, string Username, string Password,
    DateOnly DateOfBirth, string? Email, string? Phone,
    string Street, string City, string? StateCounty, string Postcode,
    string? SecurityQuestion, string? SecurityAnswer, string? Notes)
{
    public Identity ToDomain(long id = 0) => new()
    {
        Id = id, Country = Country, Title = Title, Gender = Gender,
        FirstName = FirstName, LastName = LastName, Username = Username, Password = Password,
        DateOfBirth = DateOfBirth, Email = Email, Phone = Phone,
        Street = Street, City = City, StateCounty = StateCounty, Postcode = Postcode,
        SecurityQuestion = SecurityQuestion, SecurityAnswer = SecurityAnswer, Notes = Notes
    };
}

public sealed record IdentityDto(
    long Id, string Country, string? Title, string? Gender,
    string FirstName, string LastName, string FullName, string Username, string Password,
    DateOnly DateOfBirth, int AgeYears, string? Email, string? Phone,
    string Street, string City, string? StateCounty, string Postcode, string FullAddress,
    string? SecurityQuestion, string? SecurityAnswer, string? Notes, DateTimeOffset CreatedAt)
{
    public static IdentityDto From(Identity i) => new(
        i.Id, i.Country, i.Title, i.Gender,
        i.FirstName, i.LastName, i.FullName, i.Username, i.Password,
        i.DateOfBirth, i.AgeYears, i.Email, i.Phone,
        i.Street, i.City, i.StateCounty, i.Postcode, i.FullAddress,
        i.SecurityQuestion, i.SecurityAnswer, i.Notes, i.CreatedAt);
}
