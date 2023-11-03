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
using System.Xml;

namespace EclipseLevelsSave
{
    public static class Stats
    {
        const string trustedProfileFlag = "GS_PersistentProfiles_Trusted";
        const string eclipseString = "Eclipse.";
        const int maxVanillaEclipseLevel = 8;

        public static bool ignoreModdedEclipse;
        private static int restoringUnlockableCount;

        public static void Init()
        {
            On.RoR2.XmlUtility.ToXml += XmlUtility_ToXml;
            On.RoR2.XmlUtility.FromXml += XmlUtility_FromXml;
            UserProfile.onUnlockableGranted += UserProfile_onUnlockableGranted;
            On.RoR2.UserProfile.RevokeUnlockable += UserProfile_RevokeUnlockable;
        }

        private static XDocument XmlUtility_ToXml(On.RoR2.XmlUtility.orig_ToXml orig, UserProfile userProfile)
        {
            XDocument doc = orig(userProfile);
            if (doc?.Root != null)
            {
                doc.Root.Add(new XElement(trustedProfileFlag));
            }
            return doc;
        }

        private static UserProfile XmlUtility_FromXml(On.RoR2.XmlUtility.orig_FromXml orig, XDocument doc)
        {
            UserProfile userProfile = orig(doc);
            if (userProfile != null && doc?.Root != null && doc.Root.Element(trustedProfileFlag) == null)
            {
                RestoreEclipseUnlockables(userProfile, doc);
            }
            return userProfile;
        }

        public static void RestoreEclipseUnlockables(UserProfile userProfile, XDocument doc)
        {
            Dictionary<string, int> survivorNameToHighestEclipseLevel = new Dictionary<string, int>();
            foreach (string achievementIdentifier in userProfile.achievementsList)
            {
                if (achievementIdentifier.StartsWith(eclipseString) && TryParseEclipseUnlockable(achievementIdentifier, out string survivorName, out int eclipseLevel))
                {
                    if (survivorNameToHighestEclipseLevel.TryGetValue(survivorName, out int highestEclipseLevel))
                    {
                        survivorNameToHighestEclipseLevel[survivorName] = Math.Max(highestEclipseLevel, eclipseLevel);
                    }
                    else
                    {
                        survivorNameToHighestEclipseLevel.Add(survivorName, eclipseLevel);
                    }
                }
            }
            StringBuilder stringBuilder = HG.StringBuilderPool.RentStringBuilder();
            foreach (KeyValuePair<string, int> pair in survivorNameToHighestEclipseLevel)
            {
                int highestUnlockedLevel = pair.Value;
                if (ignoreModdedEclipse)
                {
                    highestUnlockedLevel = Math.Min(highestUnlockedLevel, maxVanillaEclipseLevel + 1);
                }
                for (int i = EclipseRun.minUnlockableEclipseLevel; i <= highestUnlockedLevel; i++)
                {
                    stringBuilder.Clear();
                    stringBuilder.Append(eclipseString).Append(pair.Key).Append(".").AppendInt(i);
                    RestoreEclipseUnlockable(userProfile, stringBuilder.ToString());
                }
            }
            HG.StringBuilderPool.ReturnStringBuilder(stringBuilder);

            HashSet<string> cleanSurvivorNames = new HashSet<string>();
            if (TryFindStatsElement(doc, out XElement statsElement))
            {
                foreach (string eclipseUnlockableName in statsElement
                    .Elements("unlock")
                    .Select(x => (x.Nodes().FirstOrDefault(node => node.NodeType == XmlNodeType.Text) as XText)?.Value)
                    .Where(x => x != null && x.StartsWith(eclipseString))
                    )
                {
                    if (TryParseEclipseUnlockable(eclipseUnlockableName, out string survivorName, out int eclipseLevel) && cleanSurvivorNames.Add(survivorName))
                    {
                        if (!survivorNameToHighestEclipseLevel.TryGetValue(survivorName, out int highestEclipseLevel) || eclipseLevel > highestEclipseLevel)
                        {
                            UpdatePersistentEclipseUnlockables(userProfile, survivorName);
                        }
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
                if (!SaveStats.orphanedUnlocksLookup.TryGetValue(userProfile.statSheet, out HashSet<string> orphanedUnlocks))
                {
                    SaveStats.orphanedUnlocksLookup.Add(userProfile.statSheet, orphanedUnlocks = new HashSet<string>());
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
