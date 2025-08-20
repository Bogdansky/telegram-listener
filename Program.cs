using Microsoft.Extensions.Configuration;
using TL; // WTelegramClient

// ---------------- CONFIGURATION ----------------
var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.dev.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

string? apiId       = Config("Telegram:ApiId", "TG_API_ID");
string? apiHash     = Config("Telegram:ApiHash", "TG_API_HASH");
string? phone       = Config("Telegram:Phone", "TG_PHONE");
string? password    = Config("Telegram:Password", "TG_PASSWORD");
string? sessionPath = Config("General:SessionPath", "SESSION_PATH") ?? "./session.dat";
string[] channels   = (Config("General:Channels", "CHANNELS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

string? sqsQueueUrl = Config("AWS:SqsQueueUrl", "SQS_QUEUE_URL");

bool running = true;

// ---------------- TELEGRAM CLIENT ----------------
using var client = new WTelegram.Client(WtConfig);

Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };

Console.WriteLine("Logging in to Telegram… (first run may prompt for code/password)");
await client.LoginUserIfNeeded();

Console.WriteLine("Listener started. Press Ctrl+C to exit.");
foreach (var ch in channels)
{
    try
    {
        var r = await client.Contacts_ResolveUsername(ch.TrimStart('@'));
        Console.WriteLine($"  • subscribed to @{ch.TrimStart('@')} (id={r.UserOrChat?.ID})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ! cannot resolve @{ch}: {ex.Message}");
    }
}

await foreach (var update in client.Updates)
{
    try
    {
        await OnUpdate(update);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing update: {ex.Message}");
    }
}

// ---------------- MAIN LOOP ----------------
while (running) await Task.Delay(500);
await client.DisposeAsync();
Console.WriteLine("Stopped.");

// ================== HELPERS ==================

async Task OnUpdate(IObject upd)
{
    if (upd is not UpdatesBase ubase) return;

    foreach (var u in ubase.UpdateList)
    {
        Message? msg = u switch
        {
            UpdateNewChannelMessage uncm   => uncm.message as Message,
            UpdateEditChannelMessage uecm  => uecm.message as Message,
            UpdateNewMessage unm           => unm.message as Message,
            _ => null
        };
        if (msg is null || msg.peer_id is not PeerChannel pch) continue;
        if (msg.message is null && msg.media is null) continue;

        var chats = await client.Messages_GetChats(pch.channel_id);
        var channel = chats.chats.TryGetValue(pch.channel_id, out var ch) ? ch as Channel : null;
        var username = (channel?.username ?? "").ToLowerInvariant();

        if (channels.Length > 0)
        {
            bool match = channels.Any(c =>
                (c.StartsWith("-100") && c == $"-100{pch.channel_id}") ||
                (c.TrimStart('@').Equals(username, StringComparison.OrdinalIgnoreCase)));
            if (!match) continue;
        }

        var text = msg.message ?? "";
        var snippet = text.Length > 120 ? text[..120] + "…" : text;
        var ts = msg.date.ToLocalTime();
        var link = !string.IsNullOrEmpty(username) ? $"https://t.me/{username}/{msg.id}" : "(no link)";

        Console.WriteLine($"\n[{ts:yyyy-MM-dd HH:mm:ss}] @{username} #{msg.id}\n{snippet}\n{link}");
    }
}

// функция для WTelegram
string? WtConfig(string what) => what switch
{
    "api_id"           => apiId,
    "api_hash"         => apiHash,
    "phone_number"     => phone,
    "password"         => password,
    "verification_code"=> Prompt("Enter code from Telegram: "),
    "session_pathname" => sessionPath,
    _ => null
};

string? Config(string jsonKey, string envVar)
{
    var val = config[jsonKey];
    if (!string.IsNullOrEmpty(val)) return val;
    return Environment.GetEnvironmentVariable(envVar);
}

static string Prompt(string label)
{
    Console.Write(label);
    return Console.ReadLine() ?? "";
}
