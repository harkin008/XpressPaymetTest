namespace XpressTest.AuthService.ViewModels;

public record ApiKeyListItemViewModel(long Id, string Name, string Prefix, DateTime CreatedAtUtc, DateTime? RevokedAtUtc);
