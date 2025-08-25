using Microsoft.Extensions.Configuration;
using Telegram_Listener;
using TL; // WTelegramClient
using Amazon.SQS;

// ---------------- CONFIGURATION ----------------
var dir = Directory.GetCurrentDirectory();
var config = new ConfigurationBuilder()
    .SetBasePath(dir)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

var apiId       = Config("Telegram:ApiId", "TG_API_ID");
var apiHash     = Config("Telegram:ApiHash", "TG_API_HASH");
var phone       = Config("Telegram:Phone", "TG_PHONE");
var password    = Config("Telegram:Password", "TG_PASSWORD");
var sessionPath = Config("General:SessionPath", "SESSION_PATH") ?? "./session.dat";
string[] channels   = (Config("General:Channels", "CHANNELS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var sqsQueueUrl = Config("AWS:SqsQueueUrl", "SQS_QUEUE_URL");
var awsRegion = Config("AWS:Region", "AWS_REGION") ?? "us-east-1"; // Default to us-east-1 if not specified

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

async Task OnUpdate(IObject update)
{
    try
    {
        List<SqsMessage> messages = new();
        if (update is UpdatesBase updates)
        {
            foreach (var u in updates.UpdateList)
            {
                switch (u)
                {
                    case UpdateNewChannelMessage uncm when uncm.message is Message msg:
                        if (msg.peer_id is PeerChannel pch)
                        {
                            var link = $"https://t.me/c/{pch.channel_id}/{msg.id}";
                            messages.Add(new SqsMessage(msg.message, link));
                            Console.WriteLine(
                                $"[{msg.date:u}] " +
                                $"Channel {pch.channel_id}, Msg {msg.id}: {msg.message}\n" +
                                $"Link: {link}"
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

        // here we send messages to sqs
        await SendMessagesToSqsAsync(messages);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing update: {ex.Message}");
    }
}

async Task SendMessagesToSqsAsync(IReadOnlyCollection<SqsMessage> messages)
{
    if (string.IsNullOrEmpty(sqsQueueUrl) || messages.Count == 0)
        return;
        
    try
    {
        // Initialize the Amazon SQS client with the specified region
        var sqsClient = new AmazonSQSClient(Amazon.RegionEndpoint.EUNorth1);
        
        foreach (var message in messages)
        {
            // Create the request with message attributes
            var sendMessageRequest = new Amazon.SQS.Model.SendMessageRequest
            {
                QueueUrl = sqsQueueUrl,
                MessageBody = System.Text.Json.JsonSerializer.Serialize(message),
                MessageAttributes = new Dictionary<string, Amazon.SQS.Model.MessageAttributeValue>
                {
                    { 
                        "MessageType", 
                        new Amazon.SQS.Model.MessageAttributeValue
                        { 
                            DataType = "String",
                            StringValue = "TelegramMessage" 
                        }
                    }
                }
            };
            
            // Check if this is a FIFO queue (URL ends with .fifo)
            if (sqsQueueUrl.EndsWith(".fifo", StringComparison.OrdinalIgnoreCase))
            {
                // Required for FIFO queues
                sendMessageRequest.MessageGroupId = "telegram-messages";
                sendMessageRequest.MessageDeduplicationId = Guid.NewGuid().ToString();
            }
            
            // Send the message to SQS
            var response = await sqsClient.SendMessageAsync(sendMessageRequest);
            Console.WriteLine($"Message sent to SQS with ID: {response.MessageId}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error sending to SQS: {ex.Message}");
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
