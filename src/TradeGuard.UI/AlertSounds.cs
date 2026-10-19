namespace TradeGuard.UI;

/// <summary>
/// Sons do TradeGuard (arquivos de C:\Windows\Media). A tela só escolhe e pede para ouvir; quem toca, com o volume
/// próprio do app, é o host desktop, que assina <see cref="PreviewRequested"/>.
/// </summary>
public static class AlertSounds
{
    public const string Connection = "Windows Notify System Generic.wav";

    public static readonly IReadOnlyList<(string Label, string File)> Catalog =
    [
        ("Sem som", ""),
        ("Mensagem", "Windows Notify Messaging.wav"),
        ("Toque suave", "Windows Proximity Notification.wav"),
        ("Calendário", "Windows Notify Calendar.wav"),
        ("E-mail", "Windows Notify Email.wav"),
        ("Ding", "Windows Ding.wav"),
        ("Acorde", "chord.wav"),
        ("Sinos", "chimes.wav"),
        ("Tada", "tada.wav")
    ];

    public static event Action<string, int>? PreviewRequested;

    public static void Preview(string file, int volumePercent) => PreviewRequested?.Invoke(file, volumePercent);
}
