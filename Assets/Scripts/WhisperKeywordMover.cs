using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Whisper;
using Whisper.Utils;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace WhisperDemo
{
    /// <summary>
    /// Offline English Keyword Command controller using Macoron/whisper.unity for Windows & Android.
    /// Listens to microphone or offline test audio clips, transcribes locally with Whisper (language = "en"),
    /// records response times to UI & local CSV/TXT files, and moves/animates a target 3D object.
    /// </summary>
    public class WhisperKeywordMover : MonoBehaviour
    {
        [Serializable]
        public class ResponseTimeRecord
        {
            public int runIndex;
            public string timestamp;
            public string testType;
            public string clipName;
            public string language;
            public float audioDurationSec;
            public long responseTimeMs;
            public double realTimeFactor;
            public string transcript;
            public string matchedCommands;
        }

        [Header("Whisper Components")]
        public WhisperManager whisper;
        public MicrophoneRecord microphoneRecord;

        [Header("Target Object to Move")]
        public Transform targetObject;
        public Renderer targetRenderer;
        public float moveStep = 1.2f;
        public float moveSmoothSpeed = 8f;
        public float jumpHeight = 1.8f;
        public float spinSpeed = 180f;

        [Header("Sample Audio for Mic-Free Offline Testing")]
        public AudioClip sampleAudioClip;
        public AudioSource audioSource;

        [Header("UI References")]
        public Text statusText;
        public Text transcriptText;
        public Text commandLogText;
        public Text responseTimeLogText;
        public Text recordButtonText;
        public Button recordButton;
        public Image recordButtonImage;
        public Button testSampleButton;
        public Button benchmarkButton;
        public Button autoVadButton;
        public Text autoVadButtonText;
        public Button languageButton;
        public Text languageButtonText;
        public Image vadDotImage;

        // Movement state
        private Vector3 _initialPosition;
        private Quaternion _initialRotation;
        private Vector3 _initialScale;
        private Color _initialColor = new Color(0.2f, 0.75f, 1f);

        private Vector3 _targetPosition;
        private Vector3 _targetScale;
        private Color _targetColor;
        private bool _isSpinning;
        private float _jumpTimer = -1f;
        private const float JumpDuration = 0.65f;

        private bool _isTranscribing;
        private string _lastCommand = "-";
        private readonly string[] _languages = { "en", "auto", "id" };
        private int _langIndex = 0;

        // Response Time tracking
        private long _modelLoadTimeMs = 0;
        private readonly List<ResponseTimeRecord> _responseRecords = new List<ResponseTimeRecord>();

        public IReadOnlyList<ResponseTimeRecord> ResponseRecords => _responseRecords;
        public long ModelLoadTimeMs
        {
            get => _modelLoadTimeMs;
            set => _modelLoadTimeMs = value;
        }

        private void Awake()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Screen.orientation = ScreenOrientation.LandscapeLeft;
#endif
            if (whisper == null)
                whisper = FindFirstObjectByType<WhisperManager>();
            if (microphoneRecord == null)
                microphoneRecord = FindFirstObjectByType<MicrophoneRecord>();

            if (targetObject == null)
                targetObject = transform;

            if (targetRenderer == null && targetObject != null)
                targetRenderer = targetObject.GetComponentInChildren<Renderer>();

            if (audioSource == null)
            {
                audioSource = gameObject.GetComponent<AudioSource>();
                if (audioSource == null)
                    audioSource = gameObject.AddComponent<AudioSource>();
            }

            if (sampleAudioClip == null)
                sampleAudioClip = Resources.Load<AudioClip>("jfk");

            _initialPosition = targetObject.position;
            _initialRotation = targetObject.rotation;
            _initialScale = targetObject.localScale;
            if (targetRenderer != null && targetRenderer.sharedMaterial != null)
            {
                EnsureCompatible3DMaterial(targetRenderer.sharedMaterial);
                _initialColor = GetMaterialColor(targetRenderer.sharedMaterial, _initialColor);
            }

            _targetPosition = _initialPosition;
            _targetScale = _initialScale;
            _targetColor = _initialColor;
        }

        private async void Start()
        {
            if (whisper != null)
            {
                // Guide Whisper decoder toward expected English command keywords
                whisper.initialPrompt =
                    "Voice command keywords: up, down, left, right, forward, back, jump, spin, rotate, stop, reset, bigger, smaller, red, green, blue, yellow.";
                whisper.language = _languages[_langIndex];
            }

            if (microphoneRecord != null)
            {
                microphoneRecord.OnRecordStop += OnMicrophoneRecordStop;
                microphoneRecord.OnVadChanged += OnVadChanged;
            }

            if (recordButton != null)
                recordButton.onClick.AddListener(ToggleRecording);
            if (testSampleButton != null)
                testSampleButton.onClick.AddListener(RunSampleAudioTest);
            if (benchmarkButton != null)
                benchmarkButton.onClick.AddListener(RunOfflineBenchmarkSuite);
            if (autoVadButton != null)
                autoVadButton.onClick.AddListener(ToggleAutoVad);
            if (languageButton != null)
                languageButton.onClick.AddListener(CycleLanguage);

            UpdateUiState();
            UpdateResponseTimePanelUi();
            UpdateStatus("Loading Whisper offline model (ggml-tiny.bin)...");

            // Wait until WhisperManager finishes loading
            if (whisper != null)
            {
                var sw = Stopwatch.StartNew();
                while (whisper.IsLoading)
                {
                    await Task.Yield();
                }

                if (!whisper.IsLoaded)
                {
                    await whisper.InitModel();
                }
                sw.Stop();
                _modelLoadTimeMs = sw.ElapsedMilliseconds;

                UpdateUiState();
                UpdateResponseTimePanelUi();

                if (whisper.IsLoaded)
                {
                    RequestAndroidMicPermission();
                    string micName = GetDefaultMicName();
                    UpdateStatus($"READY ({Application.platform} | Offline EN) | Model loaded in {_modelLoadTimeMs} ms | Mic: {micName}");
                }
                else
                {
                    UpdateStatus("ERROR: Failed to load Whisper model! Check StreamingAssets/Whisper/ggml-tiny.bin");
                }
            }
        }

        private void OnDestroy()
        {
            if (microphoneRecord != null)
            {
                microphoneRecord.OnRecordStop -= OnMicrophoneRecordStop;
                microphoneRecord.OnVadChanged -= OnVadChanged;
            }
        }

        private void Update()
        {
            // Spacebar shortcut on Windows / Desktop
            bool spacePressed = false;
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                spacePressed = true;
#else
            if (Input.GetKeyDown(KeyCode.Space))
                spacePressed = true;
#endif
            if (spacePressed && !_isTranscribing)
            {
                ToggleRecording();
            }

            AnimateTargetObject();
        }

        private void AnimateTargetObject()
        {
            if (targetObject == null)
                return;

            Vector3 desiredPos = _targetPosition;

            if (_jumpTimer >= 0f)
            {
                _jumpTimer += Time.deltaTime;
                float t = Mathf.Clamp01(_jumpTimer / JumpDuration);
                float arc = Mathf.Sin(t * Mathf.PI) * jumpHeight;
                desiredPos.y += arc;

                if (t >= 1f)
                    _jumpTimer = -1f;
            }

            targetObject.position = Vector3.Lerp(targetObject.position, desiredPos, Time.deltaTime * moveSmoothSpeed);
            targetObject.localScale = Vector3.Lerp(targetObject.localScale, _targetScale, Time.deltaTime * moveSmoothSpeed);

            if (_isSpinning)
            {
                targetObject.Rotate(Vector3.up, spinSpeed * Time.deltaTime, Space.World);
                targetObject.Rotate(Vector3.right, spinSpeed * 0.35f * Time.deltaTime, Space.Self);
            }
            else
            {
                targetObject.rotation = Quaternion.Slerp(targetObject.rotation, _initialRotation, Time.deltaTime * 5f);
            }

            if (targetRenderer != null && targetRenderer.material != null)
            {
                Material mat = targetRenderer.material;
                EnsureCompatible3DMaterial(mat);
                Color currentColor = GetMaterialColor(mat, _targetColor);
                Color nextColor = Color.Lerp(currentColor, _targetColor, Time.deltaTime * moveSmoothSpeed);
                SetMaterialColor(mat, nextColor);
            }
        }

        private static void EnsureCompatible3DMaterial(Material mat)
        {
            if (mat == null)
                return;

            if (!mat.HasProperty("_BaseColor") && !mat.HasProperty("_Color"))
            {
                Shader litShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (litShader != null)
                    mat.shader = litShader;
            }
        }

        private static Color GetMaterialColor(Material mat, Color fallback)
        {
            if (mat == null)
                return fallback;
            if (mat.HasProperty("_BaseColor"))
                return mat.GetColor("_BaseColor");
            if (mat.HasProperty("_Color"))
                return mat.GetColor("_Color");
            return fallback;
        }

        private static void SetMaterialColor(Material mat, Color color)
        {
            if (mat == null)
                return;
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
        }

        private void RequestAndroidMicPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Permission.RequestUserPermission(Permission.Microphone);
            }
#endif
        }

        private string GetDefaultMicName()
        {
            try
            {
                var devices = Microphone.devices;
                if (devices != null && devices.Length > 0)
                    return devices[0];
                return "No microphone detected";
            }
            catch (Exception e)
            {
                return $"Mic error ({e.Message})";
            }
        }

        public void ToggleRecording()
        {
            if (microphoneRecord == null || _isTranscribing)
                return;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Permission.RequestUserPermission(Permission.Microphone);
                UpdateStatus("Requesting Android Microphone permission...");
                return;
            }
#endif

            if (!microphoneRecord.IsRecording)
            {
                if (Microphone.devices.Length == 0)
                {
                    UpdateStatus("No microphone detected! Use 'Test Sample Audio' or 'Run Offline Benchmark' or connect a mic.");
                    return;
                }

                microphoneRecord.StartRecord();
                UpdateStatus("Recording voice... Speak an English command (e.g., 'up', 'left', 'jump', 'spin', 'green', 'reset')!");
            }
            else
            {
                microphoneRecord.StopRecord();
            }

            UpdateUiState();
        }

        public void ToggleAutoVad()
        {
            if (microphoneRecord == null)
                return;

            microphoneRecord.vadStop = !microphoneRecord.vadStop;
            microphoneRecord.vadStopTime = 1.4f;
            UpdateUiState();
        }

        public void CycleLanguage()
        {
            _langIndex = (_langIndex + 1) % _languages.Length;
            if (whisper != null)
            {
                whisper.language = _languages[_langIndex];
            }
            UpdateUiState();
        }

        private void OnVadChanged(bool isSpeechDetected)
        {
            if (vadDotImage != null)
            {
                vadDotImage.color = isSpeechDetected
                    ? new Color(0.2f, 0.95f, 0.35f)
                    : new Color(0.45f, 0.45f, 0.5f);
            }
        }

        private async void OnMicrophoneRecordStop(AudioChunk recordedAudio)
        {
            UpdateUiState();

            if (whisper == null || !whisper.IsLoaded)
            {
                UpdateStatus("Whisper model is not ready yet!");
                return;
            }

            if (recordedAudio.Data == null || recordedAudio.Data.Length == 0 || recordedAudio.Length < 0.15f)
            {
                UpdateStatus("Recording too short (< 0.15s). Please try again.");
                return;
            }

            _isTranscribing = true;
            UpdateUiState();
            UpdateStatus($"Transcribing {recordedAudio.Length:F2}s microphone audio offline (Language: {whisper.language.ToUpperInvariant()})...");

            var sw = Stopwatch.StartNew();
            var res = await whisper.GetTextAsync(recordedAudio.Data, recordedAudio.Frequency, recordedAudio.Channels);
            sw.Stop();

            _isTranscribing = false;
            UpdateUiState();

            if (res == null || string.IsNullOrWhiteSpace(res.Result))
            {
                UpdateStatus($"Done ({sw.ElapsedMilliseconds} ms) — No speech text detected.");
                return;
            }

            ProcessTranscriptionResult(
                res.Result,
                sw.ElapsedMilliseconds,
                recordedAudio.Length,
                testType: "MicOffline",
                clipName: "Microphone",
                isSampleTest: false);
        }

        public async void RunSampleAudioTest()
        {
            if (_isTranscribing || whisper == null || !whisper.IsLoaded)
                return;

            if (sampleAudioClip == null)
                sampleAudioClip = Resources.Load<AudioClip>("jfk");

            if (sampleAudioClip == null)
            {
                UpdateStatus("Sample AudioClip (Resources/jfk.wav) not found!");
                return;
            }

            whisper.language = "en";
            _langIndex = 0;

            if (audioSource != null)
            {
                audioSource.clip = sampleAudioClip;
                audioSource.Play();
            }

            _isTranscribing = true;
            UpdateUiState();
            UpdateStatus($"Testing Whisper offline with '{sampleAudioClip.name}' ({sampleAudioClip.length:F2}s, Language: EN)...");

            var sw = Stopwatch.StartNew();
            var res = await whisper.GetTextAsync(sampleAudioClip);
            sw.Stop();

            _isTranscribing = false;
            UpdateUiState();

            if (res == null || string.IsNullOrWhiteSpace(res.Result))
            {
                UpdateStatus($"Sample test failed ({sw.ElapsedMilliseconds} ms).");
                return;
            }

            ProcessTranscriptionResult(
                res.Result,
                sw.ElapsedMilliseconds,
                sampleAudioClip.length,
                testType: "SampleOffline",
                clipName: sampleAudioClip.name,
                isSampleTest: true);
        }

        /// <summary>
        /// Runs the complete offline English benchmark suite across all pre-generated voice command clips + JFK sample.
        /// Records each response time to the UI panel and local CSV/TXT files.
        /// </summary>
        public async void RunOfflineBenchmarkSuite()
        {
            if (_isTranscribing || whisper == null || !whisper.IsLoaded)
                return;

            whisper.language = "en";
            _langIndex = 0;
            _isTranscribing = true;
            UpdateUiState();

            string[] resourcePaths =
            {
                "OfflineTestAudio/cmd_01_up_right_jump",
                "OfflineTestAudio/cmd_02_left_forward_spin",
                "OfflineTestAudio/cmd_03_bigger_green",
                "OfflineTestAudio/cmd_04_smaller_red_stop",
                "OfflineTestAudio/cmd_05_down_back_reset",
                "jfk"
            };

            int completed = 0;
            for (int i = 0; i < resourcePaths.Length; i++)
            {
                AudioClip clip = Resources.Load<AudioClip>(resourcePaths[i]);
                if (clip == null)
                    continue;

                UpdateStatus($"[Offline Benchmark {i + 1}/{resourcePaths.Length}] Transcribing '{clip.name}' ({clip.length:F2}s, EN)...");

                var sw = Stopwatch.StartNew();
                var res = await whisper.GetTextAsync(clip);
                sw.Stop();

                string text = res != null ? res.Result : "";
                bool isJfk = clip.name.IndexOf("jfk", StringComparison.OrdinalIgnoreCase) >= 0;

                ProcessTranscriptionResult(
                    text,
                    sw.ElapsedMilliseconds,
                    clip.length,
                    testType: "BenchmarkOffline",
                    clipName: clip.name,
                    isSampleTest: isJfk);

                completed++;
                await Task.Delay(150);
            }

            _isTranscribing = false;
            UpdateUiState();

            if (_responseRecords.Count > 0)
            {
                double avgMs = _responseRecords.Average(r => r.responseTimeMs);
                long minMs = _responseRecords.Min(r => r.responseTimeMs);
                long maxMs = _responseRecords.Max(r => r.responseTimeMs);
                UpdateStatus($"Offline EN Benchmark Complete ({completed} clips) | Avg: {avgMs:F1} ms | Min: {minMs} ms | Max: {maxMs} ms");
            }
        }

        /// <summary>
        /// Direct method to trigger a keyword command (can also be called from UI quick buttons).
        /// </summary>
        public void ExecuteKeywordCommand(string rawText)
        {
            ProcessTranscriptionResult(rawText, 0, 0f, "QuickButton", "UI_Button", isSampleTest: false, logToCsv: false);
        }

        /// <summary>
        /// Allows editor benchmark scripts to register measured offline runs directly into the scene UI & log history.
        /// </summary>
        public ResponseTimeRecord RecordMeasuredRun(
            string rawTranscript,
            long elapsedMs,
            float audioDurationSec,
            string testType,
            string clipName,
            string language = "en",
            bool isSampleTest = false,
            bool logToCsv = true)
        {
            return ProcessTranscriptionResult(rawTranscript, elapsedMs, audioDurationSec, testType, clipName, isSampleTest, logToCsv, language);
        }

        public void ClearResponseRecords()
        {
            _responseRecords.Clear();
            UpdateResponseTimePanelUi();
        }

        private ResponseTimeRecord ProcessTranscriptionResult(
            string rawTranscript,
            long elapsedMs,
            float audioDurationSec,
            string testType = "Offline",
            string clipName = "Audio",
            bool isSampleTest = false,
            bool logToCsv = true,
            string languageOverride = null)
        {
            string cleaned = (rawTranscript ?? string.Empty).Trim();
            if (transcriptText != null)
                transcriptText.text = $"Transcript: \"{cleaned}\"";

            UnityEngine.Debug.Log($"[WhisperKeywordMover] [{testType}] {clipName} ({audioDurationSec:F2}s) -> {elapsedMs} ms: \"{cleaned}\"");

            // Remove bracketed tags like [BLANK_AUDIO], (music), punctuation
            string normalized = Regex.Replace(cleaned.ToLowerInvariant(), @"\[.*?\]|\(.*?\)", " ");
            normalized = Regex.Replace(normalized, @"[^a-z0-9\s]", " ");

            string[] tokens = normalized.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> matchedActions = new List<string>();

            foreach (string word in tokens)
            {
                switch (word)
                {
                    case "up":
                    case "naik":
                    case "atas":
                        _targetPosition += Vector3.up * moveStep;
                        matchedActions.Add("UP (+Y)");
                        break;

                    case "down":
                    case "turun":
                    case "bawah":
                        _targetPosition += Vector3.down * moveStep;
                        matchedActions.Add("DOWN (-Y)");
                        break;

                    case "left":
                    case "kiri":
                        _targetPosition += Vector3.left * moveStep;
                        matchedActions.Add("LEFT (-X)");
                        break;

                    case "right":
                    case "kanan":
                        _targetPosition += Vector3.right * moveStep;
                        matchedActions.Add("RIGHT (+X)");
                        break;

                    case "forward":
                    case "front":
                    case "maju":
                    case "depan":
                        _targetPosition += Vector3.forward * moveStep;
                        matchedActions.Add("FORWARD (+Z)");
                        break;

                    case "back":
                    case "backward":
                    case "mundur":
                    case "belakang":
                        _targetPosition += Vector3.back * moveStep;
                        matchedActions.Add("BACK (-Z)");
                        break;

                    case "jump":
                    case "hop":
                    case "lompat":
                    case "loncat":
                        _jumpTimer = 0f;
                        matchedActions.Add("JUMP");
                        break;

                    case "spin":
                    case "rotate":
                    case "turn":
                    case "putar":
                        _isSpinning = true;
                        matchedActions.Add("SPIN ON");
                        break;

                    case "bigger":
                    case "grow":
                    case "large":
                    case "besar":
                        _targetScale *= 1.3f;
                        matchedActions.Add("SCALE UP");
                        break;

                    case "smaller":
                    case "shrink":
                    case "small":
                    case "kecil":
                        _targetScale *= 0.75f;
                        matchedActions.Add("SCALE DOWN");
                        break;

                    case "red":
                    case "merah":
                        _targetColor = new Color(0.95f, 0.25f, 0.25f);
                        matchedActions.Add("COLOR RED");
                        break;

                    case "green":
                    case "hijau":
                        _targetColor = new Color(0.2f, 0.9f, 0.35f);
                        matchedActions.Add("COLOR GREEN");
                        break;

                    case "blue":
                    case "biru":
                        _targetColor = new Color(0.2f, 0.6f, 1f);
                        matchedActions.Add("COLOR BLUE");
                        break;

                    case "yellow":
                    case "kuning":
                    case "gold":
                        _targetColor = new Color(1f, 0.85f, 0.15f);
                        matchedActions.Add("COLOR YELLOW");
                        break;

                    case "stop":
                    case "halt":
                    case "berhenti":
                    case "diam":
                        _isSpinning = false;
                        _jumpTimer = -1f;
                        if (targetObject != null)
                            _targetPosition = targetObject.position;
                        matchedActions.Add("STOP");
                        break;

                    case "reset":
                    case "center":
                    case "home":
                    case "ulang":
                    case "tengah":
                        _isSpinning = false;
                        _jumpTimer = -1f;
                        _targetPosition = _initialPosition;
                        _targetScale = _initialScale;
                        _targetColor = _initialColor;
                        matchedActions.Add("RESET");
                        break;
                }
            }

            // If running the JFK sample audio test, trigger a celebratory demo animation
            if (isSampleTest && matchedActions.Count == 0 &&
                (normalized.Contains("fellow") || normalized.Contains("country") || normalized.Contains("americans")))
            {
                _jumpTimer = 0f;
                _isSpinning = true;
                _targetColor = new Color(0.2f, 0.95f, 0.55f);
                matchedActions.Add("SAMPLE DEMO (JUMP + SPIN)");
            }

            if (matchedActions.Count > 0)
            {
                _lastCommand = string.Join(" -> ", matchedActions);
                if (commandLogText != null)
                    commandLogText.text = $"Detected Command: {_lastCommand}  |  Target Pos: ({_targetPosition.x:F1}, {_targetPosition.y:F1}, {_targetPosition.z:F1})";
                UpdateStatus($"OK ({elapsedMs} ms) | Executed: {_lastCommand}");
            }
            else
            {
                _lastCommand = "NONE";
                if (commandLogText != null)
                    commandLogText.text = "Detected Command: (No matching keyword — try saying: up, down, left, right, jump, spin, reset)";
                UpdateStatus($"Done ({elapsedMs} ms) | Transcript contains no matching command keywords.");
            }

            ResponseTimeRecord record = null;
            if (logToCsv)
            {
                string activeLang = languageOverride ?? (whisper != null ? whisper.language : "en");
                double rtf = audioDurationSec > 0.01f ? (elapsedMs / 1000.0) / audioDurationSec : 0.0;
                record = new ResponseTimeRecord
                {
                    runIndex = _responseRecords.Count + 1,
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    testType = testType,
                    clipName = clipName,
                    language = activeLang,
                    audioDurationSec = audioDurationSec,
                    responseTimeMs = elapsedMs,
                    realTimeFactor = rtf,
                    transcript = cleaned,
                    matchedCommands = _lastCommand
                };

                _responseRecords.Add(record);
                AppendResponseRecordToFiles(record);
                UpdateResponseTimePanelUi();
            }

            return record;
        }

        public static string GetOfflineLogDirectory()
        {
#if UNITY_EDITOR || UNITY_STANDALONE
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string dir = Path.Combine(projectRoot, "OfflineTestLogs");
#else
            string dir = Path.Combine(Application.persistentDataPath, "OfflineTestLogs");
#endif
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        private void AppendResponseRecordToFiles(ResponseTimeRecord record)
        {
            try
            {
                string dir = GetOfflineLogDirectory();
                string csvPath = Path.Combine(dir, "whisper_response_times.csv");
                string txtPath = Path.Combine(dir, "whisper_response_times.txt");

                bool writeHeader = !File.Exists(csvPath);
                var csvSb = new StringBuilder();
                if (writeHeader)
                {
                    csvSb.AppendLine("RunIndex,Timestamp,TestType,Language,ClipName,AudioDurationSec,ResponseTimeMs,RealTimeFactor,Transcript,MatchedCommands");
                }

                string escapedTranscript = record.transcript.Replace("\"", "\"\"");
                string escapedCommands = record.matchedCommands.Replace("\"", "\"\"");
                csvSb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},\"{1}\",\"{2}\",\"{3}\",\"{4}\",{5:F2},{6},{7:F4},\"{8}\",\"{9}\"",
                    record.runIndex,
                    record.timestamp,
                    record.testType,
                    record.language,
                    record.clipName,
                    record.audioDurationSec,
                    record.responseTimeMs,
                    record.realTimeFactor,
                    escapedTranscript,
                    escapedCommands));

                File.AppendAllText(csvPath, csvSb.ToString(), Encoding.UTF8);

                string txtLine = string.Format(
                    CultureInfo.InvariantCulture,
                    "[#{0} | {1}] Type={2} | Lang={3} | Clip={4} ({5:F2}s) | ResponseTime={6} ms (RTF={7:F3}x) | Transcript=\"{8}\" | Action={9}{10}",
                    record.runIndex,
                    record.timestamp,
                    record.testType,
                    record.language.ToUpperInvariant(),
                    record.clipName,
                    record.audioDurationSec,
                    record.responseTimeMs,
                    record.realTimeFactor,
                    record.transcript,
                    record.matchedCommands,
                    Environment.NewLine);

                File.AppendAllText(txtPath, txtLine, Encoding.UTF8);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[WhisperKeywordMover] Failed to write response time log file: {e.Message}");
            }
        }

        public void UpdateResponseTimePanelUi()
        {
            if (responseTimeLogText == null)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("<b>OFFLINE RESPONSE TIME LOG (LANGUAGE: EN)</b>");
            sb.AppendLine($"Model Init (ggml-tiny.bin): <b>{_modelLoadTimeMs} ms</b>");

            if (_responseRecords.Count == 0)
            {
                sb.AppendLine("Runs: 0 | Last: - | Avg: - | Min: - | Max: -");
                sb.AppendLine("--------------------------------------------------");
                sb.AppendLine("<i>No offline transcription recorded yet.</i>");
                sb.AppendLine("<i>Click 'Run Offline Benchmark' or 'Test Sample Audio' to record response times.</i>");
            }
            else
            {
                long lastMs = _responseRecords[_responseRecords.Count - 1].responseTimeMs;
                double avgMs = _responseRecords.Average(r => r.responseTimeMs);
                long minMs = _responseRecords.Min(r => r.responseTimeMs);
                long maxMs = _responseRecords.Max(r => r.responseTimeMs);
                double avgRtf = _responseRecords.Average(r => r.realTimeFactor);

                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Runs: <b>{0}</b> | Last: <color=#66FF99><b>{1} ms</b></color> | Avg: <color=#FFE066><b>{2:F1} ms</b></color>",
                    _responseRecords.Count, lastMs, avgMs));
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Min: <b>{0} ms</b> | Max: <b>{1} ms</b> | Avg RTF: <b>{2:F3}x</b>",
                    minMs, maxMs, avgRtf));
                sb.AppendLine("--------------------------------------------------");

                int startIdx = Mathf.Max(0, _responseRecords.Count - 6);
                for (int i = startIdx; i < _responseRecords.Count; i++)
                {
                    var r = _responseRecords[i];
                    string shortTranscript = r.transcript.Length > 34
                        ? r.transcript.Substring(0, 31) + "..."
                        : r.transcript;
                    sb.AppendLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "#{0} <b>{1}</b> ({2:F1}s) -> <color=#66FF99><b>{3} ms</b></color> ({4:F2}x)\n   \"{5}\" -> <color=#80D8FF>{6}</color>",
                        r.runIndex,
                        r.clipName,
                        r.audioDurationSec,
                        r.responseTimeMs,
                        r.realTimeFactor,
                        shortTranscript,
                        r.matchedCommands));
                }
            }

            sb.AppendLine("--------------------------------------------------");
            sb.Append("Log Saved: <i>OfflineTestLogs/whisper_response_times.csv</i>");
            responseTimeLogText.text = sb.ToString();
        }

        private void UpdateStatus(string msg)
        {
            if (statusText != null)
                statusText.text = msg;
            UnityEngine.Debug.Log($"[WhisperStatus] {msg}");
        }

        private void UpdateUiState()
        {
            bool isRec = microphoneRecord != null && microphoneRecord.IsRecording;
            bool modelReady = whisper != null && whisper.IsLoaded && !whisper.IsLoading;

            if (recordButtonText != null)
            {
                if (!modelReady)
                    recordButtonText.text = "⏳ Loading Whisper Model...";
                else if (_isTranscribing)
                    recordButtonText.text = "⏳ Transcribing Audio...";
                else if (isRec)
                    recordButtonText.text = "⏹ STOP & TRANSCRIBE (Space)";
                else
                    recordButtonText.text = "🎙 START VOICE RECORD (Space)";
            }

            if (recordButtonImage != null)
            {
                if (!modelReady || _isTranscribing)
                    recordButtonImage.color = new Color(0.55f, 0.45f, 0.15f);
                else if (isRec)
                    recordButtonImage.color = new Color(0.85f, 0.2f, 0.2f);
                else
                    recordButtonImage.color = new Color(0.15f, 0.65f, 0.35f);
            }

            if (recordButton != null)
                recordButton.interactable = modelReady && !_isTranscribing;

            if (testSampleButton != null)
                testSampleButton.interactable = modelReady && !_isTranscribing && !isRec;

            if (benchmarkButton != null)
                benchmarkButton.interactable = modelReady && !_isTranscribing && !isRec;

            if (autoVadButtonText != null && microphoneRecord != null)
            {
                autoVadButtonText.text = microphoneRecord.vadStop
                    ? "Auto-Stop Silence (VAD): ON"
                    : "Auto-Stop Silence (VAD): OFF";
            }

            if (languageButtonText != null && whisper != null)
            {
                languageButtonText.text = $"Language: {whisper.language.ToUpperInvariant()}";
            }
        }
    }
}
