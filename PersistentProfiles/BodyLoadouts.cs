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
    public static class BodyLoadouts
    {
        public static Dictionary<UserProfile, XElement[]> orphanedBodyLoadoutsLookup;

        public static void Init()
        {
            orphanedBodyLoadoutsLookup = new Dictionary<UserProfile, XElement[]>();
            On.RoR2.XmlUtility.ToXml += XmlUtility_ToXml;
            On.RoR2.XmlUtility.FromXml += XmlUtility_FromXml;
            On.RoR2.SaveSystem.Copy += SaveSystem_Copy;
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
    }
}
