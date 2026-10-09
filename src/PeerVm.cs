using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WolfSpeak;

/// <summary>A person found on the network, shown on the home screen.</summary>
public sealed class PeerVm(Peer peer) : INotifyPropertyChanged
{
    static readonly (Color A, Color B)[] AvatarColors =
    [
        (Color.FromRgb(0x87, 0x78, 0x9B), Color.FromRgb(0x62, 0x57, 0x76)),
        (Color.FromRgb(0x6C, 0x8B, 0x98), Color.FromRgb(0x45, 0x64, 0x72)),
        (Color.FromRgb(0x76, 0x98, 0x86), Color.FromRgb(0x49, 0x6B, 0x5C)),
        (Color.FromRgb(0xAD, 0x91, 0x6C), Color.FromRgb(0x80, 0x68, 0x4D)),
        (Color.FromRgb(0x92, 0x83, 0xA2), Color.FromRgb(0x6C, 0x5D, 0x7E)),
        (Color.FromRgb(0xAA, 0x80, 0x82), Color.FromRgb(0x7A, 0x59, 0x64)),
        (Color.FromRgb(0x73, 0x96, 0x96), Color.FromRgb(0x49, 0x6D, 0x72)),
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
