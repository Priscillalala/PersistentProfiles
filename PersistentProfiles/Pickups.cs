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
    public static class Pickups
    {
        public static Dictionary<UserProfile, string> orphanedPickupsLookup;

        public static void Init()
        {
            orphanedPickupsLookup = new Dictionary<UserProfile, string>();
            On.RoR2.SaveFieldAttribute.SetupPickupsSet += SaveFieldAttribute_SetupPickupsSet;
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
    }
}
