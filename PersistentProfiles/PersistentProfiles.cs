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
using System.Collections;

[module: UnverifiableCode]
#pragma warning disable
[assembly: SecurityPermission(System.Security.Permissions.SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore
[assembly: HG.Reflection.SearchableAttribute.OptIn]

namespace PersistentProfiles
{
    [BepInPlugin("com.groovesalad.PersistentProfiles", "PersistentProfiles", "1.0.0")]
    public class PersistentProfiles : BaseUnityPlugin
    {
        const string configSection = "PersistentProfiles";
        const string trustedProfileFlag = "GS_PersistentProfiles_Trusted";

        public static ManualLogSource logger;
        public static PersistentProfiles instance;
        public static ConfigFile config;
        public static event Action<UserProfile, XDocument> onUntrustedProfileDiscovered;

        public void Awake()
        {
            logger = Logger;
            instance = this;
            config = new ConfigFile(System.IO.Path.Combine(Paths.ConfigPath, "PersistentProfiles.cfg"), true, Info.Metadata);
            Stats.includeStats = config.Bind(configSection, "Preserve Modded Stats", true, "Prevent modded stats from being wiped for as long as this mod is installed.").Value;
            Stats.includeAllUnlocks = config.Bind(configSection, "Preserve Modded Unlockables", true, "Prevent modded unlockables that are not tied to achievements (like stage and monster logs) from being wiped for as long as this mod is installed. Eclipse unlocks are always saved.").Value;
            Stats.Init();
            Eclipse.ignoreModdedEclipse = config.Bind(configSection, "Ignore Extended Eclipse Levels", true, "Only manage Eclipse 8 and below to avoid possible conflicts with mods that add new eclipse levels.").Value;
            Eclipse.Init();
            if (config.Bind(configSection, "Preserve Modded Loadouts", true, "Prevent modded loadout preferences from being wiped for as long as this mod is installed.").Value)
            {
                BodyLoadouts.Init();
            }
            if (config.Bind(configSection, "Preserve Modded Pickups", true, "Prevent discovered modded items and equipment from being wiped for as long as this mod is installed.").Value)
            {
                Pickups.Init();
            }

            On.RoR2.XmlUtility.ToXml += XmlUtility_ToXml;
            On.RoR2.XmlUtility.FromXml += XmlUtility_FromXml;
        }

        private XDocument XmlUtility_ToXml(On.RoR2.XmlUtility.orig_ToXml orig, UserProfile userProfile)
        {
            XDocument doc = orig(userProfile);
            if (doc?.Root != null)
            {
                doc.Root.Add(new XElement(trustedProfileFlag));
            }
            return doc;
        }

        private UserProfile XmlUtility_FromXml(On.RoR2.XmlUtility.orig_FromXml orig, XDocument doc)
        {
            UserProfile userProfile = orig(doc);
            if (userProfile != null && doc?.Root != null && doc.Root.Element(trustedProfileFlag) == null)
            {
                onUntrustedProfileDiscovered?.Invoke(userProfile, doc);
                StartCoroutine(SaveUntrustedProfile(userProfile));
            }
            return userProfile;
        }

        public IEnumerator SaveUntrustedProfile(UserProfile userProfile)
        {
            yield return new WaitForFixedUpdate();
            if (userProfile != null && userProfile.canSave)
            {
                PlatformSystems.saveSystem?.Save(userProfile, false);
            }
        }
    }
}
