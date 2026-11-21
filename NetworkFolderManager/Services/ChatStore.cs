using NetworkFolderManager.Models;
using System.IO;
using System.Text.Json;

namespace NetworkFolderManager.Services;

/// <summary>
/// Speichert Kontakte und Chatverläufe lokal unter %APPDATA%\NetworkFolderManager\chats\.
/// Pro Kontakt eine JSON-Datei; Bilder/Dateien werden nur per Pfad referenziert.
/// </summary>
public static class ChatStore
{
    private static readonly string ChatDir = Path.Combine(SettingsService.SettingsDir, "chats");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private class ChatFile
    {
        public Peer Peer { get; set; } = new();
        public List<ChatMessage> Messages { get; set; } = new();
    }

    public static List<Peer> LoadAll()
    {
        var peers = new List<Peer>();
        try
        {
            if (!Directory.Exists(ChatDir))
                return peers;

            foreach (var file in Directory.EnumerateFiles(ChatDir, "*.json"))
            {
                try
                {
                    var data = JsonSerializer.Deserialize<ChatFile>(File.ReadAllText(file), JsonOptions);
                    if (data?.Peer == null || string.IsNullOrEmpty(data.Peer.Id))
                        continue;

                    foreach (var msg in data.Messages)
                    {
                        // Unterbrochene Übertragungen erneut einreihen
                        if (msg.Status == MessageStatus.Sending)
                            msg.Status = MessageStatus.Pending;
                        data.Peer.Messages.Add(msg);
                    }
                    peers.Add(data.Peer);
                }
                catch
                {
                    // einzelne beschädigte Datei überspringen
                }
            }
        }
        catch
        {
            // ignorieren
        }
        return peers;
    }

    public static void Save(Peer peer)
    {
        try
        {
            Directory.CreateDirectory(ChatDir);
            var path = Path.Combine(ChatDir, SafeId(peer.Id) + ".json");
            var tmp = path + ".tmp";
            var data = new ChatFile { Peer = peer, Messages = peer.Messages.ToList() };
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // nicht kritisch
        }
    }

    public static void Delete(Peer peer)
    {
        try
        {
            var path = Path.Combine(ChatDir, SafeId(peer.Id) + ".json");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignorieren
        }
    }

    private static string SafeId(string id) =>
        new(id.Where(char.IsLetterOrDigit).Take(64).ToArray());
}
