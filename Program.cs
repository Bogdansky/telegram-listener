using Microsoft.Extensions.Configuration;
using TL; // WTelegramClient

// ---------------- CONFIGURATION ----------------
var dir = Directory.GetCurrentDirectory();
var config = new ConfigurationBuilder()
    .SetBasePath(dir)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
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
client.OnUpdates += OnUpdate;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };

Console.WriteLine("Logging in to Telegram… (first run may prompt for code/password)");
await client.LoginUserIfNeeded();

Console.WriteLine("Listener started. Press Ctrl+C to exit.");
var peer = await client.Contacts_ResolveUsername(channels.First());

if (peer.peer is not PeerChannel pch)
{
    throw new InvalidDataException($"{channels.First()} is not a channel");
}

var targetChannelId = pch.channel_id;
var getAllChatsTask = client.Messages_GetAllChats();
getAllChatsTask.Wait();


if (!getAllChatsTask.Result.chats.TryGetValue(targetChannelId, out ChatBase channel))
{
    throw new InvalidDataException($"ID {targetChannelId} does not match with any channel");
}

// ---------------- MAIN LOOP ----------------
while (running) Task.Delay(500).Wait();
await client.DisposeAsync();
Console.WriteLine("Stopped.");

// ================== HELPERS ==================

Task OnUpdate(IObject update)
{
    if (update is UpdatesBase updates)
    {
        foreach (var u in updates.UpdateList)
        {
            switch (u)
            {
                case UpdateNewChannelMessage uncm when uncm.message is Message msg:
                    if (msg.peer_id is PeerChannel pch)
                    {
                        Console.WriteLine(
                            $"[{msg.date:u}] " +
                            $"Channel {pch.channel_id}, Msg {msg.id}: {msg.message}"
                        );
                    }
                    break;

                case UpdateNewMessage unm when unm.message is Message dm:
                    Console.WriteLine($"DM {dm.id}: {dm.message}");
                    break;

                // при желании: UpdateEditChannelMessage, UpdateDeleteMessages, и т.д.
            }
        }
    }
    return Task.CompletedTask;
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
