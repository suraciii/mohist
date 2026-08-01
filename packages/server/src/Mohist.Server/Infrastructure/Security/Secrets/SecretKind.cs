namespace Mohist.Server.Infrastructure.Security.Secrets;

public enum SecretKind
{
    AppToken = 0,
    BotToken = 1,
    WebhookSecret = 2,
}

public static class SecretKinds
{
    public const string AppToken = "appToken";
    public const string BotToken = "botToken";
    public const string WebhookSecret = "webhookSecret";

    public static string ToWire(SecretKind kind) => kind switch
    {
        SecretKind.AppToken => AppToken,
        SecretKind.BotToken => BotToken,
        SecretKind.WebhookSecret => WebhookSecret,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool TryParseWire(string value, out SecretKind kind)
    {
        switch (value)
        {
            case AppToken:
                kind = SecretKind.AppToken;
                return true;
            case BotToken:
                kind = SecretKind.BotToken;
                return true;
            case WebhookSecret:
                kind = SecretKind.WebhookSecret;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
