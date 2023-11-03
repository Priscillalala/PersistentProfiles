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
    public static class Eclipse
    {
        const string eclipseString = "Eclipse.";
        const int maxVanillaEclipseLevel = 8;

        public static bool ignoreModdedEclipse;
        private static int restoringUnlockableCount;

        public static void Init()
        {
            PersistentProfiles.onUntrustedProfileDiscovered += PersistentProfiles_onUntrustedProfileDiscovered;
            UserProfile.onUnlockableGranted += UserProfile_onUnlockableGranted;
            On.RoR2.UserProfile.RevokeUnlockable += UserProfile_RevokeUnlockable;
        }

        private static void PersistentProfiles_onUntrustedProfileDiscovered(UserProfile userProfile, XDocument doc)
        {
            RestoreEclipseUnlockables(userProfile, out Dictionary<string, int> survivorNameToPersistentEclipseLevel);
            if (TryFindStatsElement(doc, out XElement statsElement))
            {
                UpdateAllPersistentEclipseUnlockables(userProfile, statsElement
                    .Elements("unlock")
                    .Select(x => (x.Nodes().FirstOrDefault(node => node.NodeType == XmlNodeType.Text) as XText)?.Value)
                    .Where(x => x != null && x.StartsWith(eclipseString)), survivorNameToPersistentEclipseLevel);
            }
            userProfile.saveRequestPending = true;
        }

        public static void RestoreEclipseUnlockables(UserProfile userProfile, out Dictionary<string, int> survivorNameToPersistentEclipseLevel)
        {
            PersistentProfiles.logger.LogInfo($"Restoring eclipse unlockables for UserProfile {userProfile.name}:");
            survivorNameToPersistentEclipseLevel = new Dictionary<string, int>();
            foreach (string achievementIdentifier in userProfile.achievementsList)
            {
                if (achievementIdentifier.StartsWith(eclipseString) && TryParseEclipseUnlockable(achievementIdentifier, out string survivorName, out int eclipseLevel))
                {
                    if (survivorNameToPersistentEclipseLevel.TryGetValue(survivorName, out int highestEclipseLevel))
                    {
                        survivorNameToPersistentEclipseLevel[survivorName] = Math.Max(highestEclipseLevel, eclipseLevel);
                    }
                    else
                    {
                        survivorNameToPersistentEclipseLevel.Add(survivorName, eclipseLevel);
                    }
                }
            }
            StringBuilder stringBuilder = HG.StringBuilderPool.RentStringBuilder();
            foreach (KeyValuePair<string, int> pair in survivorNameToPersistentEclipseLevel)
            {
                int highestUnlockedLevel = pair.Value;
                if (ignoreModdedEclipse)
                {
                    highestUnlockedLevel = Math.Min(highestUnlockedLevel, maxVanillaEclipseLevel + 1);
                }
                PersistentProfiles.logger.LogInfo($"Restoring unlockables up to Eclipse {highestUnlockedLevel} for {pair.Key}.");
                for (int i = EclipseRun.minUnlockableEclipseLevel; i <= highestUnlockedLevel; i++)
                {
                    stringBuilder.Clear();
                    stringBuilder.Append(eclipseString).Append(pair.Key).Append(".").AppendInt(i);
                    RestoreEclipseUnlockable(userProfile, stringBuilder.ToString());
                }
            }
            HG.StringBuilderPool.ReturnStringBuilder(stringBuilder);
        }

        public static void UpdateAllPersistentEclipseUnlockables(UserProfile userProfile, IEnumerable<string> eclipseUnlockableNames, Dictionary<string, int> survivorNameToPersistentEclipseLevel)
        {
            HashSet<string> cleanSurvivorNames = new HashSet<string>();
            foreach (string eclipseUnlockableName in eclipseUnlockableNames)
            {
                if (TryParseEclipseUnlockable(eclipseUnlockableName, out string survivorName, out int eclipseLevel) && cleanSurvivorNames.Add(survivorName))
                {
                    if (!survivorNameToPersistentEclipseLevel.TryGetValue(survivorName, out int persistentEclipseLevel) || eclipseLevel > persistentEclipseLevel)
                    {
                        UpdatePersistentEclipseUnlockables(userProfile, survivorName);
                    }
                }
            }
        }

        public static bool TryFindStatsElement(XDocument doc, out XElement statsElement)
        {
            return (statsElement = doc?.Root?.Element("stats")) != null;
        }

        public static void RestoreEclipseUnlockable(UserProfile userProfile, string eclipseUnlockableName)
        {
            UnlockableDef unlockableDef = UnlockableCatalog.GetUnlockableDef(eclipseUnlockableName);
            if (unlockableDef && !userProfile.HasUnlockable(unlockableDef))
            {
                restoringUnlockableCount++;
                try
                {
                    userProfile.GrantUnlockable(unlockableDef);
                }
                finally
                {
                    restoringUnlockableCount--;
                }
            }
            else if (userProfile.statSheet != null)
            {
                if (!Stats.orphanedUnlocksLookup.TryGetValue(userProfile.statSheet, out HashSet<string> orphanedUnlocks))
                {
                    Stats.orphanedUnlocksLookup.Add(userProfile.statSheet, orphanedUnlocks = new HashSet<string>());
                }
                orphanedUnlocks.Add(eclipseUnlockableName);
            }
        }

        private static void UserProfile_onUnlockableGranted(UserProfile userProfile, UnlockableDef unlockableDef)
        {
            if (userProfile != null && unlockableDef && unlockableDef.cachedName.StartsWith(eclipseString) && TryParseEclipseUnlockable(unlockableDef.cachedName, out string survivorName, out _))
            {
                UpdatePersistentEclipseUnlockables(userProfile, survivorName);
            }
        }

        private static void UserProfile_RevokeUnlockable(On.RoR2.UserProfile.orig_RevokeUnlockable orig, UserProfile userProfile, UnlockableDef unlockableDef)
        {
            orig(userProfile, unlockableDef);
            if (userProfile != null && unlockableDef && unlockableDef.cachedName.StartsWith(eclipseString) && TryParseEclipseUnlockable(unlockableDef.cachedName, out string survivorName, out _))
            {
                UpdatePersistentEclipseUnlockables(userProfile, survivorName);
            }
        }

        public static void UpdatePersistentEclipseUnlockables(UserProfile userProfile, string survivorName)
        {
            if (restoringUnlockableCount > 0)
            {
                return;
            }
            if (survivorName.Contains(" "))
            {
                PersistentProfiles.logger.LogWarning($"Cannot add a persistent eclipse unlockable for invalid survivor name {survivorName}! Ignoring.");
                return;
            }
            SurvivorDef survivorDef = SurvivorCatalog.FindSurvivorDef(survivorName);
            if (survivorDef == null)
            {
                return;
            }

            List<UnlockableDef> eclipseLevelUnlockables = EclipseRun.GetEclipseLevelUnlockablesForSurvivor(survivorDef);
            int count = eclipseLevelUnlockables.Count;
            if (ignoreModdedEclipse)
            {
                count = Math.Min(count, maxVanillaEclipseLevel);
            }
            int lastUnlockedIndex = eclipseLevelUnlockables.FindLastIndex(x => userProfile.HasUnlockable(x));
            PersistentProfiles.logger.LogInfo($"Updating persistent eclipse unlockable for {survivorName} in UserProfile {userProfile.name}. Highest eclipse unlockable is Eclipse {EclipseRun.minUnlockableEclipseLevel + lastUnlockedIndex}.");
            for (int i = 0; i < count; i++)
            {
                userProfile.achievementsList.Remove(eclipseLevelUnlockables[i].cachedName);
            }
            if (lastUnlockedIndex >= 0 && lastUnlockedIndex < count)
            {
                userProfile.achievementsList.Add(eclipseLevelUnlockables[lastUnlockedIndex].cachedName);
            }
            userProfile.RequestEventualSave();
        }

        public static bool TryParseEclipseUnlockable(string eclipseUnlockableString, out string survivorName, out int eclipseLevel)
        {
            int firstIndex = 7;
            int lastIndex = eclipseUnlockableString.LastIndexOf('.');
            if (firstIndex == lastIndex || !int.TryParse(eclipseUnlockableString.Substring(lastIndex + 1), out eclipseLevel))
            {
                survivorName = default;
                eclipseLevel = default;
                return false;
            }
            survivorName = eclipseUnlockableString.Substring(firstIndex + 1, lastIndex - firstIndex - 1);
            return true;
        }
    }
}
