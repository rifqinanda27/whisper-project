using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Whisper;
using Whisper.Utils;
using WhisperDemo;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace WhisperDemo.Editor
{
    public static class WhisperSceneSetupEditor
    {
        [MenuItem("Whisper Demo/1. Setup Scene & Configure Windows + Android")]
        public static void SetupEverything()
        {
            ConfigureAndroidAndWindowsSettings();
            BuildDemoScene();
        }

        [MenuItem("Whisper Demo/2. Configure Android Settings (IL2CPP + ARM64)")]
        public static void ConfigureAndroidAndWindowsSettings()
        {
            // Required by com.whisper.unity CheckAndroidPreprocessBuild.cs
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;

            // Use standard Android Activity entry point + OpenGLES3 to avoid Unity 6 GameActivity/Vulkan blank screen
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });

            // Force Landscape orientation so the 1280x720 UI layout is never clipped in Portrait mode
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            // Set company/product/applicationIdentifier
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.demo.whisperkeywordmover");
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "com.demo.whisperkeywordmover");

            AssetDatabase.SaveAssets();
            Debug.Log("[WhisperSetup] Configured Android (IL2CPP + ARM64 + OpenGLES3 + Activity + Landscape) & Windows Standalone settings.");
        }

        [MenuItem("Whisper Demo/3. Run Automated Whisper Verification Test")]
        public static void RunAutomatedWhisperTest()
        {
            string modelPath = Path.Combine(Application.streamingAssetsPath, "Whisper/ggml-tiny.bin");
            if (!File.Exists(modelPath))
            {
                Debug.LogError($"[WhisperTest] Model file missing at: {modelPath}");
                return;
            }

            // Prepare clean log files for the benchmark run
            string logDir = WhisperKeywordMover.GetOfflineLogDirectory();
            string csvPath = Path.Combine(logDir, "whisper_response_times.csv");
            string txtPath = Path.Combine(logDir, "whisper_response_times.txt");
            string reportPath = Path.Combine(logDir, "whisper_offline_benchmark_report.md");

            if (File.Exists(csvPath)) File.Delete(csvPath);
            if (File.Exists(txtPath)) File.Delete(txtPath);

            // 1. Measure Model Initialization (CPU mode & GPU mode)
            var cpuCtxParams = WhisperContextParams.GetDefaultParams();
            cpuCtxParams.UseGpu = false;
            var swCpuLoad = System.Diagnostics.Stopwatch.StartNew();
            var cpuWrapper = WhisperWrapper.InitFromFile(modelPath, cpuCtxParams);
            swCpuLoad.Stop();
            long cpuLoadMs = swCpuLoad.ElapsedMilliseconds;

            var gpuCtxParams = WhisperContextParams.GetDefaultParams();
            gpuCtxParams.UseGpu = true;
            var swGpuLoad = System.Diagnostics.Stopwatch.StartNew();
            var gpuWrapper = WhisperWrapper.InitFromFile(modelPath, gpuCtxParams);
            swGpuLoad.Stop();
            long gpuLoadMs = swGpuLoad.ElapsedMilliseconds;

            var activeWrapper = cpuWrapper ?? gpuWrapper;
            if (activeWrapper == null)
            {
                Debug.LogError("[WhisperTest] Failed to initialize WhisperWrapper from ggml-tiny.bin!");
                return;
            }

            Debug.Log($"[WhisperTest] SUCCESS: Loaded ggml-tiny.bin offline | CPU Init: {cpuLoadMs} ms | GPU Init: {gpuLoadMs} ms (Multilingual={activeWrapper.IsMultilingual})");

            var mover = UnityEngine.Object.FindFirstObjectByType<WhisperKeywordMover>();
            if (mover != null)
            {
                mover.ClearResponseRecords();
                mover.ModelLoadTimeMs = cpuLoadMs;
            }

            string[] testAssetPaths =
            {
                "Assets/Resources/OfflineTestAudio/cmd_01_up_right_jump.wav",
                "Assets/Resources/OfflineTestAudio/cmd_02_left_forward_spin.wav",
                "Assets/Resources/OfflineTestAudio/cmd_03_bigger_green.wav",
                "Assets/Resources/OfflineTestAudio/cmd_04_smaller_red_stop.wav",
                "Assets/Resources/OfflineTestAudio/cmd_05_down_back_reset.wav",
                "Assets/Resources/jfk.wav"
            };

            var whisperParam = WhisperParams.GetDefaultParams();
            whisperParam.Language = "en";
            whisperParam.NoContext = true;
            whisperParam.SingleSegment = true;
            whisperParam.InitialPrompt =
                "Voice command keywords: up, down, left, right, forward, back, jump, spin, rotate, stop, reset, bigger, smaller, red, green, blue, yellow.";

            // Warm-up run for both CPU and GPU wrappers (standard practice for accurate response time measurement)
            AudioClip warmupClip = AssetDatabase.LoadAssetAtPath<AudioClip>(testAssetPaths[0]);
            long cpuWarmupMs = 0;
            long gpuWarmupMs = 0;
            if (warmupClip != null)
            {
                if (cpuWrapper != null)
                {
                    var swWarmCpu = System.Diagnostics.Stopwatch.StartNew();
                    cpuWrapper.GetText(warmupClip, whisperParam);
                    swWarmCpu.Stop();
                    cpuWarmupMs = swWarmCpu.ElapsedMilliseconds;
                }

                if (gpuWrapper != null)
                {
                    var swWarmGpu = System.Diagnostics.Stopwatch.StartNew();
                    gpuWrapper.GetText(warmupClip, whisperParam);
                    swWarmGpu.Stop();
                    gpuWarmupMs = swWarmGpu.ElapsedMilliseconds;
                }

                Debug.Log($"[WhisperTest] Offline Warm-up Run ({warmupClip.name}) completed | GPU Warm-up: {gpuWarmupMs} ms | CPU Warm-up: {cpuWarmupMs} ms");
            }

            var records = new List<WhisperKeywordMover.ResponseTimeRecord>();
            var cpuComparison = new List<(string clipName, float durationSec, long cpuMs, double cpuRtf, string transcript)>();

            // 1. Measure Offline English Test Runs on Primary Engine (GPU Accelerated on Windows) sequentially without CPU cache thrashing
            var primaryWrapper = gpuWrapper ?? cpuWrapper;
            foreach (string assetPath in testAssetPaths)
            {
                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                if (clip == null)
                {
                    Debug.LogWarning($"[WhisperTest] Missing audio clip at {assetPath}");
                    continue;
                }

                var swGpu = System.Diagnostics.Stopwatch.StartNew();
                var resGpu = primaryWrapper.GetText(clip, whisperParam);
                swGpu.Stop();

                string transcript = resGpu != null ? resGpu.Result.Trim() : "";
                bool isJfk = clip.name.IndexOf("jfk", StringComparison.OrdinalIgnoreCase) >= 0;

                if (mover != null)
                {
                    var rec = mover.RecordMeasuredRun(
                        transcript,
                        swGpu.ElapsedMilliseconds,
                        clip.length,
                        testType: gpuWrapper != null ? "OfflineEN_GPU" : "OfflineEN_CPU",
                        clipName: clip.name,
                        language: "en",
                        isSampleTest: isJfk,
                        logToCsv: true);
                    if (rec != null)
                        records.Add(rec);
                }
            }

            // 2. Measure Offline English Test Runs on CPU Fallback Engine sequentially
            if (cpuWrapper != null)
            {
                for (int i = 0; i < testAssetPaths.Length; i++)
                {
                    AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(testAssetPaths[i]);
                    if (clip == null) continue;

                    var swCpu = System.Diagnostics.Stopwatch.StartNew();
                    var resCpu = cpuWrapper.GetText(clip, whisperParam);
                    swCpu.Stop();

                    long cpuMs = swCpu.ElapsedMilliseconds;
                    double cpuRtf = clip.length > 0.01f ? (cpuMs / 1000.0) / clip.length : 0.0;
                    string transcript = resCpu != null ? resCpu.Result.Trim() : "";
                    cpuComparison.Add((clip.name, clip.length, cpuMs, cpuRtf, transcript));

                    long gpuMs = i < records.Count ? records[i].responseTimeMs : cpuMs;
                    Debug.Log($"[WhisperTest] OFFLINE EN TEST | Clip: {clip.name} ({clip.length:F2}s) | GPU: {gpuMs} ms | CPU: {cpuMs} ms | Transcript: \"{transcript}\"");
                }
            }

            // Generate Markdown Summary Report in OfflineTestLogs/whisper_offline_benchmark_report.md
            var md = new StringBuilder();
            md.AppendLine("# Offline Whisper.Unity Benchmark Report (English Language)");
            md.AppendLine();
            md.AppendLine($"- **Test Date**: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
            md.AppendLine($"- **Engine / Model**: `com.whisper.unity` (`ggml-tiny.bin`, 75 MB)");
            md.AppendLine($"- **Language Setting**: `en` (English)");
            md.AppendLine($"- **Connection Mode**: 100% Offline (Local On-Device Inference)");
            md.AppendLine($"- **Platform / Unity Version**: `{Application.platform}` / Unity `{Application.unityVersion}`");
            md.AppendLine($"- **Model Initialization Time (GPU)**: `{gpuLoadMs} ms` (Warm-up: `{gpuWarmupMs} ms`)");
            md.AppendLine($"- **Model Initialization Time (CPU)**: `{cpuLoadMs} ms` (Warm-up: `{cpuWarmupMs} ms`)");
            md.AppendLine();
            md.AppendLine("## 1. Offline English Transcription & Keyword Response Time Results");
            md.AppendLine();
            md.AppendLine("| # | Audio Clip | Audio Duration (s) | GPU Response Time (ms) | GPU RTF | CPU Response Time (ms) | CPU RTF | Transcribed Text (EN) | Triggered 3D Action |");
            md.AppendLine("|---|------------|-------------------:|-----------------------:|--------:|-----------------------:|--------:|-----------------------|---------------------|");

            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                long cpuMs = i < cpuComparison.Count ? cpuComparison[i].cpuMs : r.responseTimeMs;
                double cpuRtf = i < cpuComparison.Count ? cpuComparison[i].cpuRtf : r.realTimeFactor;
                md.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "| {0} | `{1}` | {2:F2} s | **{3} ms** | {4:F3}x | **{5} ms** | {6:F3}x | \"{7}\" | `{8}` |",
                    r.runIndex,
                    r.clipName,
                    r.audioDurationSec,
                    r.responseTimeMs,
                    r.realTimeFactor,
                    cpuMs,
                    cpuRtf,
                    r.transcript,
                    r.matchedCommands));
            }

            if (records.Count > 0)
            {
                double avgGpuMs = records.Average(r => r.responseTimeMs);
                long minGpuMs = records.Min(r => r.responseTimeMs);
                long maxGpuMs = records.Max(r => r.responseTimeMs);
                double avgGpuRtf = records.Average(r => r.realTimeFactor);

                double avgCpuMs = cpuComparison.Count > 0 ? cpuComparison.Average(c => c.cpuMs) : avgGpuMs;
                long minCpuMs = cpuComparison.Count > 0 ? cpuComparison.Min(c => c.cpuMs) : minGpuMs;
                long maxCpuMs = cpuComparison.Count > 0 ? cpuComparison.Max(c => c.cpuMs) : maxGpuMs;
                double avgCpuRtf = cpuComparison.Count > 0 ? cpuComparison.Average(c => c.cpuRtf) : avgGpuRtf;

                md.AppendLine();
                md.AppendLine("## 2. Summary Statistics");
                md.AppendLine();
                md.AppendLine($"- **Total Clips Tested**: `{records.Count}` (5 English Voice Command Clips + 1 JFK Speech Sample)");
                md.AppendLine($"- **GPU Offline Response Time**: Avg `{avgGpuMs:F1} ms` | Min `{minGpuMs} ms` | Max `{maxGpuMs} ms` | Avg RTF `{avgGpuRtf:F3}x` (~{(1.0 / Math.Max(0.001, avgGpuRtf)):F1}x faster than real-time)");
                md.AppendLine($"- **CPU Offline Response Time**: Avg `{avgCpuMs:F1} ms` | Min `{minCpuMs} ms` | Max `{maxCpuMs} ms` | Avg RTF `{avgCpuRtf:F3}x` (~{(1.0 / Math.Max(0.001, avgCpuRtf)):F1}x faster than real-time)");
            }

            File.WriteAllText(reportPath, md.ToString(), Encoding.UTF8);

            if (mover != null)
            {
                EditorUtility.SetDirty(mover);
                if (mover.responseTimeLogText != null)
                    EditorUtility.SetDirty(mover.responseTimeLogText);
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            }

            Debug.Log($"[WhisperTest] COMPLETE! Saved CSV to '{csvPath}' and Markdown Report to '{reportPath}'.");
        }

        [MenuItem("Whisper Demo/4. Build Windows Standalone (.exe)")]
        public static void BuildWindowsStandalone()
        {
            ConfigureAndroidAndWindowsSettings();
            string outDir = "Builds/Windows";
            Directory.CreateDirectory(outDir);
            string exePath = Path.Combine(outDir, "WhisperKeywordMover.exe");

            var report = BuildPipeline.BuildPlayer(
                new[] { "Assets/Scenes/SampleScene.unity" },
                exePath,
                BuildTarget.StandaloneWindows64,
                BuildOptions.CompressWithLz4HC);

            Debug.Log($"[WhisperBuild] Windows Build Result: {report.summary.result} -> {Path.GetFullPath(exePath)}");
        }

        [MenuItem("Whisper Demo/5. Build Android APK (.apk)")]
        public static void BuildAndroidApk()
        {
            ConfigureAndroidAndWindowsSettings();
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            }

            string outDir = "Builds/Android";
            Directory.CreateDirectory(outDir);
            string apkPath = Path.Combine(outDir, "WhisperKeywordMover.apk");

            var report = BuildPipeline.BuildPlayer(
                new[] { "Assets/Scenes/SampleScene.unity" },
                apkPath,
                BuildTarget.Android,
                BuildOptions.CompressWithLz4HC);

            Debug.Log($"[WhisperBuild] Android Build Result: {report.summary.result} -> {Path.GetFullPath(apkPath)}");
        }

        public static void BuildDemoScene()
        {
            var scene = EditorSceneManager.GetActiveScene();

            // Clean up any previous generated objects to avoid duplicates
            DestroyIfExists("WhisperController");
            DestroyIfExists("TargetMoverObject");
            DestroyIfExists("DemoFloor");
            DestroyIfExists("WhisperDemoCanvas");
            DestroyIfExists("EventSystem");

            // Position Main Camera nicely
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 2.6f, -6.5f);
                cam.transform.rotation = Quaternion.Euler(14f, 0f, 0f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.09f, 0.11f, 0.16f);
            }

            // Create Materials folder if needed
            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
                AssetDatabase.CreateFolder("Assets", "Materials");

            Shader litShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

            // Floor plane
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "DemoFloor";
            floor.transform.position = new Vector3(0f, -1.2f, 0f);
            floor.transform.localScale = new Vector3(1.5f, 1f, 1.5f);
            Material floorMat = CreateOrLoadMaterial("Assets/Materials/FloorMat.mat", litShader, new Color(0.16f, 0.19f, 0.25f));
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;

            // Target Object to move via Voice Commands
            GameObject targetObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            targetObj.name = "TargetMoverObject";
            targetObj.transform.position = new Vector3(-1.0f, 0.2f, 0f);
            targetObj.transform.rotation = Quaternion.Euler(0f, 25f, 0f);
            targetObj.transform.localScale = new Vector3(1.3f, 1.3f, 1.3f);
            Material targetMat = CreateOrLoadMaterial("Assets/Materials/TargetCubeMat.mat", litShader, new Color(0.2f, 0.75f, 1f));
            targetObj.GetComponent<Renderer>().sharedMaterial = targetMat;

            // Add a small nose/visor indicator on the cube so rotation/direction is visually obvious
            GameObject visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visor.name = "DirectionVisor";
            visor.transform.SetParent(targetObj.transform, false);
            visor.transform.localPosition = new Vector3(0f, 0.2f, -0.45f);
            visor.transform.localScale = new Vector3(0.65f, 0.25f, 0.2f);
            Material visorMat = CreateOrLoadMaterial("Assets/Materials/VisorMat.mat", litShader, new Color(1f, 0.9f, 0.2f));
            visor.GetComponent<Renderer>().sharedMaterial = visorMat;

            // Create WhisperController GameObject
            GameObject controllerGo = new GameObject("WhisperController");
            var whisperManager = controllerGo.AddComponent<WhisperManager>();
            whisperManager.ModelPath = "Whisper/ggml-tiny.bin";
            whisperManager.IsModelPathInStreamingAssets = true;
            whisperManager.language = "en";
            whisperManager.noContext = true;
            whisperManager.singleSegment = true;

            // Disable GPU by default so Android (which only links libggml-cpu.a) never fails GPU backend init
            var so = new SerializedObject(whisperManager);
            var useGpuProp = so.FindProperty("useGpu");
            if (useGpuProp != null) useGpuProp.boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();

            var micRecord = controllerGo.AddComponent<MicrophoneRecord>();
            micRecord.maxLengthSec = 15;
            micRecord.frequency = 16000;
            micRecord.useVad = true;
            micRecord.vadStop = true;
            micRecord.vadStopTime = 1.4f;
            micRecord.dropVadPart = true;
            micRecord.echo = false;

            var audioSource = controllerGo.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;

            var keywordMover = controllerGo.AddComponent<WhisperKeywordMover>();
            keywordMover.whisper = whisperManager;
            keywordMover.microphoneRecord = micRecord;
            keywordMover.targetObject = targetObj.transform;
            keywordMover.targetRenderer = targetObj.GetComponent<Renderer>();
            keywordMover.audioSource = audioSource;
            keywordMover.sampleAudioClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Resources/jfk.wav");

            // Create EventSystem
            GameObject esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            esGo.AddComponent<InputSystemUIInputModule>();
#else
            esGo.AddComponent<StandaloneInputModule>();
#endif

            // Create Responsive Canvas UI
            GameObject canvasGo = new GameObject("WhisperDemoCanvas");
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            Font defaultFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (defaultFont == null)
                defaultFont = Resources.GetBuiltinResource<Font>("Arial.ttf");

            // Top Panel (Header + Status + Transcript)
            GameObject topPanel = CreatePanel(canvasGo.transform, "TopPanel",
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -10f), new Vector2(-24f, 165f),
                new Color(0.06f, 0.08f, 0.12f, 0.90f));

            CreateText(topPanel.transform, "TitleText",
                "WHISPER.UNITY — OFFLINE ENGLISH VOICE KEYWORD MOVER (WINDOWS & ANDROID)",
                defaultFont, 19, FontStyle.Bold, TextAnchor.MiddleLeft,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -22f), new Vector2(-140f, 28f),
                new Color(0.45f, 0.85f, 1f));

            // VAD Indicator dot in top-right
            GameObject vadBadge = CreatePanel(topPanel.transform, "VadIndicator",
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-16f, -12f), new Vector2(115f, 28f),
                new Color(0.45f, 0.45f, 0.5f));
            CreateText(vadBadge.transform, "VadLabel", "VAD SPEECH",
                defaultFont, 13, FontStyle.Bold, TextAnchor.MiddleCenter,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Color.white);
            keywordMover.vadDotImage = vadBadge.GetComponent<Image>();
            micRecord.vadIndicatorImage = keywordMover.vadDotImage;

            keywordMover.statusText = CreateText(topPanel.transform, "StatusText",
                "Status: Ready to load offline Whisper model (ggml-tiny.bin | Language: EN)...",
                defaultFont, 15, FontStyle.Normal, TextAnchor.MiddleLeft,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -52f), new Vector2(-32f, 26f),
                new Color(0.85f, 0.9f, 0.95f));

            keywordMover.transcriptText = CreateText(topPanel.transform, "TranscriptText",
                "Transcript: (Press Start Voice Record, Spacebar, or Run Offline Benchmark)",
                defaultFont, 17, FontStyle.Bold, TextAnchor.MiddleLeft,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -84f), new Vector2(-32f, 30f),
                new Color(1f, 0.92f, 0.4f));

            keywordMover.commandLogText = CreateText(topPanel.transform, "CommandLogText",
                "Detected Command: -  |  English Keywords: UP, DOWN, LEFT, RIGHT, FORWARD, BACK, JUMP, SPIN, BIGGER, SMALLER, RED, GREEN, BLUE, STOP, RESET",
                defaultFont, 14, FontStyle.Normal, TextAnchor.MiddleLeft,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -126f), new Vector2(-32f, 40f),
                new Color(0.7f, 0.95f, 0.75f));

            // Right-Side Panel: Offline Response Time Logger Panel
            GameObject responsePanel = CreatePanel(canvasGo.transform, "ResponseTimePanel",
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-12f, -10f), new Vector2(470f, 360f),
                new Color(0.05f, 0.07f, 0.11f, 0.90f));

            Text rtText = CreateText(responsePanel.transform, "ResponseTimeLogText",
                "<b>OFFLINE RESPONSE TIME LOG (LANGUAGE: EN)</b>\nModel Init (ggml-tiny.bin): <b>0 ms</b>\nRuns: 0 | Last: - | Avg: - | Min: - | Max: -",
                defaultFont, 13, FontStyle.Normal, TextAnchor.UpperLeft,
                Vector2.zero, Vector2.one, new Vector2(14f, 0f), new Vector2(-28f, -20f),
                new Color(0.9f, 0.95f, 1f));
            rtText.supportRichText = true;
            keywordMover.responseTimeLogText = rtText;

            // Bottom Panel (Controls for Mic, Sample Test, Offline Benchmark, VAD, Language, and Quick Command Buttons)
            GameObject bottomPanel = CreatePanel(canvasGo.transform, "BottomPanel",
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 10f), new Vector2(-24f, 145f),
                new Color(0.06f, 0.08f, 0.12f, 0.90f));

            // Row 1: Main Voice & Offline Test Controls
            var recBtn = CreateButton(bottomPanel.transform, "RecordButton",
                "🎙 START VOICE RECORD (Space)", defaultFont, 15,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -12f), new Vector2(280f, 52f),
                new Color(0.15f, 0.65f, 0.35f), out Text recBtnText, out Image recBtnImg);
            keywordMover.recordButton = recBtn;
            keywordMover.recordButtonText = recBtnText;
            keywordMover.recordButtonImage = recBtnImg;

            var sampleBtn = CreateButton(bottomPanel.transform, "TestSampleButton",
                "🔊 Test Sample Audio (JFK)", defaultFont, 14,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(306f, -12f), new Vector2(225f, 52f),
                new Color(0.2f, 0.45f, 0.8f), out _, out _);
            keywordMover.testSampleButton = sampleBtn;

            var benchBtn = CreateButton(bottomPanel.transform, "BenchmarkButton",
                "⏱ Run Offline Benchmark (EN)", defaultFont, 14,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(541f, -12f), new Vector2(245f, 52f),
                new Color(0.75f, 0.42f, 0.12f), out _, out _);
            keywordMover.benchmarkButton = benchBtn;

            var vadBtn = CreateButton(bottomPanel.transform, "AutoVadButton",
                "Auto-Stop Silence (VAD): ON", defaultFont, 14,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(796f, -12f), new Vector2(235f, 52f),
                new Color(0.3f, 0.35f, 0.45f), out Text vadBtnText, out _);
            keywordMover.autoVadButton = vadBtn;
            keywordMover.autoVadButtonText = vadBtnText;

            var langBtn = CreateButton(bottomPanel.transform, "LanguageButton",
                "Language: EN", defaultFont, 14,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(1041f, -12f), new Vector2(180f, 52f),
                new Color(0.45f, 0.3f, 0.6f), out Text langBtnText, out _);
            keywordMover.languageButton = langBtn;
            keywordMover.languageButtonText = langBtnText;

            // Row 2: Quick Keyword Simulator Buttons
            string[] quickCommands = { "up", "down", "left", "right", "forward", "back", "jump", "spin", "bigger", "smaller", "red", "green", "reset" };
            float startX = 16f;
            float btnW = 86f;
            float gap = 8f;
            for (int i = 0; i < quickCommands.Length; i++)
            {
                string cmd = quickCommands[i];
                var qBtn = CreateButton(bottomPanel.transform, $"CmdBtn_{cmd}",
                    cmd.ToUpperInvariant(), defaultFont, 13,
                    new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(startX + i * (btnW + gap), -78f), new Vector2(btnW, 48f),
                    new Color(0.22f, 0.27f, 0.36f), out _, out _);

                UnityEditor.Events.UnityEventTools.AddStringPersistentListener(
                    qBtn.onClick,
                    keywordMover.ExecuteKeywordCommand,
                    cmd);
            }

            // Ensure SampleScene is in EditorBuildSettings
            if (EditorBuildSettings.scenes.Length == 0)
            {
                EditorBuildSettings.scenes = new[]
                {
                    new EditorBuildSettingsScene("Assets/Scenes/SampleScene.unity", true)
                };
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[WhisperSetup] SampleScene successfully built and saved (English UI + Offline Response Time Panel)!");
        }

        private static void DestroyIfExists(string name)
        {
            var go = GameObject.Find(name);
            while (go != null)
            {
                UnityEngine.Object.DestroyImmediate(go);
                go = GameObject.Find(name);
            }
        }

        private static Material CreateOrLoadMaterial(string path, Shader shader, Color color)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", color);
                if (mat.HasProperty("_Color"))
                    mat.SetColor("_Color", color);
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                if (shader != null && mat.shader != shader)
                    mat.shader = shader;
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", color);
                if (mat.HasProperty("_Color"))
                    mat.SetColor("_Color", color);
                EditorUtility.SetDirty(mat);
            }
            return mat;
        }

        private static GameObject CreatePanel(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
            Vector2 anchoredPos, Vector2 sizeDelta, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = sizeDelta;
            Image img = go.AddComponent<Image>();
            img.color = color;
            return go;
        }

        private static Text CreateText(Transform parent, string name, string content,
            Font font, int fontSize, FontStyle style, TextAnchor align,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPos, Vector2 sizeDelta, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = sizeDelta;
            Text txt = go.AddComponent<Text>();
            txt.font = font;
            txt.text = content;
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.alignment = align;
            txt.color = color;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            return txt;
        }

        private static Button CreateButton(Transform parent, string name, string label,
            Font font, int fontSize, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 anchoredPos, Vector2 sizeDelta, Color bgColor,
            out Text labelText, out Image bgImage)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = sizeDelta;

            bgImage = go.AddComponent<Image>();
            bgImage.color = bgColor;

            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = bgImage;

            GameObject txtGo = new GameObject("Text");
            txtGo.transform.SetParent(go.transform, false);
            RectTransform txtRt = txtGo.AddComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = new Vector2(6f, 2f);
            txtRt.offsetMax = new Vector2(-6f, -2f);

            labelText = txtGo.AddComponent<Text>();
            labelText.font = font;
            labelText.text = label;
            labelText.fontSize = fontSize;
            labelText.fontStyle = FontStyle.Bold;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.color = Color.white;

            return btn;
        }
    }
}
