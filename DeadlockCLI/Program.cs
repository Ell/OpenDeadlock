using System.CommandLine;

var rootCommand = new RootCommand();

var usernameOption = new Option<string>(["--username", "-u"], "Username");
rootCommand.AddGlobalOption(usernameOption);

var passwordOption = new Option<string>(["--password", "-p"], "Password");
rootCommand.AddGlobalOption(passwordOption);

var metadataSubCommand = new Command("metadata", "Deadlock match metadata commands");
rootCommand.Add(metadataSubCommand);

var matchIdArgument = new Argument<uint>("matchid", "Match ID to download metadata for");

var metadataDownloadSubCommand = new Command("download", "Download metadata") { matchIdArgument };

metadataDownloadSubCommand.SetHandler(
    async (usernameOptionValue, passwordOptionValue, matchIdValue) =>
    {
        Console.WriteLine("Fetching metadata for matchid {0}", matchIdValue);

        var isRunning = true;

        var deadlockClient = new DeadlockClient.DeadlockClient(usernameOptionValue, passwordOptionValue);

        async void OnDeadlockClientOnClientWelcomeEvent(object? sender,
            DeadlockClient.DeadlockClient.ClientWelcomeEventArgs e)
        {
            var metadata = await deadlockClient.GetMatchMetaData(matchIdValue);
            var url = metadata?.MetadataURL;

            Console.WriteLine($"Got metadata url: {url}");

            isRunning = false;
        }

        deadlockClient.ClientWelcomeEvent += OnDeadlockClientOnClientWelcomeEvent;

        deadlockClient.Connect();

        while (isRunning) deadlockClient.RunCallbacks(TimeSpan.FromSeconds(1));

        deadlockClient.Disconnect();
    }, usernameOption, passwordOption, matchIdArgument);

metadataSubCommand.Add(metadataDownloadSubCommand);


await rootCommand.InvokeAsync(args);