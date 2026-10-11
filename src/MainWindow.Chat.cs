using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WolfSpeak;

public partial class MainWindow
{
    sealed class ChatEntry(ChatMessage message)
    {
        public ChatMessage Message = message;
        public DateTime Time = DateTime.Now;
        public string Status = message.Outgoing ? "Sending…" : "";
    }
    readonly Dictionary<uint, List<ChatEntry>> conversations = [];
    readonly Dictionary<uint, int> unreadChats = [];
    uint? activeChatPeer;
    bool DirectChatOpen => DirectChatView.Visibility == Visibility.Visible;
    TextBox ActiveChatInput => DirectChatOpen ? DirectChatInput : ChatInput;

    void OnMessagePeerClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is PeerVm peer) OpenDirectChat(peer.Id);
    }

    void OpenDirectChat(uint peerId)
    {
        if (engine.State != CallState.Idle) return;
        var peer = engine.Peers.FirstOrDefault(p => p.Id == peerId);
        if (peer is null) { ShowNotice("Your friend is offline."); return; }
        activeChatPeer = peerId;
        HomeView.Visibility = Visibility.Collapsed;
        DirectChatView.Visibility = Visibility.Visible;
        DirectChatName.Text = peer.Name;
        DirectChatInput.Clear();
        MarkChatRead(peerId);
        RenderChat();
        DirectChatInput.Focus();
    }

    void OnChatBackClick(object sender, RoutedEventArgs e)
    {
        DirectChatView.Visibility = Visibility.Collapsed;
        activeChatPeer = null;
        HomeView.Visibility = Visibility.Visible;
    }

    void OnChatCallClick(object sender, RoutedEventArgs e)
    {
        if (activeChatPeer is uint id) engine.Call(id);
    }

    void OnChatToggleClick(object sender, RoutedEventArgs e)
    {
        bool open = ChatPanel.Visibility != Visibility.Visible;
        ChatPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        CallDetails.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        ChatToggle.Content = open ? "Back to voice" : "Open chat";
        activeChatPeer = open ? engine.Partner?.Id : null;
        if (activeChatPeer is uint id)
        {
            MarkChatRead(id);
            RenderChat();
            ChatInput.Focus();
        }
    }

    void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SendChatInput();
    }

    void OnChatSendClick(object sender, RoutedEventArgs e) => SendChatInput();

    void SendChatInput()
    {
        if (activeChatPeer is not uint id || string.IsNullOrWhiteSpace(ActiveChatInput.Text)) return;
        if (!engine.SendChat(id, ActiveChatInput.Text))
        {
            ShowNotice("Couldn't send. Check that your friend is online with the same version, or wait for your previous message.");
            return;
        }
        ActiveChatInput.Clear();
        ActiveChatInput.Focus();
        UpdateChatAvailability();
    }

    void AddChatMessage(ChatMessage message)
    {
        uint peerId = message.PeerId != 0 ? message.PeerId : engine.Partner?.Id ?? 0;
        if (peerId == 0) return;
        if (!conversations.TryGetValue(peerId, out var entries))
        {
            // Discovery itself is bounded to 64 peers. Bound retained transcripts too.
            if (conversations.Count >= 64)
            {
                uint oldest = conversations.Keys.First(id => id != activeChatPeer);
                conversations.Remove(oldest); unreadChats.Remove(oldest);
            }
            conversations[peerId] = entries = [];
        }
        if (entries.Count >= 200) entries.RemoveAt(0);
        entries.Add(new(message));
        bool reading = activeChatPeer == peerId && IsVisible && !settingsOpen;
        if (activeChatPeer == peerId) RenderChat();
        if (!message.Outgoing && !reading)
        {
            unreadChats[peerId] = Math.Min(200, unreadChats.GetValueOrDefault(peerId) + 1);
            RefreshUnreadChat(peerId);
            if (engine.Partner?.Id == peerId) ChatToggle.Content = "Open chat · new message";
            ShowNotice($"New message from {message.Sender}. Open Message in your pack to read it.");
            if (!IsVisible) tray.Notify($"Message from {message.Sender}", "Open WolfSpeak to read it");
        }
    }

    void UpdateChatDelivery(uint peerId, uint id, bool delivered)
    {
        var entry = conversations.GetValueOrDefault(peerId)?.FirstOrDefault(e => e.Message.Outgoing && e.Message.Id == id);
        if (entry is not null) entry.Status = delivered ? "Delivered" : "Delivery unconfirmed · try sending again";
        if (activeChatPeer == peerId) RenderChat();
    }

    void MarkChatRead(uint id) { unreadChats.Remove(id); RefreshUnreadChat(id); }
    void RefreshUnreadChat(uint id)
    {
        var vm = peers.FirstOrDefault(p => p.Id == id);
        if (vm is not null) vm.UnreadMessages = unreadChats.GetValueOrDefault(id);
    }

    void RenderChat()
    {
        if (activeChatPeer is not uint id) return;
        var panel = DirectChatOpen ? DirectChatMessages : ChatMessages;
        panel.Children.Clear();
        foreach (var entry in conversations.GetValueOrDefault(id) ?? [])
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var label = new TextBlock { Text = $"{entry.Message.Sender} · {entry.Time:HH:mm}", FontSize = 11 };
            label.SetResourceReference(ForegroundProperty, "SubTextBrush");
            block.Children.Add(label);
            var body = new TextBlock { Text = entry.Message.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            body.SetResourceReference(ForegroundProperty, "TextBrush");
            block.Children.Add(body);
            if (entry.Status.Length > 0)
            {
                var status = new TextBlock { Text = entry.Status, FontSize = 11 };
                status.SetResourceReference(ForegroundProperty, "SubTextBrush");
                block.Children.Add(status);
            }
            panel.Children.Add(block);
        }
        (DirectChatOpen ? DirectChatEmptyHint : ChatEmptyHint).Visibility = panel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        (DirectChatOpen ? DirectChatScroll : ChatScroll).ScrollToEnd();
        UpdateChatAvailability();
    }

    void UpdateChatAvailability()
    {
        if (activeChatPeer is not uint id) return;
        var peer = engine.Peers.FirstOrDefault(p => p.Id == id);
        bool pending = engine.ChatPending(id);
        var send = DirectChatOpen ? DirectChatSend : ChatSend;
        send.IsEnabled = peer is { VersionMatches: true } && !pending;
        send.Content = pending ? "Sending…" : "Send";
        DirectChatCall.IsEnabled = peer is { VersionMatches: true };
        DirectChatStatus.Text = peer is null ? "Offline · messages stay here until you quit" :
            !peer.VersionMatches ? "Update both PCs to the same version to message or call" :
            "Encrypted messages · no call needed · kept until you quit";
    }

    void RememberChatIdentity(uint id, string fingerprint)
    {
        var peer = engine.Peers.FirstOrDefault(p => p.Id == id);
        if (peer is null) return;
        if (settings.KnownKeys.TryGetValue(peer.Name, out var known) && known != fingerprint)
            ShowNotice($"⚠ {peer.Name}'s security key changed. Check with your friend before sharing private messages.");
        settings.KnownKeys[peer.Name] = fingerprint;
        settings.Save();
    }
}
