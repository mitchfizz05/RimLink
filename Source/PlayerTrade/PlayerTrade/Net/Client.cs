﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using PlayerTrade.Chat;
using PlayerTrade.Mail;
using PlayerTrade.Mechanoids;
using PlayerTrade.Missions;
using PlayerTrade.Net.Packets;
using PlayerTrade.Raids;
using PlayerTrade.Trade;
using PlayerTrade.Trade.Packets;
using PlayerTrade.Util;
using RimWorld;
using UnityEngine;
using Verse;

namespace PlayerTrade.Net
{
    public class Client : Connection
    {
        public const float TimeoutThresholdSeconds = 6f;

        public event EventHandler Connected;
        public event EventHandler<PlayerUpdateEventArgs> PlayerUpdated;
        public event EventHandler<Player> PlayerConnected;
        public event EventHandler<Player> PlayerDisconnected;

        public new event EventHandler<PacketReceivedEventArgs> PacketReceived; 

        public delegate bool PacketPredicate(Packet packet);

        public RimLinkComp RimLinkComp;
        public TradeSystem Trade;
        public MissionSystem Mission;
        public ChatSystem Chat;

        public ClientState State = ClientState.Disconnected;
        public float LastHeartbeat;

        public Player Player { get; private set; }
        public string Guid => RimLinkComp.Guid; // Unique user ID

        public GameSettings GameSettings;

        public Dictionary<string, Player> OnlinePlayers = new Dictionary<string, Player>();

        private readonly List<AwaitPacketRequest> _awaitingPackets = new List<AwaitPacketRequest>();

        private Queue<Packet> _pendingPackets = new Queue<Packet>();
        
        public Client(RimLinkComp rimLinkComp)
        {
            RimLinkComp = rimLinkComp;
            MarkDirty(false, true);

            Disconnected += OnDisconnected;
            PacketReceived += OnPacketReceived;
            PlayerUpdated += OnPlayerUpdated;
            PlayerConnected += OnPlayerConnected;
            PlayerDisconnected += OnPlayerDisconnected;

            // Workers
            Trade = new TradeSystem(this);
            new RaidSystem(this);
            Mission = new MissionSystem(this);
            new MailSystem(this);
            Chat = new ChatSystem(this);
            new MechanoidSystem(this);
        }

        public async Task Connect(string ip, int port = 35562)
        {
            Tcp?.Close();

            Tcp = new TcpClient();
            try
            {
                Log.Message("Connecting to: " + ip + ":" + port);
                await Tcp.ConnectAsync(ip, port);
            }
            catch (Exception e)
            {
                throw new ConnectionFailedException(e.Message, true, e);
            }
            IsConnected = true;
            State = ClientState.Connected;
            Log.Message("TCP connection established.");

            Stream = Tcp.GetStream();

            // Send connect request
            SendPacket(new PacketConnect
            {
                ProtocolVersion = RimLinkMod.ProtocolVersion,
                Guid = Guid,
                Secret = RimLinkComp.Secret,
                Player = Player
            });

            Run();

            // Await connection response
            PacketConnectResponse response = (PacketConnectResponse) await AwaitPacket(packet => packet is PacketConnectResponse, 2000);
            if (response == null)
            {
                Tcp.Close();
                throw new ConnectionFailedException("No connect response received. Is the server running and reachable?", true);
            }

            if (!response.Success)
            {
                Tcp.Close();
                throw new ConnectionFailedException("Server refused connection: " + response.FailReason, response.AllowReconnect);
            }

            Log.Message("Connected!");
            Log.Message($"GameSettings: RaidBasePrice={response.Settings.RaidBasePrice} MaxRaidStrength={response.Settings.RaidMaxStrengthPercent} Anticheat={response.Settings.Anticheat}");

            State = ClientState.Authenticated;

            GameSettings = response.Settings;
            Connected?.Invoke(this, EventArgs.Empty);
        }

        public void Run()
        {
            _ = SendPackets();
            _ = ReceivePackets();
        }

        private async Task ReceivePackets()
        {
            while (Tcp.Connected)
            {
                try
                {
                    Packet packet = await ReceivePacket();
                    if (packet == null)
                        break;
                    _pendingPackets.Enqueue(packet);
                }
                catch (Exception e)
                {
                    Log.Error($"Error receiving packet ({e.GetType().Name})", e);
                }
            }
        }

        private async Task SendPackets()
        {
            while (Tcp.Connected)
            {
                await SendQueuedPackets();
            }
        }

        public override void Disconnect(bool sendDisconnectPacket = true)
        {
            _awaitingPackets.Clear();
            base.Disconnect(sendDisconnectPacket);
        }

        public void Update()
        {
            // Process received packets pending in queue
            while (_pendingPackets.Count > 0)
            {
                Packet packet = _pendingPackets.Dequeue();
                PacketReceived?.Invoke(this, new PacketReceivedEventArgs(Packet.Packets.First(p => p.Value == packet.GetType()).Key, packet));
            }

            // Check if we've timed out
            if (State == ClientState.Authenticated && LastHeartbeat > 0f && Time.realtimeSinceStartup - LastHeartbeat > TimeoutThresholdSeconds)
            {
                // Timed out
                Log.Message("Connection timed out");
                Disconnect();
            }

            Mission.Update();
        }

        public void MarkDirty(bool sendPacket = true, bool mapIndependent = false)
        {
            Player = Player.Self(mapIndependent);
            OnlinePlayers[Guid] = Player; // add ourselves to the player list
            if (sendPacket && State == ClientState.Authenticated)
                SendColonyInfo();
        }

        public Player GetPlayer(string guid)
        {
            foreach (Player player in GetPlayers(includeSelf: true))
            {
                if (player.Guid == guid)
                    return player;
            }

            return null;
        }

        public string GetName(string guid, bool colored = false)
        {
            Player player = GetPlayer(guid);
            if (player != null)
                return colored ? player.Name.Colorize(player.Color.ToColor()) : player.Name;

            // Fallback to just showing GUID
            return "{" + guid + "}";
        }

        public IEnumerable<Player> GetPlayers(bool online = false, bool includeSelf = false)
        {
            // Get online playres
            foreach (Player player in OnlinePlayers.Values)
            {
                if (!includeSelf && player.IsUs)
                    continue; // Skip self
                yield return player;
            }

            if (!online)
            {
                // Get offline players
                foreach (Player player in RimLinkComp.RememberedPlayers.Where(p => !p.IsOnline))
                {
                    if (OnlinePlayers.ContainsKey(player.Guid))
                        continue; // This player is online

                    yield return player;
                }
            }
        }

        public void SendColonyInfo()
        {
            SendPacket(new PacketColonyInfo
            {
                Guid = RimLinkComp.Instance.Guid,
                Player = Player
            });
        }

        public async Task<Packet> AwaitPacket(PacketPredicate predicate, int timeout = 0)
        {
            var source = new TaskCompletionSource<Packet>();
            var request = new AwaitPacketRequest
            {
                Predicate = predicate,
                CompletionSource = source
            };

            lock (_awaitingPackets)
            {
                _awaitingPackets.Add(request);
            }

            if (timeout > 0)
            {
                if (await Task.WhenAny(source.Task, Task.Delay(timeout)) == source.Task)
                {
                    // Success
                    Packet result = await source.Task;
                    Log.Message($"Awaited packet received {result.GetType().Name}");
                    return result;
                }

                // Timed out
                return null;

            }
            else
            {
                // No timeout
                Packet result = await source.Task;
                Log.Message($"Awaited packet received {result.GetType().Name}");
                return result;
            }
        }

        private void OnDisconnected(object sender, EventArgs e)
        {
            State = ClientState.Disconnected;
        }

        private void OnPacketReceived(object sender, PacketReceivedEventArgs e)
        {
            if (!e.Packet.Attribute.HideFromLog)
                Log.Message($"Packet received #{e.Id} ({e.Packet.GetType().Name})");

            // Check awaiting packets
            lock (_awaitingPackets)
            {
                var toRemove = new List<AwaitPacketRequest>();

                foreach (var awaiting in _awaitingPackets)
                {
                    try
                    {
                        if (awaiting.Predicate(e.Packet))
                        {
                            toRemove.Add(awaiting);
                            awaiting.CompletionSource?.TrySetResult(e.Packet);
                        }
                    }
                    catch (Exception awaitException)
                    {
                        Log.Error("Exception processing awaited packet!", awaitException);
                        _awaitingPackets.Clear();
                        Disconnect();
                    }
                }

                _awaitingPackets.RemoveAll(toRemove);
            }

            try
            {
                switch (e.Packet)
                {
                    case PacketDisconnect _:
                    {
                        Disconnect(false);
                        break;
                    }

                    case PacketHeartbeat _:
                    {
                        LastHeartbeat = Time.realtimeSinceStartup;
                        SendPacket(new PacketHeartbeat());
                        break;
                    }

                    case PacketKick kickPacket:
                    {
                        if (!kickPacket.AllowReconnect) // Disable auto reconnect
                            RimLinkComp.ReconnectOnNextDisconnect = false;

                        if (kickPacket.Reason != null)
                        {
                            // Show reason
                            Find.WindowStack.Add(new Dialog_MessageBox(kickPacket.Reason, title: "Kicked",
                                buttonAText: "Close"));
                        }

                        break;
                    }

                    case PacketColonyInfo infoPacket:
                    {
                        //Log.Message($"Received colony info update for {infoPacket.Player.Name} (0tradeable = {infoPacket.Player.TradeableNow})");
                        Player oldPlayer = OnlinePlayers.ContainsKey(infoPacket.Guid) ? OnlinePlayers[infoPacket.Guid] : null;
                        if (oldPlayer == null)
                        {
                            // New connection
                            Log.Message($"Player {infoPacket.Player.Name} connected");
                            PlayerConnected?.Invoke(this, infoPacket.Player);
                        }

                        OnlinePlayers[infoPacket.Guid] = infoPacket.Player;
                        PlayerUpdated?.Invoke(this, new PlayerUpdateEventArgs(oldPlayer, infoPacket.Player));
                        break;
                    }

                    case PacketPlayerDisconnected playerDisconnectedPacket:
                    {
                        if (OnlinePlayers.ContainsKey(playerDisconnectedPacket.Player))
                        {
                            var disconnectedPlayer = OnlinePlayers[playerDisconnectedPacket.Player];
                            OnlinePlayers.Remove(playerDisconnectedPacket.Player);
                            PlayerDisconnected?.Invoke(this, disconnectedPlayer);
                        }

                        break;
                    }

                    case PacketAnnouncement announcementPacket:
                    {
                        AnnouncementUtility.Show(announcementPacket);
                        break;
                    }

                    case PacketRequestBugReport _:
                    {
                        BugReport.Send("Bug report requested via command.");
                        break;
                    }

                    case PacketGiveItem giveItemPacket:
                    {
                        try
                        {
                            giveItemPacket.GiveItem();
                            SendPacket(new PacketAcknowledgement
                            {
                                Guid = giveItemPacket.Reference,
                                Success = true
                            });
                        }
                        catch (Exception giveException)
                        {
                            SendPacket(new PacketAcknowledgement
                            {
                                Guid = giveItemPacket.Reference,
                                Success = false,
                                FailReason = giveException.Message
                            });
                        }

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Exception handling packet! ({e.Packet.GetType().Name})", ex);
            }
        }

        private void OnPlayerUpdated(object sender, PlayerUpdateEventArgs e)
        {
            // If this is a new player, or if their tradeable status has changed - issue a message
            if (e.OldPlayer == null || e.Player.TradeableNow != e.OldPlayer.TradeableNow)
            {
                if (e.Player.TradeableNow)
                {
                    Log.Message($"{e.Player.Name} is now tradable");
                    Messages.Message($"{e.Player.Name} is now tradable", def: MessageTypeDefOf.NeutralEvent, false);

                }
                else if (e.OldPlayer != null) // only show this message if the player previously existed
                {
                    Log.Message($"{e.Player.Name} no longer tradable");
                    Messages.Message($"{e.Player.Name} is no longer tradable", def: MessageTypeDefOf.NeutralEvent, false);
                }
            }
        }

        private void OnPlayerDisconnected(object sender, Player e)
        {
            Messages.Message($"{e.Name.Colorize(ColoredText.FactionColor_Neutral)} disconnected", MessageTypeDefOf.NeutralEvent, false);
        }

        private void OnPlayerConnected(object sender, Player e)
        {
            Messages.Message($"{e.Name.Colorize(ColoredText.FactionColor_Neutral)} connected", MessageTypeDefOf.NeutralEvent, false);
        }

        public class AwaitPacketRequest
        {
            public PacketPredicate Predicate;
            public TaskCompletionSource<Packet> CompletionSource;
        }

        public class PlayerUpdateEventArgs : EventArgs
        {
            public Player OldPlayer;
            public Player Player;

            public PlayerUpdateEventArgs(Player oldPlayer, Player player)
            {
                OldPlayer = oldPlayer;
                Player = player;
            }
        }

        public enum ClientState
        {
            Disconnected,
            Connected,
            Authenticated
        }
    }
}
