using BepInEx;
using BepInEx.Logging;
using System;
using System.Security;
using System.Security.Permissions;
using UnityEngine;
using RoR2;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Text;
using HG;
using BepInEx.Configuration;
using System.Xml.Linq;
using System.Linq;
using System.Xml;
using RoR2.Stats;
using MonoMod.Cil;
using Mono.Cecil.Cil;

namespace PersistentProfiles
{
    public static class Stats
    {
        public static bool includeStats;
        public static Dictionary<StatSheet, Dictionary<string, string>> orphanedStatsLookup;
        public static Dictionary<StatSheet, HashSet<string>> orphanedUnlocksLookup;

        public static void Init()
        {
            if (includeStats)
            {
                orphanedStatsLookup = new Dictionary<StatSheet, Dictionary<string, string>>();
            }
            orphanedUnlocksLookup = new Dictionary<StatSheet, HashSet<string>>();

            IL.RoR2.XmlUtility.GetStatsField += XmlUtility_GetStatsField;
            On.RoR2.XmlUtility.CreateStatsField += XmlUtility_CreateStatsField;
            On.RoR2.Stats.StatSheet.Copy += StatSheet_Copy;
        }

        private static void StatSheet_Copy(On.RoR2.Stats.StatSheet.orig_Copy orig, StatSheet src, StatSheet dest)
        {
            orig(src, dest);
            if (includeStats && orphanedStatsLookup.TryGetValue(src, out Dictionary<string, string> orphanedStats))
            {
                orphanedStatsLookup[dest] = orphanedStats;
            }
            if (orphanedUnlocksLookup.TryGetValue(src, out HashSet<string> orphanedUnlocks))
            {
                orphanedUnlocksLookup[dest] = orphanedUnlocks;
            }
        }

        private static void XmlUtility_GetStatsField(ILContext il)
        {
            ILCursor c = new ILCursor(il);
            /*if (c.TryGotoNext(MoveType.AfterLabel, x => x.MatchRet()))
            {
                c.Emit(OpCodes.Ldarg, 2);
                c.EmitDelegate<Action<StatSheet>>(dest =>
                {
                    orphanedStatsLookup[dest] = new Dictionary<string, string>();
                    orphanedUnlocksLookup[dest] = new HashSet<string>();
                });
            }
            else Debug.LogError("Failed IL 1!");*/
            if (includeStats)
            {
                int locStatNameIndex = -1;
                int locStatValueIndex = -1;
                if (c.TryGotoNext(MoveType.Before,
                    x => x.MatchLdloc(out locStatNameIndex),
                    x => x.MatchCallOrCallvirt<StatDef>(nameof(StatDef.Find)),
                    x => x.MatchLdloc(out locStatValueIndex),
                    x => x.MatchCallOrCallvirt<StatSheet>(nameof(StatSheet.SetStatValueFromString))
                    ))
                {
                    c.Index += 2;
                    c.Emit(OpCodes.Dup);
                    c.Emit(OpCodes.Ldloc, locStatNameIndex);
                    c.Emit(OpCodes.Ldloc, locStatValueIndex);
                    c.Emit(OpCodes.Ldarg, 2);
                    c.EmitDelegate<Action<StatDef, string, string, StatSheet>>((statDef, name, value, dest) =>
                    {
                        if (!string.IsNullOrEmpty(name) && value != null && statDef == null)
                        {
                            if (!orphanedStatsLookup.TryGetValue(dest, out Dictionary<string, string> orphanedStats))
                            {
                                orphanedStatsLookup.Add(dest, orphanedStats = new Dictionary<string, string>());
                            }
                            orphanedStats[name] = value;
                        }
                    });
                }
                else PersistentProfiles.logger.LogError($"Failed stats IL hook for {nameof(XmlUtility_GetStatsField)}!");
            }

            if (c.TryGotoNext(MoveType.Before, x => x.MatchCallOrCallvirt(typeof(UnlockableCatalog), nameof(UnlockableCatalog.GetUnlockableDef))))
            {
                c.MoveAfterLabels();
                c.Emit(OpCodes.Dup);
                c.Index++;
                c.Emit(OpCodes.Ldarg, 2);
                c.EmitDelegate<Func<string, UnlockableDef, StatSheet, UnlockableDef>>((name, unlockableDef, dest) =>
                {
                    if (!string.IsNullOrEmpty(name) && unlockableDef == null)
                    {
                        if (!orphanedUnlocksLookup.TryGetValue(dest, out HashSet<string> orphanedUnlocks))
                        {
                            orphanedUnlocksLookup.Add(dest, orphanedUnlocks = new HashSet<string>());
                        }
                        orphanedUnlocks.Add(name);
                    }
                    return unlockableDef;
                });
            }
            else PersistentProfiles.logger.LogError($"Failed unlockables IL hook for {nameof(XmlUtility_GetStatsField)}!");
        }

        private static XElement XmlUtility_CreateStatsField(On.RoR2.XmlUtility.orig_CreateStatsField orig, string name, StatSheet statSheet)
        {
            XElement result = orig(name, statSheet);
            if (includeStats && orphanedStatsLookup.TryGetValue(statSheet, out Dictionary<string, string> orphanedStats))
            {
                foreach (KeyValuePair<string, string> orphanedStat in orphanedStats)
                {
                    XElement statElement = new XElement("stat", new XText(orphanedStat.Value));
                    statElement.SetAttributeValue("name", orphanedStat.Key);
                    result.Add(statElement);
                }
            }
            if (orphanedUnlocksLookup.TryGetValue(statSheet, out HashSet<string> orphanedUnlocks))
            {
                foreach (string orphanedUnlock in orphanedUnlocks)
                {
                    XElement unlockElement = new XElement("unlock", new XText(orphanedUnlock));
                    result.Add(unlockElement);
                }
            }
            return result;
        }
    }
}
