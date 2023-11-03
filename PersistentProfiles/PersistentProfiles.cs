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

[module: UnverifiableCode]
#pragma warning disable
[assembly: SecurityPermission(System.Security.Permissions.SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore
[assembly: HG.Reflection.SearchableAttribute.OptIn]

namespace EclipseLevelsSave
{
    [BepInPlugin("com.groovesalad.PersistentProfiles", "PersistentProfiles", "1.0.0")]
    public class PersistentProfiles : BaseUnityPlugin
    {
        const string configSection = "PersistentProfiles";
        const string trustedProfileFlag = "GS_PersistentProfiles_Trusted";
        const string eclipseString = "Eclipse.";
        const int maxVanillaEclipseLevel = 8;

        public static ManualLogSource logger;
        private static bool ignoreModdedEclipse;
        private static int restoringUnlockableCount;
        private static bool includeStats;
        public static Dictionary<StatSheet, Dictionary<string, string>> orphanedStatsLookup = new Dictionary<StatSheet, Dictionary<string, string>>();
        public static Dictionary<StatSheet, HashSet<string>> orphanedUnlocksLookup = new Dictionary<StatSheet, HashSet<string>>();

        public void Awake()
        {
            logger = Logger;
            ignoreModdedEclipse = Config.Bind(configSection, "Ignore Modded Eclipse Levels", true, "Only manage Eclipse 8 and below to avoid possible conflicts with mods that add new eclipse levels.").Value;
            includeStats = Config.Bind(configSection, "Preserve Modded Stats", true, "Prevent modded stats from being wiped for as long as this mod is installed.").Value;
            if (includeStats)
            {
                orphanedStatsLookup = new Dictionary<StatSheet, Dictionary<string, string>>();
            }
            orphanedUnlocksLookup = new Dictionary<StatSheet, HashSet<string>>();
            if (Config.Bind(configSection, "Preserve Modded Loadouts", true, "Prevent modded loadout preferences from being wiped for as long as this mod is installed.").Value)
            {
                BodyLoadouts.Init();
            }
            if (Config.Bind(configSection, "Preserve Modded Pickups", true, "Prevent discovered modded items and equipment from being wiped for as long as this mod is installed.").Value)
            {
                Pickups.Init();
            }

            On.RoR2.XmlUtility.ToXml += XmlUtility_ToXml;
            On.RoR2.XmlUtility.FromXml += XmlUtility_FromXml;
            UserProfile.onUnlockableGranted += UserProfile_onUnlockableGranted;
            On.RoR2.UserProfile.RevokeUnlockable += UserProfile_RevokeUnlockable;
            IL.RoR2.XmlUtility.GetStatsField += XmlUtility_GetStatsField;
            On.RoR2.XmlUtility.CreateStatsField += XmlUtility_CreateStatsField;
            On.RoR2.Stats.StatSheet.Copy += StatSheet_Copy;
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
                        if (!string.IsNullOrEmpty(name) && value != null && statDef == null && orphanedStatsLookup.TryGetValue(dest, out Dictionary<string, string> orphanedStats))
                        {
                            orphanedStats[name] = value;
                        }
                    });
                }
                else Debug.LogError("Failed IL 2!");
            }

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

    /*const string eclipseString = "Eclipse.";
  const string defaultIgnoredSurvivorNames = "Commando Huntress Bandit2 Toolbot Engi Mage Merc Treebot Croco Loader Captain Railgunner VoidSurvivor";
  private bool isRestoringUnlockable;
  private HashSet<string> ignoredSurvivorNames;
  private int maxUnlockableIgnoredLevel;

  public void Awake()
  {
      ignoredSurvivorNames = new HashSet<string>(Config.Bind("EclipseLevelsSave", "Ignored Survivor Names", defaultIgnoredSurvivorNames, "Survivors who will always be enabled and do not need persistent eclipse unlockables").Value.Split(' '));
      maxUnlockableIgnoredLevel = Config.Bind("EclipseLevelsSave", "Maximum Ignored Eclipse Level", 8, "Persistent eclipse unlockables past this level will not be ignored (to preserve modded eclipse levels)").Value + 1;
      LocalUserManager.onUserSignIn += LocalUserManager_onUserSignIn;
      UserProfile.onUnlockableGranted += UserProfile_onUnlockableGranted;
      On.RoR2.UserProfile.RevokeUnlockable += UserProfile_RevokeUnlockable;
  }

  private void LocalUserManager_onUserSignIn(LocalUser localUser)
  {
      if (localUser == null || localUser.userProfile == null || !localUser.userProfile.canSave)
      {
          return;
      }
      Dictionary<string, int> survivorNameToEclipseLevel = new Dictionary<string, int>();
      for (int i = localUser.userProfile.achievementsList.Count - 1; i >= 0; i--)
      {
          string achievementIdentifier = localUser.userProfile.achievementsList[i];
          if (EclipseUnlockString.Exists(achievementIdentifier) && EclipseUnlockString.TryParse(achievementIdentifier, out EclipseUnlockString eclipseUnlockString))
          {
              if (ShouldIgnore(eclipseUnlockString))
              {
                  Logger.LogInfo($"Removing ignored persistent eclipse unlockable {achievementIdentifier} for UserProfile {localUser.userProfile.name}.");
                  localUser.userProfile.achievementsList.RemoveAt(i);
                  localUser.userProfile.RequestEventualSave();
              }
              else if (survivorNameToEclipseLevel.TryGetValue(eclipseUnlockString.survivorName, out int eclipseLevel))
              {
                  survivorNameToEclipseLevel[eclipseUnlockString.survivorName] = Math.Max(eclipseLevel, eclipseUnlockString.eclipseLevel);
              }
              else
              {
                  survivorNameToEclipseLevel.Add(eclipseUnlockString.survivorName, eclipseUnlockString.eclipseLevel);
              }
          }
      }
      StringBuilder stringBuilder = HG.StringBuilderPool.RentStringBuilder();
      foreach (KeyValuePair<string, int> pair in survivorNameToEclipseLevel)
      {
          for (int i = EclipseRun.minUnlockableEclipseLevel; i <= pair.Value; i++)
          {
              stringBuilder.Clear();
              stringBuilder.Append(eclipseString).Append(pair.Key).Append(".").AppendInt(i);
              UnlockableDef unlockableDef = UnlockableCatalog.GetUnlockableDef(stringBuilder.ToString());
              if (unlockableDef && !localUser.userProfile.HasUnlockable(unlockableDef))
              {
                  RestoreEclipseUnlockable(localUser.userProfile, unlockableDef);
              }
          }
      }
      HG.StringBuilderPool.ReturnStringBuilder(stringBuilder);
      foreach (SurvivorDef survivorDef in SurvivorCatalog.allSurvivorDefs)
      {
          if (!ignoredSurvivorNames(survivorDef.cachedName)
      }
  }

  public void RestoreEclipseUnlockable(UserProfile userProfile, UnlockableDef unlockableDef)
  {
      Logger.LogInfo($"UserProfile {userProfile.name} is missing eclipse unlockable {unlockableDef.cachedName}. Restoring.");
      isRestoringUnlockable = true;
      try
      {
          userProfile.GrantUnlockable(unlockableDef);
      }
      finally
      {
          isRestoringUnlockable = false;
      }
  }

  private bool ShouldIgnore(EclipseUnlockString eclipseUnlockString)
  {
      return ignoredSurvivorNames.Contains(eclipseUnlockString.survivorName) && eclipseUnlockString.eclipseLevel <= maxUnlockableIgnoredLevel;
  }

  private bool CanPersistCheck(EclipseUnlockString eclipseUnlockString)
  {
      if (ShouldIgnore(eclipseUnlockString))
      {
          return false;
      }
      if (eclipseUnlockString.survivorName.Contains(" "))
      {
          Logger.LogWarning($"Cannot add persistent eclipse unlockable with invalid survivor name {eclipseUnlockString.survivorName}! Ignoring.");
          return false;
      }
      return true;
  }

  private void UserProfile_onUnlockableGranted(UserProfile userProfile, UnlockableDef unlockableDef)
  {
      if (!isRestoringUnlockable && userProfile != null && unlockableDef)
      {
          AttemptGrantPersistentUnlockable(userProfile, unlockableDef);
      }
  }

  private void UserProfile_RevokeUnlockable(On.RoR2.UserProfile.orig_RevokeUnlockable orig, UserProfile userProfile, UnlockableDef unlockableDef)
  {
      orig(userProfile, unlockableDef);
      if (userProfile != null && unlockableDef)
      {
          AttemptRevokePersistentUnlockable(userProfile, unlockableDef);
      }
  }

  private void AttemptGrantPersistentUnlockable(UserProfile userProfile, UnlockableDef unlockableDef)
  {
      if (userProfile.HasAchievement(unlockableDef.cachedName))
      {
          return;
      }
      if (EclipseUnlockString.TryParse(unlockableDef.cachedName, out EclipseUnlockString eclipseUnlockString) && CanPersistCheck(eclipseUnlockString))
      {
          for (int i = userProfile.achievementsList.Count - 1; i >= 0; i--)
          {
              string achievementIdentifier = userProfile.achievementsList[i];
              if (EclipseUnlockString.Exists(achievementIdentifier) && EclipseUnlockString.TryParse(achievementIdentifier, out EclipseUnlockString other))
              {
                  if (other.survivorName == eclipseUnlockString.survivorName && other.eclipseLevel < eclipseUnlockString.eclipseLevel)
                  {
                      Logger.LogInfo($"Upgrading persistent eclipse unlockable {achievementIdentifier} to {unlockableDef.cachedName} in UserProfile {userProfile.name}.");
                      userProfile.achievementsList[i] = unlockableDef.cachedName;
                      userProfile.RequestEventualSave();
                      return;
                  }
              }
          }
          Logger.LogInfo($"Adding persistent eclipse unlockable {unlockableDef.cachedName} to UserProfile {userProfile.name}.");
          userProfile.achievementsList.Add(unlockableDef.cachedName);
          userProfile.RequestEventualSave();
      }
  }

  private void AttemptRevokePersistentUnlockable(UserProfile userProfile, UnlockableDef unlockableDef)
  {
      if (EclipseUnlockString.TryParse(unlockableDef.cachedName, out EclipseUnlockString eclipseUnlockString))
      {
          bool foundNextLowest = false;
          for (int i = userProfile.achievementsList.Count - 1; i >= 0; i--)
          {
              string achievementIdentifier = userProfile.achievementsList[i];
              if (EclipseUnlockString.Exists(achievementIdentifier) && EclipseUnlockString.TryParse(achievementIdentifier, out EclipseUnlockString other))
              {
                  if (other.survivorName == eclipseUnlockString.survivorName)
                  {
                      if (other.eclipseLevel >= eclipseUnlockString.eclipseLevel)
                      {
                          Logger.LogInfo($"Revoking persistent eclipse unlockable {achievementIdentifier} for UserProfile {userProfile.name}.");
                          userProfile.achievementsList.RemoveAt(i);
                          userProfile.RequestEventualSave();
                      }
                      else if (other.eclipseLevel == eclipseUnlockString.eclipseLevel - 1)
                      {
                          foundNextLowest = true;
                      }
                  }
              }
          }
          if (!foundNextLowest && eclipseUnlockString.eclipseLevel > EclipseRun.minUnlockableEclipseLevel)
          {
              eclipseUnlockString.eclipseLevel--;
              if (CanPersistCheck(eclipseUnlockString))
              {
                  Logger.LogInfo($"UserProfile {userProfile.name} is missing the next lowest persisten eclipse unlockable ({eclipseUnlockString}). Adding.");
                  userProfile.achievementsList.Add(eclipseUnlockString.ToString());
                  userProfile.RequestEventualSave();
              }
          }
      }
  }

  public struct EclipseUnlockString
  {
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public static bool Exists(string unlockString)
      {
          return unlockString.StartsWith(eclipseString);
      }

      public static bool TryParse(string unlockString, out EclipseUnlockString eclipseUnlockString)
      {
          if (!Exists(unlockString))
          {
              eclipseUnlockString = default;
              return false;
          }
          int firstIndex = 7;
          int lastIndex = unlockString.LastIndexOf('.');
          if (firstIndex == lastIndex || !int.TryParse(unlockString.Substring(lastIndex + 1), out int level))
          {
              eclipseUnlockString = default;
              return false;
          }
          eclipseUnlockString = new EclipseUnlockString
          {
              eclipseLevel = level,
              survivorName = unlockString.Substring(firstIndex + 1, lastIndex - firstIndex - 1)
          };
          return true;
      }

      public string survivorName;
      public int eclipseLevel;

      public override string ToString()
      {
          return string.Concat(eclipseString, survivorName, ".", eclipseLevel);
      }
  }*/
}
