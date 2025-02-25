using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Dissonance;
using HarmonyLib;
using LobbyCompatibility.Attributes;
using LobbyCompatibility.Enums;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements.Collections;
using Object = System.Object;

namespace SteamBlacklist;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("BMX.LobbyCompatibility")]
[LobbyCompatibility(CompatibilityLevel.ServerOnly, VersionStrictness.None)]
public class SteamBlacklist : BaseUnityPlugin
{
    public static SteamBlacklist Instance { get; private set; } = null!;
    internal static new ManualLogSource Logger { get; private set; } = null!;
    internal static Harmony? Harmony { get; set; }

    internal ConfigEntry<bool> AllowBlockedFriends { get; private set; } = null!;
    internal ConfigEntry<bool> IgnoreBlockedMembers { get; private set; } = null!;

    public Dictionary<ulong, bool> SteamJoinQueue { get; set; } = new Dictionary<ulong, bool>();

    private void Awake()
    {
        Logger = base.Logger;
        Instance = this;

        Patch();

        AllowBlockedFriends = Config.Bind(
            "General",
            "allowBlockedFriends",
            true,
            "Allows blocked players in your friends list"
        );
        IgnoreBlockedMembers = Config.Bind(
            "General",
            "ignoreBlockedMembers",
            true,
            "true: Join lobby if host is not blocked\nfalse: Join lobby if host and members aren't blocked"
        );

        Logger.LogInfo($"Loaded SteamBlacklist mod v{MyPluginInfo.PLUGIN_VERSION}");
    }

    [HarmonyPatch(typeof(GameNetworkManager), "SteamMatchmaking_OnLobbyMemberJoined")]
    public class SteamPlayerJoinPatch
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Warning", "Harmony003")]
        private static bool Prefix(Lobby lobby, Friend friend)
        {
            Logger.LogDebug(
                $" >> SteamPlayerJoinPatch (SteamMatchmaking_OnLobbyMemberJoined) STEAM PLAYER JOINED {friend.Name} {friend.Id} {friend.Relationship}"
            );
            if (!GameNetworkManager.Instance.isHostingGame)
            {
                Logger.LogDebug(
                    $" << SteamPlayerJoinPatch NOT HOST {friend.Name} ({friend.Id}) [{friend.Relationship}]"
                );
                return true;
            }

            bool allow = true;
            if (friend.Id == lobby.Owner.Id)
            {
                Logger.LogInfo($"Host joined: {friend.Name}");
                Logger.LogDebug($" << SteamPlayerJoinPatch HOST {friend.Name} {friend.Id}");
                return true;
            }
            if (GameNetworkManager.Instance.currentLobby.HasValue)
                switch (friend.Relationship) // https://partner.steamgames.com/doc/api/ISteamFriends#EFriendRelationship
                {
                    case Relationship.Ignored:
                        Logger.LogInfo($"Blocked player rejected: {friend.Name} ({friend.Id})");
                        allow = false;
                        break;
                    case Relationship.IgnoredFriend:
                        string s;
                        if (Instance.AllowBlockedFriends.Value)
                            s = "allowed";
                        else
                        {
                            s = "rejected";
                            allow = false;
                        }

                        Logger.LogInfo($"Blocked friend {s}: {friend.Name} ({friend.Id})");
                        break;
                    case Relationship.Friend:
                        Logger.LogInfo($"Friend joined: {friend.Name} ({friend.Id})");
                        break;
                    case Relationship.None:
                        Logger.LogInfo($"Player joined: {friend.Name} ({friend.Id})");
                        break;
                    case Relationship.RequestInitiator:
                        Logger.LogInfo($"Player joined: {friend.Name} ({friend.Id})");
                        Logger.LogInfo("You have sent them a friend request");
                        break;
                    case Relationship.RequestRecipient:
                        Logger.LogInfo($"Player joined: {friend.Name} ({friend.Id})");
                        Logger.LogInfo("They have sent you a friend request");
                        break;
                    case Relationship.Blocked: // Blocked = Ignored; Ignored = Blocked
                        Logger.LogInfo($"Player joined: {friend.Name} ({friend.Id})");
                        Logger.LogInfo("You have ignored their friend request");
                        break;
                    default:
                        Logger.LogInfo(
                            $"Player joined: {friend.Name} ({friend.Id}) [Relationship: {friend.Relationship}]"
                        );
                        break;
                }

            Instance.SteamJoinQueue.TryAdd(friend.Id, allow);

            Logger.LogDebug(
                " << SteamPlayerJoinPatch "
                    + (allow ? "ALLOW" : "DENY")
                    + $" {friend.Name} {friend.Id}"
            );
            return true;
        }
    }

    [HarmonyPatch(typeof(GameNetworkManager), "ConnectionApproval")]
    public class PlayerJoinPatch
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Warning", "Harmony003")]
        private static bool Prefix(
            NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response
        )
        {
            Logger.LogDebug(
                $" >> PlayerJoinPatch (ConnectionApproval) UNITY-NETCODE PLAYER CONNECTED {request.ClientNetworkId} {request.Payload}"
            );
            if (GameNetworkManager.Instance.disableSteam)
            {
                Logger.LogDebug($" << PlayerJoinPatch STEAM DISABLED");
                return true;
            }

            if (request.ClientNetworkId == 0)
            {
                Logger.LogDebug($" << PlayerJoinPatch HOST");
                Logger.LogDebug(
                    $" >> StartHostPatch (None) Resetting SteamJoinQueue (was {SteamBlacklist.Instance.SteamJoinQueue.Count} items)"
                );
                Instance.SteamJoinQueue.Clear();
                Logger.LogDebug(
                    $" << StartHostPatch RESET SteamJoinQueue {SteamBlacklist.Instance.SteamJoinQueue.Count}"
                );
                return true;
            }

            string[] payload;
            ulong id;
            try
            {
                payload = Encoding.ASCII.GetString(request.Payload).Split(",");
                id = (ulong)Convert.ToInt64(payload[1]);
                Logger.LogDebug($" >> STEAMID={id} - PAYLOAD={payload}");
            }
            catch (Exception)
            {
                Logger.LogDebug($"Received invalid approval request: {request.Payload}");
                return false;
            }

            Logger.LogDebug($"Received approval request: {id}");
            bool? allow;
            try
            {
                allow = Instance.SteamJoinQueue.Get(id);
            }
            catch (KeyNotFoundException)
            {
                allow = null;
            }

            if (allow.HasValue && allow.Value)
            {
                Instance.SteamJoinQueue.Remove(id);
                Logger.LogDebug(
                    $" << PlayerJoinPatch ALLOW {request.ClientNetworkId} {payload[1]}"
                );
                Logger.LogDebug($"Playing with {id} - notifying steam...");
                SteamFriends.SetPlayedWith(id);
                return true;
            }

            response.Reason = allow.HasValue
                ? "You have been blacklisted!"
                : "Invalid SteamID, please remove any AntiKick mods!";
            response.CreatePlayerObject = false;
            response.Approved = false;
            response.Pending = false;
            Logger.LogInfo(
                $"Denied approval request: {id} - {response.Reason.Split(",")[0].Replace("!", "")}!"
            );
            Instance.SteamJoinQueue.Remove(id);
            Logger.LogDebug(
                $" << PlayerJoinPatch DENY {request.ClientNetworkId} {response.Reason}"
            );
            return false;
        }
    }

    [HarmonyPatch(typeof(GameNetworkManager), "LobbyDataIsJoinable")]
    class JoinGamePatch
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Warning", "Harmony003")]
        private static bool Prefix(Lobby lobby, ref bool __result)
        {
            string? message = null;

            switch (lobby.Owner.Relationship)
            {
                case Relationship.Ignored:
                    Logger.LogInfo(
                        $"Lobby hosted by blocked player: {lobby.Owner.Name} ({lobby.Owner.Id})"
                    );
                    message = "Lobby is hosted by a blocked player!";
                    break;
                case Relationship.IgnoredFriend:
                    if (!Instance.AllowBlockedFriends.Value)
                        message = "Lobby is hosted by a blocked friend!";
                    Logger.LogInfo(
                        $"Lobby is hosted by a blocked friend: {lobby.Owner.Name} ({lobby.Owner.Id})"
                    );
                    break;
                case Relationship.Friend:
                    Logger.LogInfo($"Lobby host (Friend): {lobby.Owner.Name} ({lobby.Owner.Id})");
                    break;
                case Relationship.None:
                    Logger.LogInfo($"Lobby host: {lobby.Owner.Name} ({lobby.Owner.Id})");
                    break;
                case Relationship.RequestInitiator:
                    Logger.LogInfo($"Lobby host: {lobby.Owner.Name} ({lobby.Owner.Id})");
                    Logger.LogInfo("You have sent them a friend request");
                    break;
                case Relationship.RequestRecipient:
                    Logger.LogInfo($"Lobby host: {lobby.Owner.Name} ({lobby.Owner.Id})");
                    Logger.LogInfo("They have sent you a friend request");
                    break;
                case Relationship.Blocked: // Blocked = Ignored; Ignored = Blocked
                    Logger.LogInfo($"Lobby host: {lobby.Owner.Name} ({lobby.Owner.Id})");
                    Logger.LogInfo("You have ignored their friend request");
                    break;
                default:
                    Logger.LogInfo(
                        $"Lobby host: {lobby.Owner.Name} ({lobby.Owner.Id}) [Relationship: {lobby.Owner.Relationship}]"
                    );
                    break;
            }
            if (message != null)
            {
                UnityEngine
                    .Object.FindObjectOfType<MenuManager>()
                    .SetLoadingScreen(isLoading: false, RoomEnter.YouBlockedMember, message);
                __result = false;
                return false;
            }

            foreach (Friend friend in lobby.Members)
            {
                if (SteamBlacklist.Instance.IgnoreBlockedMembers.Value)
                    break;
                if (friend.Id == lobby.Owner.Id)
                    continue;

                switch (friend.Relationship)
                {
                    case Relationship.Ignored:
                        Logger.LogInfo(
                            $"Lobby contains a blocked player: {friend.Name} ({friend.Id})"
                        );
                        message = "Lobby contains a blocked player!";
                        break;
                    case Relationship.IgnoredFriend:
                        if (!Instance.AllowBlockedFriends.Value)
                            message = "Lobby contains a blocked friend!";
                        Logger.LogInfo(
                            $"Lobby contains a blocked friend: {friend.Name} ({friend.Id})"
                        );
                        break;
                    case Relationship.Friend:
                        Logger.LogInfo($"Lobby member (Friend): {friend.Name} ({friend.Id})");
                        break;
                    case Relationship.None:
                        Logger.LogInfo($"Lobby member: {friend.Name} ({friend.Id})");
                        break;
                    case Relationship.RequestInitiator:
                        Logger.LogInfo($"Lobby member: {friend.Name} ({friend.Id})");
                        Logger.LogInfo("You have sent them a friend request");
                        break;
                    case Relationship.RequestRecipient:
                        Logger.LogInfo($"Lobby member: {friend.Name} ({friend.Id})");
                        Logger.LogInfo("They have sent you a friend request");
                        break;
                    case Relationship.Blocked: // Blocked = Ignored; Ignored = Blocked
                        Logger.LogInfo($"Lobby member: {friend.Name} ({friend.Id})");
                        Logger.LogInfo("You have ignored their friend request");
                        break;
                    default:
                        Logger.LogInfo(
                            $"Lobby member: {friend.Name} ({friend.Id}) [Relationship: {friend.Relationship}]"
                        );
                        break;
                }

                if (message != null)
                {
                    UnityEngine
                        .Object.FindObjectOfType<MenuManager>()
                        .SetLoadingScreen(isLoading: false, RoomEnter.YouBlockedMember, message);
                    __result = false;
                    return false;
                }
            }

            try
            {
                string data = lobby.GetData("vers");
                if (data != GameNetworkManager.Instance.gameVersionNum.ToString())
                {
                    Logger.LogDebug(
                        $" == Lobby join denied! Invalid version: '{data}' lobby id: '{lobby.Id}'"
                    );
                    UnityEngine
                        .Object.FindObjectOfType<MenuManager>()
                        .SetLoadingScreen(
                            isLoading: false,
                            RoomEnter.Error,
                            $"Invalid version\nLobby: {data}\nClient: {GameNetworkManager.Instance.gameVersionNum}"
                        );
                    return false;
                }

                if (lobby.GetData("joinable") == "false")
                {
                    Logger.LogDebug(" == Lobby join denied! Host lobby is not joinable");
                    UnityEngine
                        .Object.FindObjectOfType<MenuManager>()
                        .SetLoadingScreen(
                            isLoading: false,
                            RoomEnter.NotAllowed,
                            "The server host has already landed their ship."
                        );
                    __result = false;
                    return false;
                }

                if (lobby.MemberCount >= 4 || lobby.MemberCount < 1)
                {
                    Logger.LogDebug(
                        $" == Lobby join denied! Invalid member count {lobby.MemberCount} lobby id: '{lobby.Id}'"
                    );
                    UnityEngine
                        .Object.FindObjectOfType<MenuManager>()
                        .SetLoadingScreen(isLoading: false, RoomEnter.Full, "The server is full!");
                    __result = false;
                    return false;
                }

                Logger.LogDebug($" == Lobby join accepted! Lobby id {lobby.Id} is OK");
                __result = true;
            }
            catch (Exception e)
            {
                Logger.LogError($"Lobby join denied! Error: {e.StackTrace}");
                __result = false;
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(SteamLobbyManager), "LoadServerList")]
    public class LobbyListPatch
    {
        private static bool Prefix()
        {
            _Prefix();
            return false;
        }

        private static async void _Prefix()
        {
            SteamLobbyManager lobbymanager =
                UnityEngine.Object.FindObjectOfType<SteamLobbyManager>();
            if (GameNetworkManager.Instance.waitingForLobbyDataRefresh)
            {
                return;
            }
            lobbymanager.refreshServerListTimer = 0f;
            lobbymanager.serverListBlankText.text = "Loading server list...";
            lobbymanager.currentLobbyList = null;
            LobbySlot[] array = UnityEngine.Object.FindObjectsOfType<LobbySlot>();
            foreach (LobbySlot slot in array)
            {
                UnityEngine.Object.Destroy(slot.gameObject);
            }
            SteamMatchmaking.LobbyList.WithMaxResults(20);
            SteamMatchmaking.LobbyList.WithKeyValue("started", "0");
            SteamMatchmaking.LobbyList.WithKeyValue(
                "versNum",
                GameNetworkManager.Instance.gameVersionNum.ToString()
            );
            SteamMatchmaking.LobbyList.WithSlotsAvailable(1);
            switch (lobbymanager.sortByDistanceSetting)
            {
                case 0:
                    SteamMatchmaking.LobbyList.FilterDistanceClose();
                    break;
                case 1:
                    SteamMatchmaking.LobbyList.FilterDistanceFar();
                    break;
                case 2:
                    SteamMatchmaking.LobbyList.FilterDistanceWorldwide();
                    break;
            }
            lobbymanager.currentLobbyList = null;
            int hidden = 0;
            Debug.Log("Requested server list");
            GameNetworkManager.Instance.waitingForLobbyDataRefresh = true;
            SteamMatchmaking.LobbyList.WithSlotsAvailable(1);
            LobbyQuery lobbyQuery = lobbymanager.sortByDistanceSetting switch
            {
                0 => SteamMatchmaking
                    .LobbyList.FilterDistanceClose()
                    .WithSlotsAvailable(1)
                    .WithKeyValue("vers", GameNetworkManager.Instance.gameVersionNum.ToString()),
                1 => SteamMatchmaking
                    .LobbyList.FilterDistanceFar()
                    .WithSlotsAvailable(1)
                    .WithKeyValue("vers", GameNetworkManager.Instance.gameVersionNum.ToString()),
                _ => SteamMatchmaking
                    .LobbyList.FilterDistanceWorldwide()
                    .WithSlotsAvailable(1)
                    .WithKeyValue("vers", GameNetworkManager.Instance.gameVersionNum.ToString()),
            };
            if (!lobbymanager.sortWithChallengeMoons)
            {
                lobbyQuery = lobbyQuery.WithKeyValue("chal", "f");
            }

            lobbymanager.currentLobbyList = await (
                (lobbymanager.serverTagInputField.text == string.Empty)
                    ? lobbyQuery.WithKeyValue("tag", "none")
                    : lobbyQuery.WithKeyValue(
                        "tag",
                        lobbymanager
                            .serverTagInputField.text.Substring(
                                0,
                                Mathf.Min(19, lobbymanager.serverTagInputField.text.Length)
                            )
                            .ToLower()
                    )
            ).RequestAsync();

            GameNetworkManager.Instance.waitingForLobbyDataRefresh = false;
            if (lobbymanager.currentLobbyList != null)
            {
                if (lobbymanager.currentLobbyList.Length == 0)
                {
                    lobbymanager.serverListBlankText.text = "No available servers to join.";
                }
                else
                {
                    lobbymanager.serverListBlankText.text = "";
                }
                lobbymanager.lobbySlotPositionOffset = 0f;

                foreach (Lobby lobby in lobbymanager.currentLobbyList)
                {
                    string lobbyName = lobby.GetData("name");
                    if (lobbyName.Length == 0)
                    {
                        continue;
                    }
                    if (
                        lobby.Owner.Relationship == Relationship.Ignored
                        || (
                            !SteamBlacklist.Instance.AllowBlockedFriends.Value
                            && lobby.Owner.Relationship == Relationship.IgnoredFriend
                        )
                    )
                    {
                        Logger.LogInfo(
                            $"Lobby hidden: '{lobbyName}' ({lobby.Id}) by '{lobby.Owner.Name}' ({lobby.Owner.Id}) [Blocked"
                                + (
                                    lobby.Owner.Relationship == Relationship.IgnoredFriend
                                        ? " friend]"
                                        : " host]"
                                )
                        );
                        hidden++;
                        continue;
                    }

                    GameObject original = (
                        (lobby.GetData("chal") != "t")
                            ? lobbymanager.LobbySlotPrefab
                            : lobbymanager.LobbySlotPrefabChallenge
                    );
                    GameObject obj = UnityEngine.Object.Instantiate(
                        original,
                        lobbymanager.levelListContainer
                    );
                    obj.GetComponent<RectTransform>().anchoredPosition = new Vector2(
                        0f,
                        0f + lobbymanager.lobbySlotPositionOffset
                    );
                    lobbymanager.lobbySlotPositionOffset -= 42f;
                    LobbySlot componentInChildren = obj.GetComponentInChildren<LobbySlot>();
                    componentInChildren.LobbyName.text = lobbyName.Substring(
                        0,
                        Mathf.Min(lobbyName.Length, 40)
                    );
                    componentInChildren.playerCount.text = $"{lobby.MemberCount} / 4";
                    componentInChildren.lobbyId = lobby.Id;
                    componentInChildren.thisLobby = lobby;
                }
                if (hidden > 0)
                    Logger.LogInfo(
                        $"{hidden} "
                            + (hidden == 1 ? "lobby was" : "lobbies were")
                            + " hidden because you blocked "
                            + (hidden == 1 ? "its host" : "their hosts")
                    );
                else if (hidden == lobbymanager.currentLobbyList.Length)
                    lobbymanager.serverListBlankText.text =
                        "No available servers to join (All servers are hosted by people you've blocked).";
            }
            else
            {
                Debug.Log("Lobby list is null after request.");
                lobbymanager.serverListBlankText.text =
                    "No available servers to join (Steam did not respond).";
            }
        }
    }

    internal static void Patch()
    {
        Harmony ??= new Harmony(MyPluginInfo.PLUGIN_GUID);

        Logger.LogDebug("Patching...");

        Harmony.PatchAll();

        Logger.LogDebug("Finished patching!");
    }

    internal static void Unpatch()
    {
        Logger.LogDebug("Unpatching...");

        Harmony?.UnpatchSelf();

        Logger.LogDebug("Finished unpatching!");
    }
}
