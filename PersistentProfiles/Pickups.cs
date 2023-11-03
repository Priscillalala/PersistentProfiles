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

namespace PersistentProfiles
{
    public static class Pickups
    {
        public static Dictionary<UserProfile, string> orphanedPickupsLookup;

        public static void Init()
        {
            orphanedPickupsLookup = new Dictionary<UserProfile, string>();
            On.RoR2.SaveFieldAttribute.SetupPickupsSet += SaveFieldAttribute_SetupPickupsSet;
            if (UserProfile.saveFields != null)
            {
                foreach (SaveFieldAttribute saveField in UserProfile.saveFields)
                {
                    if (saveField.explicitSetupMethod == nameof(SaveFieldAttribute.SetupPickupsSet))
                    {
                        ModifyPickupsSaveField(saveField);
                    }
                }
            }
        }

        private static void SaveFieldAttribute_SetupPickupsSet(On.RoR2.SaveFieldAttribute.orig_SetupPickupsSet orig, SaveFieldAttribute self, FieldInfo fieldInfo)
        {
            orig(self, fieldInfo);
            ModifyPickupsSaveField(self);
        }

        public static void ModifyPickupsSaveField(SaveFieldAttribute saveField)
        {
            Func<UserProfile, string> origGetter = saveField.getter;
            saveField.getter = (userProfile) =>
            {
                string valueString = origGetter(userProfile);
                if (orphanedPickupsLookup.TryGetValue(userProfile, out string orphanedPickups))
                {
                    PersistentProfiles.logger.LogInfo("Getting orphaned pickups: " + orphanedPickups);
                    valueString += " " + orphanedPickups;
                }
                return valueString;
            };
            saveField.setter = (Action<UserProfile, string>)Delegate.Combine(saveField.setter, (UserProfile userProfile, string valueString) =>
            {
                string orphanedPickups = string.Join(" ",
                    valueString.Split(' ')
                    .Where(x => !PickupCatalog.FindPickupIndex(x).isValid)
                    .Distinct()
                    );
                if (!string.IsNullOrWhiteSpace(orphanedPickups))
                {
                    orphanedPickupsLookup[userProfile] = orphanedPickups;
                    PersistentProfiles.logger.LogInfo($"Orphaned pickups for UserProfile {userProfile.name}: {orphanedPickups}");
                }
            });
            saveField.copier = (Action<UserProfile, UserProfile>)Delegate.Combine(saveField.copier, (UserProfile srcProfile, UserProfile destProfile) =>
            {
                if (orphanedPickupsLookup.TryGetValue(srcProfile, out string orphanedPickups))
                {
                    orphanedPickupsLookup[destProfile] = orphanedPickups;
                }
            });
        }
    }
}
