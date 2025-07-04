﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using System.Collections;

namespace GBufferCapture
{

    [HarmonyPatch(typeof(Creature), nameof(Creature.Start))]
    public static class JellyRayOnGBuffer_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Creature __instance)
        {
            if (!(__instance is Jellyray))
            {
                return;
            }
            Jellyray jellyray = __instance as Jellyray;
            jellyray.StartCoroutine(ApplyGbufferFix(jellyray));
        }

        private static IEnumerator ApplyGbufferFix(Jellyray jellyrayInstance)
        {
            yield return new WaitForEndOfFrame();
            SkinnedMeshRenderer renderer = jellyrayInstance.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (renderer == null)
            {
                Debug.LogError("Cant find SkinnedMeshRenderer of JellyRay!");
                yield break;
            }
            foreach (var mat in renderer.materials)
            {
                if (mat != null && mat.shader.name == "MarmosetUBER")
                {
                    mat.renderQueue = (int) UnityEngine.Rendering.RenderQueue.Geometry;
                    mat.SetOverrideTag("RenderType", "Opaque");
                    mat.SetFloat("_Mode", 0f);
                    mat.SetFloat("_ZWrite", 1f);
                }
            }
        }
    }

}
