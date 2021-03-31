﻿using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using ValheimPlus;
using ValheimPlus.Configurations;
using ValheimPlus.Utility;

namespace ValheimPlus.GameClasses
{
    /// <summary>
    /// Determines how a creature should behave if it should be dead based on Tameable options.
    /// </summary>
    [HarmonyPatch(typeof(Character), "CheckDeath")]
    public static class Character_CheckDeath_Patch
    {
        public static void Prefix(Character __instance)
        {
            if (Configuration.Current.Tameable.IsEnabled)
            {
                Character character = __instance;
                ZDO zdo = character.m_nview.GetZDO();

                if (!character.IsTamed() || zdo == null)
                    return;

                if (ShouldSetStunned(character))
                {
                    character.SetHealth(character.GetMaxHealth());
                    character.m_animator.SetBool("sleeping", true);
                    zdo.Set("sleeping", true);
                    zdo.Set("isRecoveringFromStun", true);
                }
            }
        }

        private static bool ShouldSetStunned(Character character)
        {
            return (TameableMortalityTypes)Configuration.Current.Tameable.mortality == TameableMortalityTypes.Essential && character.GetHealth() <= 0f && !Configuration.Current.Tameable.onlyOwnerCanHurt;
        }
    }

    /// <summary>
    /// Determines whether a tamed creature should take damage or not based on Tameable options.
    /// </summary>
    [HarmonyPatch(typeof(Character), "Damage")]
    public static class Character_Damage_Patch
    {
        public static void Prefix(Character __instance, ref HitData hit)
        {
            if (Configuration.Current.Tameable.IsEnabled)
            {
                Character character = __instance;
                ZDO zdo = character.m_nview.GetZDO();

                if (!character.IsTamed() || zdo == null)
                    return;

                if (ShouldIgnoreDamage(hit, zdo))
                    hit = new HitData();
            }
        }

        private static bool ShouldIgnoreDamage(HitData hit, ZDO zdo)
        {
            return ((TameableMortalityTypes)Configuration.Current.Tameable.mortality == TameableMortalityTypes.Immortal || (Configuration.Current.Tameable.onlyOwnerCanHurt && hit.GetAttacker().m_nview.GetZDO().m_owner != zdo.m_owner) || zdo.GetBool("isRecoveringFromStun")) && !Configuration.Current.Tameable.onlyOwnerCanHurt;
        }
    }

    /// <summary>
    /// Allow tweaking of fall damage
    /// </summary>
    [HarmonyPatch(typeof(Character), "UpdateGroundContact")]
    public static class Character_UpdateGroundContact_Transpiler
    {
        private static readonly MethodInfo method_calculateFallDamage = AccessTools.Method(typeof(Character_UpdateGroundContact_Transpiler), nameof(calculateFallDamage));

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (!Configuration.Current.Player.IsEnabled)
                return instructions;

            List<CodeInstruction> il = instructions.ToList();

            // The original code checks if (this.IsPlayer() && num > 4f); if so, it calculates the fall damage as Mathf.Clamp01((num - 4f) / 16f) * 100f
            // ... where num is the fall distance.
            // We want to remove this calculation and replace it with our own, so we replace the calculation with a call to our own method, defined below this one.
            for (int i = 0; i < il.Count; i++)
            {
                if (il[i].opcode != OpCodes.Newobj) continue;
                if (i + 11 >= il.Count || il[i + 1].opcode != OpCodes.Stloc_2 || il[i + 11].opcode != OpCodes.Mul)
                {
                    // Looks like the wrong location, so we failed and this transpiler needs updating
                    ZLog.Log("Unable to transpile Character::UpdateGroundContact to patch fall damage calculation");
                    return instructions;
                }

                // OK, looks like we're good! We want to patch a bit further ahead.
                // il[i] now points to: newobj instance void HitData::.ctor()
                // We want to keep that and the three instructions after: stloc.2, ldloc.2, ldflda (...)
                // We want to remove the next 8 instructions (from ldloc.0 to mul, inclusive).
                il.RemoveRange(i + 4, 8);

                // We now have i+3 = ldflda m_damage, i+4 = stfld m_damage.
                // We want to insert the call to our damage calculation in between.
                il.Insert(i + 4, new CodeInstruction(OpCodes.Ldloc_0)); // Load the fall distance
                il.Insert(i + 5, new CodeInstruction(OpCodes.Call, method_calculateFallDamage));

                return il.AsEnumerable();
            }

            ZLog.Log("Unable to transpile Character::UpdateGroundContact to patch fall damage calculation");
            return instructions;
        }

        private static float calculateFallDamage(float fallDistance)
        {
            if (fallDistance < 4f)
                return 0f;

            float linearFallDamage = ((fallDistance - 4f) / 16f) * 100f;
            float scaledFallDamage = Helper.applyModifierValue(linearFallDamage, Configuration.Current.Player.fallDamageScalePercent);
            float fallDamage = Math.Min(scaledFallDamage, Configuration.Current.Player.maxFallDamage);

            return fallDamage;
        }
    }
}