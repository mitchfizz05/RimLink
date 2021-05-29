﻿using PlayerTrade.Net;
using PlayerTrade.Net.Packets;

namespace PlayerTrade
{
    public class Colony : IPacketable
    {
        public Player Player
        {
            get
            {
                if (_cachedPlayer == null)
                    _cachedPlayer = RimLinkComp.Instance.Client?.GetPlayer(OwnerGuid);
                return _cachedPlayer;
            }
        }

        public string Guid => OwnerGuid + "_" + Id;
        
        public string OwnerGuid;
        public int Id;
        public string Name;
        public string Seed;
        public int Tile;

        private Player _cachedPlayer;

        public void Write(PacketBuffer buffer)
        {
            buffer.WriteString(OwnerGuid);
            buffer.WriteInt(Id);
            buffer.WriteString(Name);
            buffer.WriteString(Seed);
            buffer.WriteInt(Tile);
        }

        public void Read(PacketBuffer buffer)
        {
            OwnerGuid = buffer.ReadString();
            Id = buffer.ReadInt();
            Name = buffer.ReadString();
            Seed = buffer.ReadString();
            Tile = buffer.ReadInt();
        }
    }
}