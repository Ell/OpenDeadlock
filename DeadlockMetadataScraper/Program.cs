using DeadlockClient.GC.Deadlock.Internal;

var username = Environment.GetEnvironmentVariable("DEADLOCK_STEAMWORKS_USERNAME");
var password = Environment.GetEnvironmentVariable("DEADLOCK_STEAMWORKS_PASSWORD");

if (username == null || password == null)
{
    Console.WriteLine("Missing login");
    return;
}

var client = new DeadlockClient.DeadlockClient(username, password);

client.ClientWelcomeEvent += OnClientWelcomeEvent;
client.Connect();
client.Wait();

client.Disconnect();

return;

async void OnClientWelcomeEvent(object? sender, DeadlockClient.DeadlockClient.ClientWelcomeEventArgs e)
{
    Console.WriteLine("Fetching matches");

    var matches = await client.GetGlobalMatchHistory();
    if (matches == null)
    {
        Console.WriteLine("No matches found");
        return;
    }

    if (matches.result != CMsgClientToGCGetGlobalMatchHistoryResponse.EResult.k_eSuccess)
    {
        Console.WriteLine("Failed to get global match history {0}", matches.result);
        return;
    }

    Console.WriteLine("Got global match history {0}", matches.matches.Count);
}