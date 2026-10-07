using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WolfSpeak;

/// <summary>A person found on the network, shown on the home screen.</summary>
public sealed class PeerVm(Peer peer) : INotifyPropertyChanged
{
    static readonly (Color A, Color B)[] AvatarColors =
    [
        (Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0x8B, 0x5C, 0xF6)),
        (Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x3B, 0x82, 0xF6)),
        (Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0x05, 0x96, 0x69)),
        (Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xF9, 0x73, 0x16)),
        (Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x63, 0x66, 0xF1)),
        (Color.FromRgb(0xFB, 0x71, 0x85), Color.FromRgb(0xE1, 0x1D, 0x48)),
        (Color.FromRgb(0x2D, 0xD4, 0xBF), Color.FromRgb(0x0E, 0xA5, 0xE9)),
    ];

    public Peer Peer { get; } = peer;
    public uint Id => Peer.Id;

    string name = "";
    string initials = "";
    Brush avatarBrush = Brushes.Gray;
    string address = "";
    string versionWarning = "";

    public string Name { get => name; private set => Set(ref name, value); }
    public string Initials { get => initials; private set => Set(ref initials, value); }
    public Brush AvatarBrush { get => avatarBrush; private set => Set(ref avatarBrush, value); }
    public string Address { get => address; private set => Set(ref address, value); }
    /// <summary>Empty when they run the same WolfSpeak version as us.</summary>
    public string VersionWarning
    {
        get => versionWarning;
        private set
        {
            if (versionWarning == value) return;
            Set(ref versionWarning, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VersionWarningVisibility)));
        }
    }
    public System.Windows.Visibility VersionWarningVisibility =>
        versionWarning.Length == 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public void Update()
    {
        if (Peer.Name != name)
        {
            Name = Peer.Name;
            Initials = MakeInitials(name);
            AvatarBrush = MakeAvatar(name);
        }
        Address = Peer.EndPoint.Address.ToString();
        VersionWarning = !Peer.HelloSeen || Peer.VersionMatches ? ""
            : $"Different version ({Peer.Version ?? "older"}, you: {VoiceEngine.AppVersion}) · update to call";
    }

    public static string MakeInitials(string n)
    {
        var parts = n.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) return $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}";
        return n.Length switch { 0 => "?", 1 => n.ToUpper(), _ => char.ToUpper(n[0]) + n[1..2].ToLower() };
    }

    public static Brush MakeAvatar(string n)
    {
        int h = 17;
        foreach (char c in n) h = unchecked(h * 31 + c);
        var (a, b) = AvatarColors[(h & int.MaxValue) % AvatarColors.Length];
        var brush = new LinearGradientBrush(a, b, 45);
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
