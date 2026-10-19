using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace LateJoin.Client;

[BepInPlugin("com.tolian.latejoin", "LateJoin", "0.1.0")]
[BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
[BepInProcess("EscapeFromTarkov.exe")]
public sealed class LateJoinPlugin : BaseUnityPlugin
{
    private const string HarmonyId = "com.tolian.latejoin";
    private static readonly HashSet<string> ActiveRaidServers = new HashSet<string>(StringComparer.Ordinal);
    private static ManualLogSource _logger;
    private Harmony _harmony;
    private static bool _lateJoinRequested;

    private void Awake()
    {
        _logger = Logger;
        _harmony = new Harmony(HarmonyId);

        Patch(
            "Fika.Core.Networking.FikaServer",
            "OnConnectionRequest",
            nameof(AcceptFikaConnectionPrefix));
        Patch(
            "Fika.Core.Networking.FikaPingingClient",
            "AttemptToPingHost",
            nameof(PingActiveRaidAsReconnectPrefix));
        Patch(
            "Fika.Core.Networking.FikaPingingClient",
            "OnNetworkReceiveUnconnected",
            nameof(AcceptActiveRaidInProgressPrefix));
        Patch(
            "Fika.Core.UI.Custom.MatchMakerUIScript",
            "RefreshUI",
            nameof(EnableActiveRaidEntriesPrefix));
        Patch(
            "Fika.Core.UI.Custom.MatchMakerUIScript",
            "JoinMatch",
            nameof(RecordLateJoinPrefix));
        Patch(
            "Fika.Core.Main.GameMode.ClientGameController",
            "WaitForHostInit",
            nameof(RequestLiveHostStatePrefix));
        Patch(
            "Fika.Core.Main.Utils.FikaBackendUtils",
            "CleanUpVariables",
            nameof(ResetLateJoinPostfix));

        Logger.LogInfo("LateJoin active-raid connection and host-state patches loaded.");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }

    private void Patch(string typeName, string methodName, string patchName)
    {
        var targetType = AccessTools.TypeByName(typeName);
        if (targetType == null)
        {
            throw new TypeLoadException($"Could not find required Fika type '{typeName}'.");
        }

        var target = AccessTools.Method(targetType, methodName);
        if (target == null)
        {
            throw new MissingMethodException(targetType.FullName, methodName);
        }

        var patch = AccessTools.Method(typeof(LateJoinPlugin), patchName);
        if (patch == null)
        {
            throw new MissingMethodException(typeof(LateJoinPlugin).FullName, patchName);
        }

        _harmony.Patch(target, prefix: new HarmonyMethod(patch));
        Logger.LogInfo($"Patched {targetType.FullName}.{methodName}.");
    }

    private static bool AcceptFikaConnectionPrefix(object __instance, object[] __args)
    {
        if (__args.Length == 0 || __args[0] == null)
        {
            throw new InvalidOperationException("Fika passed an empty connection request.");
        }

        var request = __args[0];
        var data = ReadMember(request, "Data");
        var originalPosition = ReadMember(data, "Position");
        var getString = data.GetType().GetMethod("GetString", Type.EmptyTypes);
        if (getString == null)
        {
            throw new MissingMethodException(data.GetType().FullName, "GetString");
        }

        var key = getString.Invoke(data, null) as string;
        var setPosition = data.GetType().GetMethod("SetPosition", new[] { typeof(int) })
            ?? throw new MissingMethodException(data.GetType().FullName, "SetPosition");
        setPosition.Invoke(data, new[] { originalPosition });

        if (!IsRaidRunning(__instance))
        {
            return true;
        }

        if (!string.Equals(key, "fika.core", StringComparison.Ordinal))
        {
            return true;
        }

        var accept = request.GetType().GetMethod("Accept", Type.EmptyTypes);
        if (accept == null)
        {
            throw new MissingMethodException(request.GetType().FullName, "Accept");
        }

        accept.Invoke(request, null);
        _logger.LogInfo("Accepted a Fika client using its normal connection key.");
        return false;
    }

    private static void PingActiveRaidAsReconnectPrefix(ref bool reconnect)
    {
        if (_lateJoinRequested && !reconnect)
        {
            reconnect = true;
            _logger.LogInfo("Sending an active-raid Fika discovery ping.");
        }
    }

    private static bool AcceptActiveRaidInProgressPrefix(object __instance, object[] __args)
    {
        if (!_lateJoinRequested || __args.Length < 2 || __args[0] is not IPEndPoint endpoint)
        {
            return true;
        }

        var reader = __args[1];
        var originalPosition = ReadMember(reader, "Position");
        var tryGetString = reader.GetType().GetMethod("TryGetString", new[] { typeof(string).MakeByRefType() });
        if (tryGetString == null)
        {
            throw new MissingMethodException(reader.GetType().FullName, "TryGetString");
        }

        var arguments = new object[] { null };
        var parsed = tryGetString.Invoke(reader, arguments) is bool success && success;
        var setPosition = reader.GetType().GetMethod("SetPosition", new[] { typeof(int) })
            ?? throw new MissingMethodException(reader.GetType().FullName, "SetPosition");
        setPosition.Invoke(reader, new[] { originalPosition });

        if (!parsed || !string.Equals(arguments[0] as string, "fika.inprogress", StringComparison.Ordinal))
        {
            return true;
        }

        var client = __instance;
        var endPoints = ReadMember(client, "_endPoints") as IList
            ?? throw new InvalidOperationException("Fika ping endpoint list is missing.");
        if (!endPoints.Cast<object>().Any(candidate => Equals(candidate, endpoint)))
        {
            return true;
        }

        WriteMember(client, "InProgress", true);

        var candidates = ReadMember(client, "_candidates") as IList
            ?? throw new InvalidOperationException("Fika ping candidate list is missing.");
        if (!candidates.Cast<object>().Any(candidate => Equals(candidate, endpoint)))
        {
            candidates.Add(endpoint);
        }

        for (var i = endPoints.Count - 1; i >= 0; i--)
        {
            if (Equals(endPoints[i], endpoint))
            {
                endPoints.RemoveAt(i);
            }
        }

        if (ReadMember(client, "_hasResponse") is not bool receivedResponse || !receivedResponse)
        {
            var timeType = AccessTools.TypeByName("UnityEngine.Time")
                ?? throw new TypeLoadException("Could not find UnityEngine.Time.");
            var realtime = AccessTools.Property(timeType, "realtimeSinceStartup")
                ?? throw new MissingMemberException(timeType.FullName, "realtimeSinceStartup");
            WriteMember(client, "_firstResponseTime", Convert.ToSingle(realtime.GetValue(null)));
            WriteMember(client, "_hasResponse", true);
        }

        _logger.LogInfo($"Accepted Fika in-progress response as a late-join candidate: {endpoint}.");
        return false;
    }

    private static bool RecordLateJoinPrefix(object[] __args)
    {
        _lateJoinRequested = __args.Length > 1
            && __args[1] is string serverId
            && ActiveRaidServers.Contains(serverId);

        if (_lateJoinRequested)
        {
            _logger.LogInfo($"Starting a late join to Fika server {__args[1]}.");
        }

        return true;
    }

    private static bool RequestLiveHostStatePrefix(ref IEnumerator __result)
    {
        if (!_lateJoinRequested)
        {
            return true;
        }

        __result = RequestHostStateUntilReady();
        return false;
    }

    private static IEnumerator RequestHostStateUntilReady()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var nextRequest = DateTime.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            var client = GetSingleton("Fika.Core.Networking.FikaClient");
            if (ReadMember(client, "HostReady") is bool ready && ready)
            {
                _logger.LogInfo("Received authoritative active-raid state from the Fika host.");
                yield break;
            }

            if (DateTime.UtcNow >= nextRequest)
            {
                SendHostInformationRequest(client);
                nextRequest = DateTime.UtcNow.AddMilliseconds(500);
            }

            yield return null;
        }

        throw new TimeoutException("Fika host did not provide active-raid state within 30 seconds.");
    }

    private static void SendHostInformationRequest(object client)
    {
        var packetType = AccessTools.TypeByName("Fika.Core.Networking.Packets.Backend.InformationPacket")
            ?? throw new TypeLoadException("Could not find Fika InformationPacket.");
        var deliveryMethodType = AccessTools.TypeByName("Fika.Core.Networking.LiteNetLib.DeliveryMethod")
            ?? throw new TypeLoadException("Could not find LiteNetLib DeliveryMethod.");
        var reliableOrdered = Enum.Parse(deliveryMethodType, "ReliableOrdered");

        var sendData = client.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
            {
                if (method.Name != "SendData" || !method.IsGenericMethodDefinition)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 3
                    && parameters[0].ParameterType.IsByRef
                    && parameters[1].ParameterType == deliveryMethodType;
            })
            ?? throw new MissingMethodException(client.GetType().FullName, "SendData<T>(ref T, DeliveryMethod, bool)");

        var packet = Activator.CreateInstance(packetType)
            ?? throw new InvalidOperationException("Could not create Fika InformationPacket.");
        sendData.MakeGenericMethod(packetType).Invoke(client, new[] { packet, reliableOrdered, false });
    }

    private static bool IsRaidRunning(object __instance)
    {
        if (__instance == null)
        {
            return false;
        }

        var coopHandler = ReadMember(__instance, "CoopHandler");
        if (coopHandler == null)
        {
            return false;
        }

        var game = ReadMember(coopHandler, "LocalGameInstance");
        if (game == null)
        {
            return false;
        }

        var controller = ReadMember(game, "GameController");
        return ReadMember(controller, "RaidStarted") is bool started && started;
    }

    private static void EnableActiveRaidEntriesPrefix(object __instance)
    {
        ActiveRaidServers.Clear();
        var matches = ReadMember(__instance, "_matches") as Array;
        if (matches == null)
        {
            return;
        }

        var settings = ReadMember(__instance, "RaidSettings");
        var profileId = ReadMember(__instance, "_profileId");
        var selectedSide = ReadMember(settings, "Side");
        var selectedTime = ReadMember(settings, "SelectedDateTime");
        var entryType = matches.GetType().GetElementType()
            ?? throw new InvalidOperationException("Fika lobby entries have no element type.");
        var statusField = AccessTools.Field(entryType, "Status")
            ?? throw new MissingFieldException(entryType.FullName, "Status");
        var sideField = AccessTools.Field(entryType, "Side")
            ?? throw new MissingFieldException(entryType.FullName, "Side");
        var timeField = AccessTools.Field(entryType, "Time")
            ?? throw new MissingFieldException(entryType.FullName, "Time");
        var inGame = Enum.Parse(statusField.FieldType, "IN_GAME");
        var complete = Enum.Parse(statusField.FieldType, "COMPLETE");

        for (var i = 0; i < matches.Length; i++)
        {
            var entry = matches.GetValue(i);
            if (entry == null || !Equals(statusField.GetValue(entry), inGame))
            {
                continue;
            }

            var serverId = ReadMember(entry, "ServerId") as string;
            if (!string.IsNullOrEmpty(serverId))
            {
                ActiveRaidServers.Add(serverId);
            }

            if (IsProfileInRaid(ReadMember(entry, "Players") as IDictionary, profileId))
            {
                continue;
            }

            statusField.SetValue(entry, complete);
            sideField.SetValue(entry, selectedSide);
            timeField.SetValue(entry, selectedTime);
            matches.SetValue(entry, i);
        }
    }

    private static bool IsProfileInRaid(IDictionary players, object profileId)
    {
        if (players == null || profileId == null)
        {
            return false;
        }

        var expectedProfileId = profileId.ToString();
        foreach (DictionaryEntry player in players)
        {
            if (string.Equals(player.Key?.ToString(), expectedProfileId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ResetLateJoinPostfix()
    {
        _lateJoinRequested = false;
        ActiveRaidServers.Clear();
    }

    private static object GetSingleton(string typeName)
    {
        var instanceType = AccessTools.TypeByName(typeName)
            ?? throw new TypeLoadException($"Could not find required Fika type '{typeName}'.");
        var singletonType = AccessTools.TypeByName("Comfort.Common.Singleton`1")
            ?? throw new TypeLoadException("Could not find Comfort.Common.Singleton<T>.");
        var closedSingletonType = singletonType.MakeGenericType(instanceType);
        var instanceProperty = AccessTools.Property(closedSingletonType, "Instance")
            ?? throw new MissingMemberException(closedSingletonType.FullName, "Instance");

        return instanceProperty.GetValue(null)
            ?? throw new InvalidOperationException($"{typeName} singleton has not been initialized.");
    }

    private static object ReadMember(object instance, string name)
    {
        if (instance == null)
        {
            throw new InvalidOperationException($"Cannot read '{name}' from a null Fika object.");
        }

        var type = instance.GetType();
        var field = AccessTools.Field(type, name);
        if (field != null)
        {
            return field.GetValue(instance);
        }

        var property = AccessTools.Property(type, name);
        if (property != null)
        {
            return property.GetValue(instance);
        }

        throw new MissingMemberException(type.FullName, name);
    }

    private static void WriteMember(object instance, string name, object value)
    {
        if (instance == null)
        {
            throw new InvalidOperationException($"Cannot write '{name}' on a null Fika object.");
        }

        var type = instance.GetType();
        var field = AccessTools.Field(type, name);
        if (field != null)
        {
            field.SetValue(instance, value);
            return;
        }

        var property = AccessTools.Property(type, name);
        if (property?.CanWrite == true)
        {
            property.SetValue(instance, value);
            return;
        }

        throw new MissingMemberException(type.FullName, name);
    }
}
