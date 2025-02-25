using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LobbyCompatibility.Attributes;
using LobbyCompatibility.Enums;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine.UIElements.Collections;

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
