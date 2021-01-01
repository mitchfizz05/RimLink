﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Ionic.Zlib;
using Verse;

namespace PlayerTrade.Net
{
    public class Connection
    {
        public event EventHandler<PacketReceivedEventArgs> PacketReceived;

        public TcpClient Tcp;
        public NetworkStream Stream;

        public async Task SendPacket(Packet packet)
        {
            var pair = Packet.Packets.FirstOrDefault(p => p.Value == packet.GetType());

            byte[] buffer;
            using (var stream = new MemoryStream())
            {
                using (var gzip = new GZipStream(stream, CompressionMode.Compress))
                {
                    var packetBuffer = new PacketBuffer(gzip);
                    try
                    {
                        // Write packet data
                        packet.Write(packetBuffer);
                    }
                    catch (Exception e)
                    {
                        Log.Error("Exception writing packet", e);
                    }
                }
                buffer = stream.ToArray();
            }

            // Send packet ID and length
            await Stream.WriteAsync(BitConverter.GetBytes(pair.Key).Concat(BitConverter.GetBytes(buffer.Length)).ToArray(), 0, 8);
            // Send packet content
            await Stream.WriteAsync(buffer, 0, buffer.Length);
        }

        public async Task Disconnect()
        {
            try
            {
                // Sending 4 0 bytes (0 int32) will trigger a disconnect on the server (packet ID 0)
                await Stream.WriteAsync(new byte[] {0x00, 0x00, 0x00, 0x00}, 0, 4);
            } catch (Exception){}

            Tcp?.Close();
        }

        public async Task<Packet> ReceivePacket()
        {
            byte[] buffer = new byte[8];
            // Read packet ID and length
            await Stream.ReadAsync(buffer, 0, 8);
            int packetId = BitConverter.ToInt32(buffer, 0);
            int packetLength = BitConverter.ToInt32(buffer, 4);

            if (packetId == 0)
            {
                // Special disconnect packet ID. Handy because we get zeros when things go wrong so this just triggers a disconnect
                Tcp.Close();
                return null;
            }

            // Do some sanity checking
            if (packetLength > 3145728) // 3MiB max size
                throw new Exception("Packet over max size (3MiB) - possible packet overflow");
            if (!Packet.Packets.ContainsKey(packetId))
                throw new Exception($"Packet ID {packetId} doesn't exist");

            // Read packet content
            byte[] packetContentBuffer = new byte[packetLength];
            if (packetLength > 0)
                await Stream.ReadAsync(packetContentBuffer, 0, packetLength);

            // Instantiate packet
            Packet packet = (Packet) Activator.CreateInstance(Packet.Packets[packetId]);

            if (packetLength > 0)
            {
                // Parse packet data
                using (var gzip = new GZipStream(new MemoryStream(packetContentBuffer), CompressionMode.Decompress))
                {
                    packet.Read(new PacketBuffer(gzip));
                }
            }

            PacketReceived?.Invoke(this, new PacketReceivedEventArgs(packetId, packet));

            return packet;
        }
    }
}