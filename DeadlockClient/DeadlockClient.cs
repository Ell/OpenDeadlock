using DeadlockClient.GC.Deadlock.Internal;
using ProtoBuf;
using Snappier;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.GC;
using SteamKit2.Internal;
using EGCBaseClientMsg = SteamKit2.GC.Dota.Internal.EGCBaseClientMsg;

namespace DeadlockClient;

public class DeadlockClient
{
    private const int Appid = 1422450;
    private readonly CallbackManager _callbackManager;
    private readonly SteamClient _client;
    private readonly SteamGameCoordinator? _gameCoordinator;
    private readonly string _password;
    private readonly SteamUser? _user;
    private readonly string _username;

    private uint _clientVersion;
    private bool _disconnecting;
    private string? _guardData;

    public DeadlockClient(string username, string password, string guardData) : this(username, password)
    {
        _guardData = guardData;
    }

    public DeadlockClient(string username, string password)
    {
        DebugLog.AddListener(new SimpleConsoleDebugListener());
        DebugLog.Enabled = true;

        _username = username;
        _password = password;

        _client = new SteamClient
        {
            DebugNetworkListener = new NetHookNetworkListener()
        };

        _user = _client.GetHandler<SteamUser>();
        _gameCoordinator = _client.GetHandler<SteamGameCoordinator>();

        _callbackManager = new CallbackManager(_client);
        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamGameCoordinator.MessageCallback>(OnGCMessage);

        if (File.Exists("guard.txt")) _guardData = File.ReadAllText("guard.txt");
    }

    public bool IsConnected => _client.IsConnected;

    public void Connect()
    {
        Console.WriteLine("[!] Connecting to Steam");
        _disconnecting = false;
        _client.Connect();
    }

    public void Disconnect()
    {
        _disconnecting = true;
        _client.Disconnect();
    }

    public void Wait()
    {
        while (true) _callbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        // ReSharper disable once FunctionNeverReturns
    }

    public void RunCallbacks(TimeSpan timeSpan)
    {
        _callbackManager.RunWaitCallbacks(timeSpan);
    }

    public async Task<TU?> SendAndReceiveWithJob<T, TU>(ClientGCMsgProtobuf<T> message)
        where T : IExtensible, new()
        where TU : IExtensible, new()
    {
        message.SourceJobID = _client.GetNextJobID();
        _gameCoordinator?.Send(message, Appid);

        try
        {
            var callback = await new AsyncJob<SteamGameCoordinator.MessageCallback>(_client, message.SourceJobID);
            var response = new ClientGCMsgProtobuf<TU>(callback.Message);

            return response.Body;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
            return default;
        }
    }

    public async Task<MatchMetaData?> GetMatchMetaData(uint matchId)
    {
        var message =
            new ClientGCMsgProtobuf<CMsgClientToGCGetMatchMetaData>((uint)EGCCitadelClientMessages
                .k_EMsgClientToGCGetMatchMetaData);
        message.Body.match_id = matchId;

        var response =
            await SendAndReceiveWithJob<CMsgClientToGCGetMatchMetaData, CMsgClientToGCGetMatchMetaDataResponse>(
                message);

        if (response == null) return null;

        return new MatchMetaData
        {
            Data = response,
            ReplayURL =
                $"http://replay{response.cluster_id}.valve.net/{Appid}/{matchId}_{response.replay_salt}.dem.bz2",
            MetadataURL =
                $"http://replay{response.cluster_id}.valve.net/{Appid}/{matchId}_{response.metadata_salt}.meta.bz2"
        };
    }

    public async Task<CMsgClientToGCGetGlobalMatchHistoryResponse?> GetGlobalMatchHistory(uint cursor = 0)
    {
        var message = new ClientGCMsgProtobuf<CMsgClientToGCGetGlobalMatchHistory>((uint)EGCCitadelClientMessages
            .k_EMsgClientToGCGetGlobalMatchHistory);
        message.Body.cursor = cursor;

        return await SendAndReceiveWithJob<CMsgClientToGCGetGlobalMatchHistory,
            CMsgClientToGCGetGlobalMatchHistoryResponse>(message);
    }

    public async Task<CMsgClientToGCSpectateLobbyResponse?> SpectateLobby(ulong lobbyId)
    {
        var message = new ClientGCMsgProtobuf<CMsgClientToGCSpectateLobby>((uint)EGCCitadelClientMessages
            .k_EMsgClientToGCSpectateLobby);
        message.Body.lobby_id = lobbyId;
        message.Body.client_version = _clientVersion;

        return await SendAndReceiveWithJob<CMsgClientToGCSpectateLobby, CMsgClientToGCSpectateLobbyResponse>(message);
    }

    public async Task<CMsgClientToGCGetActiveMatchesResponse?> GetActiveMatches()
    {
        var msg = new ClientGCMsgProtobuf<CMsgClientToGCGetActiveMatches>((uint)EGCCitadelClientMessages
            .k_EMsgClientToGCGetActiveMatches)
        {
            SourceJobID = _client.GetNextJobID()
        };

        _gameCoordinator?.Send(msg, Appid);

        var callback = await new AsyncJob<SteamGameCoordinator.MessageCallback>(_client, msg.SourceJobID);

        var decompressed =
            Snappy.DecompressToArray(new ReadOnlySpan<byte>(callback.Message.GetData(), 24,
                callback.Message.GetData().Length - 24));

        return Serializer.Deserialize<CMsgClientToGCGetActiveMatchesResponse>(new ReadOnlySpan<byte>(decompressed));
    }

    private async void OnConnected(SteamClient.ConnectedCallback obj)
    {
        Console.WriteLine("[!] Connected, logging into steam as {0}", _username);

        var authSession = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = _username,
            Password = _password,
            IsPersistentSession = true,
            GuardData = _guardData,
            Authenticator = new UserConsoleAuthenticator()
        });

        var pollResponse = await authSession.PollingWaitForResultAsync();
        if (pollResponse.NewGuardData != null)
        {
            _guardData = pollResponse.NewGuardData;
            await File.WriteAllTextAsync("guard.txt", _guardData);
        }

        _user?.LogOn(new SteamUser.LogOnDetails
        {
            Username = _username,
            AccessToken = pollResponse.RefreshToken,
            ShouldRememberPassword = true
        });
    }

    private async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            Console.WriteLine("[!] Failed to log into Steam: {0}", callback.Result);
            return;
        }

        Console.WriteLine("[!] Logged into Steam as {0}, Launching Deadlock", _username);

        var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
        playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
        {
            game_id = new GameID(Appid)
        });

        _client.Send(playGame);

        // Wait a bit so steam can have some time to relax
        Thread.Sleep(5000);

        var clientHello = new ClientGCMsgProtobuf<CMsgCitadelClientHello>((uint)EGCBaseClientMsg.k_EMsgGCClientHello);
        clientHello.Body.region_mode = ECitadelRegionMode.k_ECitadelRegionMode_ROW;

        _gameCoordinator?.Send(clientHello, Appid);
    }

    private async void OnDisconnected(SteamClient.DisconnectedCallback obj)
    {
        if (_disconnecting) return;

        Console.WriteLine("[!] Disconnected, trying again in 30 seconds");

        Thread.Sleep(30000);

        Connect();
    }

    public event EventHandler<ClientWelcomeEventArgs>? ClientWelcomeEvent;

    private void OnClientWelcome(IPacketGCMsg packetMessage)
    {
        Console.WriteLine("Got client welcome");

        var message = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMessage);

        _clientVersion = message.Body.version;

        ClientWelcomeEvent?.Invoke(this, new ClientWelcomeEventArgs { Data = message.Body });
    }

    public event EventHandler<DevPlaytestStatusEventArgs>? DevPlaytestStatusEvent;

    private void OnDevPlaytestStatus(IPacketGCMsg packetMessage)
    {
        var message = new ClientGCMsgProtobuf<CMsgGCToClientDevPlaytestStatus>(packetMessage);
        DevPlaytestStatusEvent?.Invoke(this, new DevPlaytestStatusEventArgs { Data = message.Body });
    }

    private async void OnGCMessage(SteamGameCoordinator.MessageCallback callback)
    {
        var messageMap = new Dictionary<uint, Action<IPacketGCMsg>?>
        {
            { (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
            { (uint)EGCCitadelClientMessages.k_EMsgGCToClientDevPlaytestStatus, OnDevPlaytestStatus }
        };

        if (!messageMap.TryGetValue(callback.EMsg, out var func)) return;

        func?.Invoke(callback.Message);
    }

    public class ClientWelcomeEventArgs : EventArgs
    {
        public required CMsgClientWelcome Data;
    }

    public class DevPlaytestStatusEventArgs : EventArgs
    {
        public required CMsgGCToClientDevPlaytestStatus Data;
    }

    public class MatchMetaData
    {
        public required CMsgClientToGCGetMatchMetaDataResponse Data;
        public required string MetadataURL;
        public required string ReplayURL;
    }
}