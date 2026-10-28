namespace XpressTest.Shared.Auth;

/// <summary>JWT claim names issued by AuthService (unmapped, as they appear in the token).</summary>
public static class XpressClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string Scope = "scope";
    public const string ClientId = "client_id";
    public const string ApiKeyId = "api_key_id";
}

public static class XpressRoles
{
    public const string User = "User";
    public const string Admin = "Admin";
    public const string SuperAdmin = "SuperAdmin";
}

/// <summary>Each service accepts only tokens whose "aud" names it.</summary>
public static class XpressAudiences
{
    public const string AuthService = "auth-service";
    public const string OrderService = "order-service";
    public const string NotificationService = "notification-service";
}

public static class XpressScopes
{
    /// <summary>Lets a service ask NotificationService to send notifications.</summary>
    public const string NotificationsSend = "notifications.send";
}

/// <summary>RFC 9068 "typ" header for access tokens, so other JWT kinds can't be replayed as one.</summary>
public static class XpressTokenTypes
{
    public const string AccessToken = "at+jwt";
}
