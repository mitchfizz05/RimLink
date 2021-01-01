﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PlayerTrade.Net;
using RimWorld;
using UnityEngine;
using Verse;

namespace PlayerTrade
{
    public class TradeOffer : IExposable
    {
        public Guid Guid;
        public string From;
        public string For;

        public List<TradeThing> Things = new List<TradeThing>();

        /// <summary>
        /// Is a fresh trade offer? This will lose it's value when saved/loaded from disk, allowing trade offers to be invalidated when that happens.
        /// </summary>
        public bool Fresh;

        public TaskCompletionSource<bool> TradeAccepted = new TaskCompletionSource<bool>();

        public bool IsForUs => For == RimLinkComp.Find().Client.Username;

        public string GetTradeOfferString(out List<ThingDef> hyperlinks)
        {
            hyperlinks = new List<ThingDef>();

            var builder = new StringBuilder();
            builder.AppendLine($"{From} has presented a trade offer. They are offering...");

            foreach (TradeThing thing in Things)
            {
                if (thing.CountOffered <= 0 || thing.OfferedThings.Count == 0)
                    continue;
                builder.AppendLine($"      {thing.CountOffered}x {thing.OfferedThings.First().LabelCapNoCount}");

                hyperlinks.Add(thing.OfferedThings.First().def);
            }

            builder.AppendLine("In exchange for...");

            foreach (TradeThing thing in Things)
            {
                if (thing.CountOffered >= 0 || thing.RequestedThings.Count == 0)
                    continue;
                builder.AppendLine($"      {-thing.CountOffered}x {thing.RequestedThings.First().LabelCapNoCount}");

                hyperlinks.Add(thing.RequestedThings.First().def);
            }

            return builder.ToString();
        }

        public async Task Accept()
        {
            if (!IsForUs)
            {
                Log.Error($"Attempt to accept trade offer that isn't for us. (For = {For})");
                return;
            }

            Client client = RimLinkComp.Find().Client;

            Fresh = false; // Make trade no longer "fresh" (acceptable)

            // Send accept packet
            await client.SendPacket(new PacketAcceptTrade
            {
                Trade = Guid,
                Accept = true
            });
        }

        public async Task Reject()
        {
            Fresh = false; // Make trade no longer "fresh" (acceptable)

            await RimLinkComp.Find().Client.SendPacket(new PacketAcceptTrade
            {
                Trade = Guid,
                Accept = false
            });
        }

        /// <summary>
        /// Remove things being offered and give items being received. (Or reverse if we're the receiver)
        /// </summary>
        /// <param name="asReceiver">Are we fulfilling from the perspective of the receiver?</param>
        public void Fulfill(bool asReceiver)
        {
            Log.Message("Fulfill trade. asReceiver = " + asReceiver);
            
            var toGive = new List<Thing>();

            foreach (TradeThing trade in Things)
            {
                try
                {
                    // Give offered things
                    toGive.AddRange(GetQuantityFromThings(asReceiver ? trade.OfferedThings : trade.RequestedThings,
                        asReceiver ? trade.CountOffered : -trade.CountOffered, true));
                }
                catch (Exception e)
                {
                    Log.Error(e.Message);
                }
                
                // Take requested things
                int countToTake = asReceiver ? -trade.CountOffered : trade.CountOffered;
                if (countToTake > 0)
                {
                    int taken = 0;
                    foreach (Thing thing in (asReceiver ? trade.RequestedThings : trade.OfferedThings))
                    {
                        taken += LaunchUtil.LaunchThing(Find.CurrentMap, thing, Mathf.Min(thing.stackCount, countToTake - taken));
                        if (taken >= countToTake)
                            break; // taken enough
                    }

                    if (taken < countToTake)
                        Log.Warn($"Unable to find enough things to launch for trade. Player unable to fully fulfill their side of trade. ({taken}/{countToTake} launched)");
                }
            }

            var dropPodLocations = new List<IntVec3>();
            foreach (Thing thing in toGive)
            {
                Log.Message($"Give thing {thing.Label}");
                IntVec3 pos = DropCellFinder.TradeDropSpot(Find.CurrentMap);
                dropPodLocations.Add(pos);
                TradeUtility.SpawnDropPod(pos, Find.CurrentMap, thing);
            }

            if (dropPodLocations.Count == 0)
            {
                Find.LetterStack.ReceiveLetter($"Trade Success ({(IsForUs ? From : For)})", "Trade accepted.", LetterDefOf.PositiveEvent);
            }
            else
            {
                var averagePos = new IntVec3(0, 0, 0);
                foreach (IntVec3 pos in dropPodLocations)
                    averagePos += pos;
                averagePos = new IntVec3(averagePos.x / dropPodLocations.Count, averagePos.y / dropPodLocations.Count, averagePos.z / dropPodLocations.Count);
                Find.LetterStack.ReceiveLetter($"Trade Success ({(IsForUs ? From : For)})", "Trade accepted. Your items will arrive in pods momentarily.", LetterDefOf.PositiveEvent, new TargetInfo(averagePos, Find.CurrentMap));
            }
        }

        public bool CanFulfill(bool asReceiver)
        {
            foreach (TradeThing trade in Things)
            {
                // Take requested things
                int countToTake = asReceiver ? -trade.CountOffered : trade.CountOffered;
                if (countToTake > 0)
                {
                    int taken = 0;
                    foreach (Thing thing in (asReceiver ? trade.RequestedThings : trade.OfferedThings))
                    {
                        taken += LaunchUtil.LaunchThing(Find.CurrentMap, thing, Mathf.Min(thing.stackCount, countToTake - taken), true); // dry run
                        if (taken >= countToTake)
                            break; // taken enough
                    }

                    if (taken < countToTake)
                        return false;
                }
            }

            return true;
        }

        private List<Thing> GetQuantityFromThings(List<Thing> things, int count, bool exceptionIfNotEnough)
        {
            var result = new List<Thing>();

            int given = 0;
            while (given < count)
            {
                // Get first thing that isn't empty and isn't already given
                Thing thing = things.FirstOrDefault(t => (t.stackCount > 0 && !result.Contains(t)));
                if (thing == null)
                {
                    if (exceptionIfNotEnough)
                        throw new Exception("Not enough things to get desired quantity");
                    break;
                }

                int countToSplit = Mathf.Min(thing.stackCount, count - given);
                if (countToSplit == thing.stackCount)
                {
                    // Give entire thing
                    result.Add(thing);
                }
                else
                {
                    // Need to split
                    result.Add(thing.SplitOff(countToSplit));
                }

                given += countToSplit;
            }

            return result;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Guid, "guid");
            Scribe_Values.Look(ref From, "from");
            Scribe_Values.Look(ref For, "for");
            Scribe_Collections.Look(ref Things, "things");
        }

        public class TradeThing : IExposable
        {
            public List<Thing> OfferedThings;
            public List<Thing> RequestedThings;

            /// <summary>
            /// Amount requested. If negative, then amount offered.
            /// </summary>
            public int CountOffered;

            public TradeThing(List<Thing> offeredThings, List<Thing> requestedThings, int countOffered)
            {
                OfferedThings = offeredThings;
                RequestedThings = requestedThings;
                CountOffered = countOffered;
            }

            public void ExposeData()
            {
                Scribe_Collections.Look(ref OfferedThings, "offered_things");
                Scribe_Collections.Look(ref RequestedThings, "requested_things");
                Scribe_Values.Look(ref CountOffered, "count");
            }
        }
    }
}