﻿using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using static RadicalLibrary.Spline;

namespace GBufferCapture 
{

    public enum SavingFormat
    { 
        PNG,
        JPG
    }

    [BepInPlugin(MyGUID, PluginName, VersionString)]
    public class GBufferCapturePlugin : BaseUnityPlugin
    {

        public static GBufferCapturePlugin instance { get; private set; }
        public static string assetBundlePath = Paths.PluginPath + "\\GBufferCapture\\Shaders\\bundle";
        public static string captureFolder = Paths.PluginPath + "\\GBufferCapture\\captures";

        private Camera mainCam;
        private Camera gbufferCam;
        public int captureWidth => captureWidthEntry.Value;
        public int captureHeight => captureHeightEntry.Value;

        private const string MyGUID = "com.Salu.GBufferCapture";
        private const string PluginName = "GBufferCapture";
        private const string VersionString = "1.0.0";
        private static readonly Harmony harmony = new Harmony(MyGUID);
        public static ManualLogSource Log = new ManualLogSource(PluginName);

        public static ConfigEntry<float> captureIntervalEntry;
        public static ConfigEntry<float> gbuffersMaxRenderDistanceEntry;
        public static ConfigEntry<float> depthControlWaterLevelToleranceEntry;
        public static ConfigEntry<bool> seeOnGUIEntry;
        public static ConfigEntry<bool> fogEntry;
        public static ConfigEntry<int> captureWidthEntry;
        public static ConfigEntry<int> captureHeightEntry;
        public static ConfigEntry<SavingFormat> savingFormatEntry;
        public static ConfigEntry<int> jpgQualityEntry;

        private void Awake()
        {
            instance = this;
            Directory.CreateDirectory(captureFolder);
            captureIntervalEntry = Config.Bind("General", "CaptureInterval", 1.0f, "Set time between captures in seconds");
            gbuffersMaxRenderDistanceEntry = Config.Bind("General", "GBufferMaxRenderDistanceUnderwater", 120.0f, "Max saw distance by gbuffers underwater, upperwater default is 1000.0f");
            depthControlWaterLevelToleranceEntry = Config.Bind("General", "DepthControlWaterLevelTolerance", 100.0f, "the mod shaders converts depthmap to worldPos and may fail when you shake camera vertically too fast, increase this value to reduce/remove this effect error in captured gbuffers");
            seeOnGUIEntry = Config.Bind("General", "seeOnGUI", true, "toggle captures GUI");
            fogEntry = Config.Bind("General", "Fog", true, "toggle fog without affecting captures");

            captureWidthEntry = Config.Bind("Capture", "CaptureWidth", 960, "Resize capture width");
            captureHeightEntry = Config.Bind("Capture", "CaptureHeight", 540, "Resize capture height");
            savingFormatEntry = Config.Bind("Capture", "SavingFormat", SavingFormat.JPG, "Define saving format extension");
            jpgQualityEntry = Config.Bind("Capture", "JPG Quality", 95, "jpg quality");

            Logger.LogInfo($"PluginName: {PluginName}, VersionString: {VersionString} is loading...");
            harmony.PatchAll();
            Logger.LogInfo($"PluginName: {PluginName}, VersionString: {VersionString} is loaded.");
            Log = Logger;

            CountTakenCaptures();
        }

        private void CountTakenCaptures()
        {
            string[] files = Directory.GetFiles(captureFolder);
            string[] onlyFinalRenders = files.Where(f => System.IO.Path.GetFileName(f).Contains("_base")).ToArray();
            totalCaptures = onlyFinalRenders.Length;
        }

        private void RemoveScubaMaskFromGBuffers()
        {
            //most screen trash uses a component called "HideForScreenshots"
            Transform player = GameObject.Find("Player")?.transform;
            if (player == null)
            {
                return;
            }
            Transform scubaMask = player.Find("camPivot/camRoot/camOffset/pdaCamPivot/SpawnPlayerMask");
            if (scubaMask == null)
            {
                return;
            }
            scubaMask.gameObject.SetActive(false);
        }

        public static float gbuffersMaxRenderDistance => gbuffersMaxRenderDistanceEntry.Value;

        private CommandBuffer cb;

        private RenderTexture mainCamTargetTextureRT;
        private RenderTexture mainRT;
        private RenderTexture depthRT;
        private RenderTexture normalRT;
        private RenderTexture albedoRT;
        private RenderTexture emissionRT;
        private RenderTexture idRT;

        private Shader midShader;
        private Material midMaterial;
        private Shader texControlDepthShader;
        private Material mcdMaterial; //monocromatic control depth
        private Shader monocromaticControlDepthShader;
        private Material tcdMaterial; //texture control depth
        private Shader emissionShader;
        private Material emissionMat;

        public static bool lastFog;

        private void SetupCB()
        {
            RemoveScubaMaskFromGBuffers();
            Debug.LogWarning("mod core started");
            GameObject gbufferCamObj = new GameObject("GBufferCam");
            gbufferCamObj.transform.SetParent(mainCam.transform.parent);
            gbufferCamObj.transform.position = mainCam.transform.position;
            gbufferCamObj.transform.rotation = mainCam.transform.rotation;
            gbufferCam = gbufferCamObj.AddComponent<Camera>();
            gbufferCam.CopyFrom(mainCam);
            gbufferCam.depth = mainCam.depth - 1;

            if (mainRT == null)
            { 
                mainRT = new RenderTexture(mainCam.pixelWidth, mainCam.pixelHeight, 24, RenderTextureFormat.ARGB32);
                mainRT.Create();
                depthRT = new RenderTexture(mainCam.pixelWidth, mainCam.pixelHeight, 0, RenderTextureFormat.ARGBFloat);
                depthRT.Create();
                normalRT = new RenderTexture(mainCam.pixelWidth, mainCam.pixelHeight, 0, RenderTextureFormat.ARGBFloat);
                normalRT.Create();
                albedoRT = new RenderTexture(mainCam.pixelWidth, mainCam.pixelHeight, 0, RenderTextureFormat.ARGBFloat);
                albedoRT.Create();
                emissionRT = new RenderTexture(mainCam.pixelWidth, mainCam.pixelHeight, 0, RenderTextureFormat.ARGB32);
                emissionRT.Create();
                idRT = new RenderTexture(mainCam.pixelWidth, mainCam.pixelHeight, 0, RenderTextureFormat.ARGB32);
                idRT.Create();
            }

            if (monocromaticControlDepthShader == null)
            { 
                monocromaticControlDepthShader = Utils.LoadExternalShader("DepthPost");
                mcdMaterial = new Material(monocromaticControlDepthShader);
                mcdMaterial.hideFlags = HideFlags.HideAndDontSave;

                texControlDepthShader = Utils.LoadExternalShader("NormalPost");
                tcdMaterial = new Material(texControlDepthShader);
                tcdMaterial.hideFlags = HideFlags.HideAndDontSave;

                emissionShader = Utils.LoadExternalShader("EmissionMap");
                emissionMat = new Material(emissionShader);
                emissionMat.hideFlags = HideFlags.HideAndDontSave;

                midShader = Utils.LoadExternalShader("MaterialID");
                midMaterial = new Material(midShader);
                midMaterial.hideFlags = HideFlags.HideAndDontSave;
            }

            gbufferCam.depthTextureMode = DepthTextureMode.Depth;

            cb = new CommandBuffer();
            cb.name = "GBuffer Capture Command Buffer";

            //código base para shaderID e emissionMap
            //var renderers = FindObjectsOfType<Renderer>();

            //cb.SetRenderTarget(idRT);
            //cb.ClearRenderTarget(true, true, Color.black);
            //var props = new MaterialPropertyBlock();
            //foreach (var rend in renderers)
            //{
            //    if (rend.sharedMaterial == null)
            //    { 
            //        Debug.Log($"pulando renderer {rend}");
            //        continue;
            //    }
            //    if (rend is ParticleSystemRenderer)
            //    { 
            //        continue;
            //    }
            //    props.Clear();
            //    int matID = rend.sharedMaterial.GetInstanceID();
            //    props.SetFloat("_MaterialID", matID);
            //    rend.SetPropertyBlock(props);
            //    cb.DrawRenderer(rend, midMaterial);
            //}

            //cb.SetRenderTarget(emissionRT);
            //cb.ClearRenderTarget(true, true, Color.black);
            //foreach (var rend in renderers)
            //{
                // uma das idéias para capturar o emissionMap é obter todos os gameObjects que contem Light e iterar sobre objetos parentes com mesh, pois estes serão a fonte de luz, a esses objetos se aplica o material
            //    if (rend.sharedMaterial == null)
            //    {
            //        Debug.Log($"pulando renderer {rend}");
            //        continue;
            //    }
            //    if (rend is ParticleSystemRenderer)
            //    {
            //        continue;
            //    }
            //    var mat = rend.sharedMaterial;
            //    if (!mat.HasProperty("_EmissionMap"))
            //    { 
            //        cb.DrawRenderer(rend, emissionMat);
            //    }
            //}

            cb.Blit(BuiltinRenderTextureType.CameraTarget, depthRT, mcdMaterial);
            cb.Blit(BuiltinRenderTextureType.GBuffer2, normalRT, tcdMaterial);
            cb.Blit(BuiltinRenderTextureType.GBuffer0, albedoRT, tcdMaterial);
            gbufferCam.AddCommandBuffer(CameraEvent.AfterEverything, cb);
        }

        private WaterGBufferInjector injectorInstance;

        void SetupWaterSurfaceOnGBuffers()
        {
            if (injectorInstance != null)
            {
                return;
            }
            injectorInstance = gbufferCam.gameObject.AddComponent<WaterGBufferInjector>();
        }

        private GUIStyle labelStyle;

        void OnGUI()
        {
            if (cb != null && seeOnGUIEntry.Value)
            {
                GUI.DrawTexture(new Rect(0, 0, 256, 144), depthRT, ScaleMode.StretchToFill, false);
                GUI.DrawTexture(new Rect(0, 144, 256, 144), normalRT, ScaleMode.StretchToFill, false);
                GUI.DrawTexture(new Rect(0, 288, 256, 144), albedoRT, ScaleMode.StretchToFill, false);
                if (!fogEntry.Value)
                { 
                    GUI.DrawTexture(new Rect(0, 432, 256, 144), mainRT, ScaleMode.StretchToFill, false);
                }
            }
            string labelText = $"Mod Core {(cb == null ? "Disabled" : "Enabled")}\nCapture {(isCapturing ? "Enabled" : "Disabled")}\nTotal Captures: {totalCaptures}\nCapture Interval: {captureIntervalEntry.Value}s";
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label);
                labelStyle.normal.textColor = Color.white;
                labelStyle.fontSize = 20;
                labelStyle.fontStyle = FontStyle.Bold;
            }
            GUI.Label(new Rect(10, 950, 300, 200), labelText, labelStyle);
        }

        private float depthControlWaterLevel => depthControlWaterLevelToleranceEntry.Value;

        void LateUpdate()
        {
            if (cb != null && mainCam != null)
            {
                //isso seria usado no autodepth shader
                //cb.SetGlobalMatrix("_CameraInvProj", mainCam.projectionMatrix.inverse);
                //Matrix4x4 worldToCameraMatrix = mainCam.worldToCameraMatrix;
                //Transform transform = FindObjectOfType<WaterscapeVolume>().waterPlane.transform;
                //Plane plane = new Plane(transform.up, transform.position);
                //Plane plane2 = worldToCameraMatrix.TransformPlane(plane);
                //Vector3 normal = plane2.normal;
                //cb.SetGlobalVector("_UweVsWaterPlane", new Vector4(normal.x, normal.y, normal.z, plane2.distance));

                cb.SetGlobalMatrix("_CameraProj", mainCam.projectionMatrix);
                cb.SetGlobalMatrix("CameraToWorld", mainCam.cameraToWorldMatrix);
                cb.SetGlobalFloat("_DepthCutoff", gbuffersMaxRenderDistance);
                if (UnderWaterListener_Patch.IsUnderWater())
                {
                    cb.SetGlobalFloat("_WaterLevel", depthControlWaterLevel);
                }
                else
                {
                    cb.SetGlobalFloat("_WaterLevel", -depthControlWaterLevel);
                }
            }
        }

        private static int totalCaptures = 0;
        private bool isCapturing = false;
        private float timer = 0f;
        private float captureInterval => captureIntervalEntry.Value;

        public void ClearCB()
        {
            Debug.LogWarning("mod core stopped");
            if (mainCam != null)
            {
                mainCam.targetTexture = mainCamTargetTextureRT;
            }
            mainCam = null;
            fogEntry.Value = true;
            if (gbufferCam != null && cb != null)
            {
                gbufferCam.RemoveCommandBuffer(CameraEvent.AfterEverything, cb);
                GameObject.DestroyImmediate(gbufferCam.gameObject);
            }
            cb?.Release();
            cb = null;
            injectorInstance = null;
            gbufferCam = null;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F11))
            {
                if (gbufferCam != null)
                {
                    ClearCB();
                }
                else
                { 
                    mainCam = FindObjectOfType<WaterSurfaceOnCamera>()?.gameObject.GetComponent<Camera>();
                    if (mainCam != null) //prevent activating mod core at scene loading
                    {
                        mainCamTargetTextureRT = mainCam.targetTexture;
                        SetupCB();
                        SetupWaterSurfaceOnGBuffers();
                    }
                }
            }

            if (Input.GetKeyDown(KeyCode.Alpha0))
            {
                //Utils.InvestigateCenterObject();
                //Utils.ReplaceShader("UWE/Terrain/Triplanar with Capping", "TriplanarWithCapping");
                //Utils.ReplaceShader("UWE/Terrain/Triplanar", "Triplanar");
            }

            if (Input.GetKeyDown(KeyCode.Alpha9)) 
            {
                fogEntry.Value = !fogEntry.Value;
            }

            if (lastFog != fogEntry.Value && cb != null)
            {
                if (!fogEntry.Value)
                {
                    mainCam.targetTexture = mainRT;
                }
                else
                {
                    mainCam.targetTexture = mainCamTargetTextureRT;
                }
                lastFog = fogEntry.Value;
            }
            
            if (cb == null)
            { 
                lastFog = !fogEntry.Value;
            }

            if (Input.GetKeyDown(KeyCode.F10))
            {
                isCapturing = !isCapturing;
                Debug.Log($"G-Buffer capture {(isCapturing ? "started" : "stopped")}");
            }

            if (isCapturing && cb != null)
            {
                timer += Time.deltaTime;
                if (timer >= captureInterval)
                {
                    timer = 0f;
                    string timestamp = $"{System.DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}";
                    if (fogEntry.Value)
                    {
                        mainCam.targetTexture = mainRT;
                        mainCam.Render();
                        mainCam.targetTexture = mainCamTargetTextureRT;
                    }

                    Action<string, RenderTexture, int, int> saveFunc;
                    switch (savingFormatEntry.Value)
                    {
                        case SavingFormat.PNG:
                            saveFunc = (fileName, rt, w, h) => Utils.SaveTexture(fileName, rt, w, h, ".png", t => t.EncodeToPNG());
                            break;
                        case SavingFormat.JPG:
                            saveFunc = (fileName, rt, w, h) => Utils.SaveTexture(fileName, rt, w, h, ".jpg", t => t.EncodeToJPG(jpgQualityEntry.Value));
                            break;
                        default:
                            throw new NotSupportedException($"Unsupported saving type: {savingFormatEntry.Value}");
                    }
                    saveFunc($"{timestamp}_base", mainRT, captureWidth, captureHeight);
                    saveFunc($"{timestamp}_depth", depthRT, captureWidth, captureHeight);
                    saveFunc($"{timestamp}_normal", normalRT, captureWidth, captureHeight);
                    saveFunc($"{timestamp}_albedo", albedoRT, captureWidth, captureHeight);
                    totalCaptures++;
                }
            }
        }

    }
}
