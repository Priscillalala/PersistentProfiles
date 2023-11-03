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
using RoR2.Stats;
using BepInEx.Configuration;
using System.Xml.Linq;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System.Linq;
using System.Reflection;

namespace EclipseLevelsSave
{
    public static class SaveStats
    {
        public static Dictionary<StatSheet, Dictionary<string, string>> orphanedStatsLookup = new Dictionary<StatSheet, Dictionary<string, string>>();
        public static Dictionary<StatSheet, HashSet<string>> orphanedUnlocksLookup = new Dictionary<StatSheet, HashSet<string>>();
        public static Dictionary<UserProfile, string> orphanedPickupsLookup = new Dictionary<UserProfile, string>();
        public static Dictionary<UserProfile, XElement[]> orphanedBodyLoadoutsLookup = new Dictionary<UserProfile, XElement[]>();

        public static void Init()
        {
            On.RoR2.XmlUtility.ToXml += XmlUtility_ToXml;
            On.RoR2.XmlUtility.FromXml += XmlUtility_FromXml;
            On.RoR2.SaveSystem.Copy += SaveSystem_Copy;
            On.RoR2.SaveFieldAttribute.SetupPickupsSet += SaveFieldAttribute_SetupPickupsSet;
            IL.RoR2.XmlUtility.GetStatsField += XmlUtility_GetStatsField;
            On.RoR2.XmlUtility.CreateStatsField += XmlUtility_CreateStatsField;
            On.RoR2.Stats.StatSheet.Copy += StatSheet_Copy;
        }

        private static XDocument XmlUtility_ToXml(On.RoR2.XmlUtility.orig_ToXml orig, UserProfile userProfile)
        {
            XDocument doc = orig(userProfile);
            if (orphanedBodyLoadoutsLookup.TryGetValue(userProfile, out XElement[] orphanedBodyLoadouts) && TryFindBodyLoadoutsElement(doc, out XElement bodyLoadoutsElement))
            {
                bodyLoadoutsElement.Add(orphanedBodyLoadouts);
            }
            return doc;
        }

        private static UserProfile XmlUtility_FromXml(On.RoR2.XmlUtility.orig_FromXml orig, XDocument doc)
        {
            UserProfile userProfile = orig(doc);
            if (TryFindBodyLoadoutsElement(doc, out XElement bodyLoadoutsElement))
            {
                XElement[] orphanedBodyLoadouts = bodyLoadoutsElement
                    .Elements("BodyLoadout")
                    .GroupBy(x => x.Attribute("bodyName")?.Value)
                    .Where(x => x.Key != null && BodyCatalog.FindBodyIndex(x.Key) == BodyIndex.None)
                    .Select(x => x.First())
                    .ToArray();
                if (orphanedBodyLoadouts.Length > 0)
                {
                    orphanedBodyLoadoutsLookup[userProfile] = orphanedBodyLoadouts;
                }               
            }
            return userProfile;
        }

        public static bool TryFindBodyLoadoutsElement(XDocument doc, out XElement bodyLoadoutsElement)
        {
            return (bodyLoadoutsElement = doc?.Root?.Element("loadout")?.Element("BodyLoadouts")) != null;
        }

        private static void SaveSystem_Copy(On.RoR2.SaveSystem.orig_Copy orig, UserProfile src, UserProfile dest)
        {
            orig(src, dest);
            if (orphanedBodyLoadoutsLookup.TryGetValue(src, out XElement[] orphanedBodyLoadouts))
            {
                orphanedBodyLoadoutsLookup[dest] = orphanedBodyLoadouts;
            }
        }

        private static void SaveFieldAttribute_SetupPickupsSet(On.RoR2.SaveFieldAttribute.orig_SetupPickupsSet orig, SaveFieldAttribute self, FieldInfo fieldInfo)
        {
            orig(self, fieldInfo);
            Func<UserProfile, string> origGetter = self.getter;
            self.getter = (userProfile) =>
            {
                string valueString = origGetter(userProfile);
                if (orphanedPickupsLookup.TryGetValue(userProfile, out string orphanedPickups))
                {
                    valueString += " " + orphanedPickups;
                }
                return valueString;
            };
            self.setter = (Action<UserProfile, string>)Delegate.Combine(self.setter, (UserProfile userProfile, string valueString) =>
            {
                orphanedPickupsLookup[userProfile] = string.Join(" ",
                    valueString.Split(' ')
                    .Where(x => !PickupCatalog.FindPickupIndex(x).isValid)
                    .Distinct()
                    );
            });
            self.copier = (Action<UserProfile, UserProfile>)Delegate.Combine(self.copier, (UserProfile srcProfile, UserProfile destProfile) =>
            {
                if (orphanedPickupsLookup.TryGetValue(srcProfile, out string orphanedPickups))
                {
                    orphanedPickupsLookup[destProfile] = orphanedPickups;
                }
            });
        }

        private static void StatSheet_Copy(On.RoR2.Stats.StatSheet.orig_Copy orig, StatSheet src, StatSheet dest)
        {
            orig(src, dest);
            if (orphanedStatsLookup.TryGetValue(src, out Dictionary<string, string> orphanedStats))
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
            if (c.TryGotoNext(MoveType.AfterLabel, x => x.MatchRet()))
            {
                c.Emit(OpCodes.Ldarg, 2);
                c.EmitDelegate<Action<StatSheet>>(dest =>
                {
                    orphanedStatsLookup[dest] = new Dictionary<string, string>();
                    orphanedUnlocksLookup[dest] = new HashSet<string>();
                });
            }
            else Debug.LogError("Failed IL 1!");

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
                    if (!string.IsNullOrEmpty(name) && value != null && statDef == null && orphanedStatsLookup.TryGetValue(dest, out Dictionary<string, string> orphanedStats))
                    {
                        orphanedStats[name] = value;
                    }
                });
            }
            else Debug.LogError("Failed IL 2!");

            if (c.TryGotoNext(MoveType.Before, x => x.MatchCallOrCallvirt(typeof(UnlockableCatalog), nameof(UnlockableCatalog.GetUnlockableDef))))
            {
                c.Emit(OpCodes.Dup);
                c.Index++;
                c.Emit(OpCodes.Dup);
                c.Emit(OpCodes.Ldarg, 2);
                c.EmitDelegate<Action<string, UnlockableDef, StatSheet>>((name, unlockableDef, dest) =>
                {
                    if (!string.IsNullOrEmpty(name) && unlockableDef == null && orphanedUnlocksLookup.TryGetValue(dest, out HashSet<string> orphanedUnlocks))
                    {
                        orphanedUnlocks.Add(name);   
                    }
                });
            }
            else Debug.LogError("Failed IL 3!");
        }

        private static XElement XmlUtility_CreateStatsField(On.RoR2.XmlUtility.orig_CreateStatsField orig, string name, StatSheet statSheet)
        {
            XElement result = orig(name, statSheet);
            if (orphanedStatsLookup.TryGetValue(statSheet, out Dictionary<string, string> orphanedStats))
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

        /*public struct OrphanedStat
        {
            public string name;
            public string value;
        }

        public struct OrphanedUnlock
        {
            public string name;
        }*/
    }
}
