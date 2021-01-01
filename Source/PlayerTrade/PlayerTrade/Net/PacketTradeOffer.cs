﻿using System;
using System.Collections.Generic;
using Verse;

namespace PlayerTrade.Net
{
    public class PacketTradeOffer : Packet
    {
        public Guid Guid;
        public string For;
        public string From;
        public List<NetTradeThing> TradeThings = new List<NetTradeThing>();

        public override void Write(PacketBuffer buffer)
        {
            buffer.WriteGuid(Guid);
            buffer.WriteString(For);
            buffer.WriteString(From);

            buffer.WriteInt(TradeThings.Count);
            foreach (NetTradeThing thing in TradeThings)
                thing.Write(buffer);
        }

        public override void Read(PacketBuffer buffer)
        {
            Guid = buffer.ReadGuid();
            For = buffer.ReadString();
            From = buffer.ReadString();

            TradeThings.Clear();
            int tradeThingsCount = buffer.ReadInt();
            for (int i = 0; i < tradeThingsCount; i++)
            {
                var tradeThing = new NetTradeThing();
                tradeThing.Read(buffer);
                TradeThings.Add(tradeThing);
            }
        }

        public TradeOffer ToTradeOffer()
        {
            var offer = new TradeOffer
            {
                Guid = Guid,
                Fresh = true,
                For = For,
                From = From,
            };

            foreach (NetTradeThing tradeThing in TradeThings)
                offer.Things.Add(tradeThing.ToTradeThing());

            return offer;
        }

        public static PacketTradeOffer MakePacket(TradeOffer offer)
        {
            var packet = new PacketTradeOffer
            {
                Guid = offer.Guid,
                For = offer.For,
                From = offer.From
            };

            // Populate trade things
            foreach (TradeOffer.TradeThing tradeThing in offer.Things)
                packet.TradeThings.Add(NetTradeThing.FromTradeThing(tradeThing));

            return packet;
        }
    }
}