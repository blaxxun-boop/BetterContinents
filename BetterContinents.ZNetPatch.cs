﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace BetterContinents
{
    public partial class BetterContinents
    {
        private static string LastConnectionError = null;
        
        // Dealing with settings, synchronization of them in multiplayer
        [HarmonyPatch(typeof(ZNet))]
        private class ZNetPatch
        {
            private static string PeerName(ZNetPeer peer) => $"{peer.m_uid}";
                
            // When the world is set on the server (applies to single player as well), we should select the correct loaded settings
            [HarmonyPrefix, HarmonyPatch(nameof(ZNet.SetServer))]
            private static void SetServerPrefix(bool server, World world)
            {
                if (server)
                {
                    Log($"Selected world {world.m_name}, applying settings");

                    // Load in our settings for this world
                    try
                    {
                        var newSettings = BetterContinentsSettings.Load(world.GetMetaPath() + ".BetterContinents");
                        if (newSettings.WorldUId != world.m_uid)
                        {
                            LogError($"ID in saved settings for {world.m_name} didn't match, mod is disabled for this World");
                        }
                        else
                        {
                            Settings = newSettings;
                        }
                    }
                    catch
                    {
                        Log($"Couldn't find loaded settings for world {world.m_name}, mod is disabled for this World");
                        Settings = BetterContinentsSettings.Disabled(world.m_uid);
                    }
            
                    Settings.Dump();
                }
                else
                {
                    // Disable the mod so we don't end up breaking if the server doesn't use it
                    Log($"Joining a server, so disabling local settings");
                    Settings = BetterContinentsSettings.Disabled();
                }
            }

            private static byte[] SettingsReceiveBuffer;
            private static int SettingsReceiveBufferBytesReceived;
            private static int SettingsReceiveHash;
            
            private static int GetHashCode<T>(T[] array)
            {
                unchecked
                {
                    if (array == null)
                    {
                        return 0;
                    }
                    int hash = 17;
                    foreach (T element in array)
                    {
                        hash = hash * 31 + element.GetHashCode();
                    }
                    return hash;
                }
            }

            private static string ServerVersion;

            private static class WorldCache
            {
                private static readonly string WorldCachePath = Path.Combine(Utils.GetSaveDataPath(), "BetterContinents", "cache");
                private static string GetCachePath(string id) => Path.Combine(WorldCachePath, id + ".bc");
                
                public static void Add(ZPackage package)
                {
                    var filePath = GetCachePath(PackageID(package));
                    if (File.Exists(filePath))
                    {
                        LogError($"{filePath} already exists in cache, this shouldn't happen! Deleting the file...");
                        File.Delete(filePath);
                    }
                    Log($"Adding cache entry {filePath}");
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                    File.WriteAllBytes(filePath + ".tmp", package.GetArray());
                    File.Move(filePath + ".tmp", filePath);
                }

                private static List<string> GetCacheList() =>
                    Directory.Exists(WorldCachePath)
                        ? Directory.GetFiles(WorldCachePath, "*.bc").Select(f => Path.GetFileNameWithoutExtension(f).ToLower()).ToList()
                        : Enumerable.Empty<string>().ToList();

                public static ZPackage SerializeCacheList()
                {
                    var items = GetCacheList();
                    var pkg = new ZPackage();
                    pkg.Write(items.Count);
                    foreach (var item in items)
                    {
                        pkg.Write(item);
                    }

                    return pkg;
                }

                public static bool CacheItemExists(ZPackage item, ZPackage cacheList) =>
                    CacheItemExists(PackageID(item), cacheList);

                public static bool CacheItemExists(string id, ZPackage cacheList)
                {
                    int itemCount = cacheList.ReadInt();
                    for (int i = 0; i < itemCount; i++)
                    {
                        if (id == cacheList.ReadString())
                        {
                            return true;
                        }
                    }

                    return false;
                }
                
                public static ZPackage LoadCacheItem(string id) => new ZPackage(File.ReadAllBytes(GetCachePath(id)));

                public static void DeleteCacheItem(string id) => File.Delete(GetCachePath(id));

                public static BetterContinentsSettings LoadCacheSettings(string id) =>
                    BetterContinentsSettings.Load(LoadCacheItem(id));
                
                private static string ByteArrayToString(byte[] ba)
                {
                    var hex = new StringBuilder(ba.Length * 2);
                    foreach (byte b in ba)
                        hex.AppendFormat("{0:x2}", b);
                    return hex.ToString();
                }

                public static string PackageID(ZPackage package) => ByteArrayToString(package.GenerateHash()).Substring(0, 32).ToLower();
            }

            private class BCClientInfo
            {
                public string version;
                public ZPackage worldCache;
                public bool readyForPeerInfo;
            }
            
            private static readonly Dictionary<long, BCClientInfo> ClientInfo = new Dictionary<long, BCClientInfo>();

            private static readonly FieldInfo m_connectionStatus = AccessTools.Field(typeof(ZNet), "m_connectionStatus");

            // Register our RPC for receiving settings on clients
            [HarmonyPrefix, HarmonyPatch("OnNewConnection")]
            private static void OnNewConnectionPrefix(ZNet __instance, ZNetPeer peer)
            {
                Log($"Registering settings RPC");

                ServerVersion = "(old)";

                if (ZNet.instance.IsServer())
                {
                    ClientInfo.Remove(peer.m_uid);
                    peer.m_rpc.Register("BetterContinentsServerHandshake", (ZRpc rpc, string clientVersion, ZPackage worldCache) =>
                    {
                        Log($"Receiving client {PeerName(peer)} version {clientVersion}");
                        // We check this when sending settings (if we have a BC world loaded, otherwise it doesn't matter)
                        ClientInfo[peer.m_uid] = new BCClientInfo { version = clientVersion, worldCache = worldCache, readyForPeerInfo = false };
                    });
                    
                    peer.m_rpc.Register("BetterContinentsReady", (ZRpc rpc, int stage) =>
                    {
                        Log($"Client is ready for PeerInfo");

                        // We wait for this flag before continuing after sending the world settings, allowing the client to behave asynchronously on its end
                        ClientInfo[peer.m_uid].readyForPeerInfo = true;
                    });
                }
                else
                {
                    peer.m_rpc.Invoke("BetterContinentsServerHandshake", ModInfo.Version, WorldCache.SerializeCacheList());

                    peer.m_rpc.Register("BetterContinentsVersion", (ZRpc rpc, string serverVersion) =>
                    {
                        ServerVersion = serverVersion;
                        Log($"Receiving server version {serverVersion}");
                    });

                    peer.m_rpc.Register("BetterContinentsConfigLoadFromCache", (ZRpc rpc, string id) =>
                    {
                        Log($"Loading server world settings from local cache, id {id}");

                        __instance.StartCoroutine(LoadFromCache(peer, id));
                    });
                    
                    peer.m_rpc.Register("BetterContinentsConfigStart", (ZRpc rpc, int totalBytes, int hash) =>
                    {
                        SettingsReceiveBuffer = new byte[totalBytes];
                        SettingsReceiveHash = hash;
                        SettingsReceiveBufferBytesReceived = 0;
                        Log($"Receiving settings from server ({SettingsReceiveBuffer.Length} bytes)");

                        UI.Add("ConfigDownload", () => UI.ProgressBar(SettingsReceiveBufferBytesReceived * 100 / SettingsReceiveBuffer.Length, $"Better Continents: downloading world settings from server ..."));
                    });

                    peer.m_rpc.Register("BetterContinentsConfigPacket", (ZRpc rpc, int offset, int packetHash, ZPackage packet) =>
                    {
                        var packetData = packet.GetArray();
                        int hash = GetHashCode(packetData);
                        if (hash != packetHash)
                        {
                            LastConnectionError = $"Better Continents: settings from server were corrupted during transfer, please reconnect!";
                            LogError($"{LastConnectionError}: packet hash mismatch, got {hash}, expected {packetHash}");
                            
                            m_connectionStatus.SetValue(null, ZNet.ConnectionStatus.ErrorConnectFailed);
                            ZNet.instance.Disconnect(peer);
                            return;
                        }

                        Buffer.BlockCopy(packetData, 0, SettingsReceiveBuffer, offset, packetData.Length);
                        
                        SettingsReceiveBufferBytesReceived += packetData.Length;

                        Log($"Received settings packet {packetData.Length} bytes at {offset}, {SettingsReceiveBufferBytesReceived} / {SettingsReceiveBuffer.Length} received");
                        if (SettingsReceiveBufferBytesReceived == SettingsReceiveBuffer.Length)
                        {
                            UI.Remove("ConfigDownload");
                            __instance.StartCoroutine(ReceivedSettings(peer));
                        }
                    });
                }
            }

            private static IEnumerator LoadFromCache(ZNetPeer peer, string id)
            {
                var loadTask = Task.Run<BetterContinentsSettings?>(() =>
                {
                    var package = WorldCache.LoadCacheItem(id);
                    // Recalculate the id again to confirm it really matches
                    string localId = WorldCache.PackageID(package);
                    if (id != localId)
                    {
                        return null;
                    }
                    return BetterContinentsSettings.Load(package);
                });

                try
                {
                    UI.Add("LoadingFromCache", () => UI.DisplayMessage($"Better Continents: initializing from cached config"));
                    yield return new WaitUntil(() => loadTask.IsCompleted);
                }
                finally
                {
                    UI.Remove("LoadingFromCache");
                }
                
                if (loadTask.IsFaulted || loadTask.Result == null)
                {
                    LastConnectionError = loadTask.Exception != null
                        ? $"Better Continents: cached world settings failed to load ({loadTask.Exception.Message}), please reconnect to download them again!"
                        : $"Better Continents: cached world settings are corrupted, please reconnect to download them again!";
                    
                    LogError(LastConnectionError);
                    m_connectionStatus.SetValue(null, ZNet.ConnectionStatus.ErrorConnectFailed);
                    ZNet.instance.Disconnect(peer);
                    WorldCache.DeleteCacheItem(id);
                    yield break;
                }

                Settings = loadTask.Result.Value;
                Settings.Dump();


                // We only care about server/client version match when the server sends a world that actually uses the mod
                if (Settings.EnabledForThisWorld && ServerVersion != ModInfo.Version)
                {
                    LastConnectionError = $"Better Continents: world has the mod enabled, but server {ServerVersion} and client {ModInfo.Version} versions don't match";
                    LogError(LastConnectionError);
                    m_connectionStatus.SetValue(null, ZNet.ConnectionStatus.ErrorConnectFailed);
                    ZNet.instance.Disconnect(peer);
                }
                else if (!Settings.EnabledForThisWorld)
                {
                    Log($"Server world does not have Better Continents enabled, skipping version check");
                }
                
                peer.m_rpc.Invoke("BetterContinentsReady", 0);
            }

            private static IEnumerator ReceivedSettings(ZNetPeer peer)
            {
                int finalHash = GetHashCode(SettingsReceiveBuffer);
                if (finalHash == SettingsReceiveHash)
                {
                    Log($"Settings transfer complete, unpacking them now");
                    
                    var loadingTask = Task.Run(() => {
                        var settingsPkg = new ZPackage(SettingsReceiveBuffer);
                        var settings = BetterContinentsSettings.Load(settingsPkg);
                        WorldCache.Add(settingsPkg);
                        return settings;
                    });

                    try
                    {
                        UI.Add("ReceivedSettings", () => UI.DisplayMessage($"Better Continents: initializing from server config"));
                        yield return new WaitUntil(() => loadingTask.IsCompleted);
                    }
                    finally
                    {
                        UI.Remove("ReceivedSettings");
                    }

                    if (loadingTask.IsFaulted)
                    {
                        LastConnectionError = $"Better Continents: cached world settings failed to load ({loadingTask.Exception.Message}), please reconnect to download them again!";
                        LogError(LastConnectionError);
                        m_connectionStatus.SetValue(null, ZNet.ConnectionStatus.ErrorConnectFailed);
                        ZNet.instance.Disconnect(peer);
                        yield break;
                    }

                    Settings = loadingTask.Result;
                    Settings.Dump();

                    // We only care about server/client version match when the server sends a world that actually uses the mod
                    if (Settings.EnabledForThisWorld && ServerVersion != ModInfo.Version)
                    {
                        LastConnectionError = $"Better Continents: world has Better Continents enabled, but server {ServerVersion} and client {ModInfo.Version} mod versions don't match";
                        LogError(LastConnectionError);
                        m_connectionStatus.SetValue(null, ZNet.ConnectionStatus.ErrorConnectFailed);
                        ZNet.instance.Disconnect(peer);
                    }
                    else if (!Settings.EnabledForThisWorld)
                    {
                        Log($"Server world does not have Better Continents enabled, skipping version check");
                    }

                    peer.m_rpc.Invoke("BetterContinentsReady", 0);
                }
                else
                {
                    LogError($"{LastConnectionError}: hash mismatch, got {finalHash}, expected {SettingsReceiveHash}");
                    m_connectionStatus.SetValue(null, ZNet.ConnectionStatus.ErrorConnectFailed);
                    ZNet.instance.Disconnect(peer);
                }
            }


            [HarmonyPrefix, HarmonyPatch("RPC_Error")]
            private static void RPC_ErrorPrefix(ref int error)
            {
                if (error == 69)
                {
                    LastConnectionError = $"Better Continents: local mod version doesn't match the servers (local one is {ModInfo.Version}, server one is unknown)";
                    error = (int)ZNet.ConnectionStatus.ErrorConnectFailed;
                }
            }

            // Send our clients the settings for the currently loaded world. We do this before
            // the body of the RPC_PeerInfo function, so as to ensure the data is all sent to the client before they need it.
            [HarmonyPrefix, HarmonyPatch("RPC_PeerInfo")]
            private static bool RPC_PeerInfoPrefix(ZNet __instance, ZRpc rpc, ZPackage pkg)
            {
                if (__instance.IsServer() && Settings.EnabledForThisWorld)
                {
                    __instance.StartCoroutine(SendSettings(__instance, rpc, pkg));

                    return false;
                }
                else if (__instance.IsServer())
                {
                    Log($"World doesn't use Better Continents, skipping version check and sync");
                }
                return true;
            }

            // This function will be fixed up to point at the un-prefixed version of the original function (it could have modifications though)
            [HarmonyReversePatch]
            [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
            public static void RPC_PeerInfo(object instance, ZRpc rpc, ZPackage pkg)
            {
                throw new NotImplementedException("RPC_PeerInfo function was not patched correctly!");
            }

            private static IEnumerator SendSettings(ZNet instance, ZRpc rpc, ZPackage pkg)
            {
                byte[] ArraySlice(byte[] source, int offset, int length)
                {
                    var target = new byte[length];
                    Buffer.BlockCopy(source, offset, target, 0, length);
                    return target;
                }

                var peer = instance.GetPeers().First(p => p.m_rpc == rpc);

                if (!Settings.EnabledForThisWorld)
                {
                    Log($"Skipping sending settings to {PeerName(peer)}, as Better Continents is not enabled in this world");
                }
                else
                {
                    Log($"World is using Better Continents, so client version must match server version {ModInfo.Name}");

                    if (!ClientInfo.TryGetValue(peer.m_uid, out var bcClientInfo))
                    {
                        Log($"Client info for {PeerName(peer)} not found, client has an old version of Better Continents, or none!");
                        rpc.Invoke("Error", ZNet.ConnectionStatus.ErrorConnectFailed);
                        ZNet.instance.Disconnect(peer);
                        yield break;
                    }
                    else if (bcClientInfo.version != ModInfo.Version)
                    {
                        Log($"Client {PeerName(peer)} version {bcClientInfo.version} doesn't match server version {ModInfo.Version}");
                        peer.m_rpc.Invoke("Error", 69);
                        ZNet.instance.Disconnect(peer);
                        yield break;
                    }
                    else
                    {
                        Log($"Client {PeerName(peer)} version {bcClientInfo.version} matches server version {ModInfo.Version}");
                    }
                    
                    // This was the initial way that versioning was implemented, before the client->server way, so may
                    // as well leave it in
                    Log($"Sending server version {ModInfo.Version} to client for bi-lateral version agreement");
                    rpc.Invoke("BetterContinentsVersion", ModInfo.Version);

                    var settingsPackage = new ZPackage();
                    var cleanSettings = Settings.Clean();
                    cleanSettings.Serialize(settingsPackage);

                    if (WorldCache.CacheItemExists(settingsPackage, bcClientInfo.worldCache))
                    {
                        // We send hash and id
                        string cacheId = WorldCache.PackageID(settingsPackage);
                        Log($"Client {PeerName(peer)} already has cached settings for world, instructing it to load those (id {cacheId})");
                        rpc.Invoke("BetterContinentsConfigLoadFromCache", cacheId);
                    }
                    else
                    {
                        Log($"Client {PeerName(peer)} doesn't have cached settings, sending them now");
                        cleanSettings.Dump();
                        
                        var settingsData = settingsPackage.GetArray();
                        Log($"Sending settings package header for {settingsData.Length} byte stream");
                        rpc.Invoke("BetterContinentsConfigStart", settingsData.Length, GetHashCode(settingsData));

                        const int SendChunkSize = 256 * 1024;

                        for (int sentBytes = 0; sentBytes < settingsData.Length;)
                        {
                            int packetSize = Mathf.Min(settingsData.Length - sentBytes, SendChunkSize);
                            var packet = ArraySlice(settingsData, sentBytes, packetSize);
                            rpc.Invoke("BetterContinentsConfigPacket", sentBytes, GetHashCode(packet),
                                new ZPackage(packet));
                            // Make sure to flush or we will saturate the queue...
                            rpc.GetSocket().Flush();
                            sentBytes += packetSize;
                            Log($"Sent {sentBytes} of {settingsData.Length} bytes");
                            float timeout = Time.time + 30;
                            yield return new WaitUntil(() => rpc.GetSocket().GetSendQueueSize() < SendChunkSize || Time.time > timeout);
                            if (Time.time > timeout)
                            {
                                Log($"Timed out sending config to client {PeerName(peer)} after 30 seconds, disconnecting them");
                                peer.m_rpc.Invoke("Error", ZNet.ConnectionStatus.ErrorConnectFailed);
                                ZNet.instance.Disconnect(peer);
                                yield break;
                            }
                        }
                    }
                    yield return new WaitUntil(() => ClientInfo[peer.m_uid].readyForPeerInfo || !peer.m_socket.IsConnected());
                }

                RPC_PeerInfo(instance, rpc, pkg);
            }
        }

        public static string CleanPath(string path) => path?.Replace("\\\"", "").Replace("\"", "").Trim();
    }
}