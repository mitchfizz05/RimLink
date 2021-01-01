﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PlayerTrade.Net
{
    public abstract class Packet
    {
        public const int ConnectId = 1;
        public const int ColonyResourcesId = 2;
        public const int ColonyTradableId = 3;
        public const int InitiateTradeId = 4;
        public const int RequestColonyResourcesId = 5;
        public const int TradeOfferPacketId = 6;
        public const int AcceptTradePacketId = 7;
        public const int ConfirmTradePacketId = 8;

        public static Dictionary<int, Type> Packets = new Dictionary<int, Type>
        {
            {ConnectId, typeof(PacketConnect)},
            {ColonyResourcesId, typeof(PacketColonyResources)},
            {ColonyTradableId, typeof(PacketColonyTradable)},
            {InitiateTradeId, typeof(PacketInitiateTrade)},
            {RequestColonyResourcesId, typeof(PacketRequestColonyResources)},
            {TradeOfferPacketId, typeof(PacketTradeOffer)},
            {AcceptTradePacketId, typeof(PacketAcceptTrade)},
            {ConfirmTradePacketId, typeof(PacketTradeConfirm)},
        };

        public abstract void Write(PacketBuffer buffer);
        public abstract void Read(PacketBuffer buffer);
    }
}