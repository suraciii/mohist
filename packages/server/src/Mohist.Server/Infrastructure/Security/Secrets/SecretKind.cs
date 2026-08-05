namespace Mohist.Server.Infrastructure.Security.Secrets;

public enum SecretKind
{
    AppToken = 0,
    BotToken = 1,
    WebhookSecret = 2,
    ClientSecret = 3,
    SigningSecret = 4,
    ConfigurationAccessToken = 5,
    ConfigurationRefreshToken = 6,
    PreviousBotToken = 7,
    PreviousAppToken = 8,
    CandidateBotToken = 9,
    CandidateAppToken = 10,
}

public static class SecretKinds
{
    public const string AppToken = "appToken";
    public const string BotToken = "botToken";
    public const string WebhookSecret = "webhookSecret";
    public const string ClientSecret = "clientSecret";
    public const string SigningSecret = "signingSecret";
    public const string ConfigurationAccessToken = "configurationAccessToken";
    public const string ConfigurationRefreshToken = "configurationRefreshToken";
    public const string PreviousBotToken = "previousBotToken";
    public const string PreviousAppToken = "previousAppToken";
    public const string CandidateBotToken = "candidateBotToken";
    public const string CandidateAppToken = "candidateAppToken";

    public static string ToWire(SecretKind kind) => kind switch
    {
        SecretKind.AppToken => AppToken,
        SecretKind.BotToken => BotToken,
        SecretKind.WebhookSecret => WebhookSecret,
        SecretKind.ClientSecret => ClientSecret,
        SecretKind.SigningSecret => SigningSecret,
        SecretKind.ConfigurationAccessToken => ConfigurationAccessToken,
        SecretKind.ConfigurationRefreshToken => ConfigurationRefreshToken,
        SecretKind.PreviousBotToken => PreviousBotToken,
        SecretKind.PreviousAppToken => PreviousAppToken,
        SecretKind.CandidateBotToken => CandidateBotToken,
        SecretKind.CandidateAppToken => CandidateAppToken,
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
            case ClientSecret:
                kind = SecretKind.ClientSecret;
                return true;
            case SigningSecret:
                kind = SecretKind.SigningSecret;
                return true;
            case ConfigurationAccessToken:
                kind = SecretKind.ConfigurationAccessToken;
                return true;
            case ConfigurationRefreshToken:
                kind = SecretKind.ConfigurationRefreshToken;
                return true;
            case PreviousBotToken:
                kind = SecretKind.PreviousBotToken;
                return true;
            case PreviousAppToken:
                kind = SecretKind.PreviousAppToken;
                return true;
            case CandidateBotToken:
                kind = SecretKind.CandidateBotToken;
                return true;
            case CandidateAppToken:
                kind = SecretKind.CandidateAppToken;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
