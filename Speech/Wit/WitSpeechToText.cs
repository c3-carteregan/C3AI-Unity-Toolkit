using C3AI.Events;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

// WitSpeechToText.cs
// Fixes added:
// 1) Wrap safe ring buffer reads (no more AudioClip.GetData overrun across loop boundary)
// 2) Prompt mode uses sequential chunks (cursor based) instead of "last N seconds" sliding window
// 3) More robust JSON extraction (real key parsing + JSON unescape) and prefers is_final
// 4) Silence detection uses the same wrap safe reads (more reliable)
// 5) Stale callback protection via generation counters (mode changes wont apply old responses)
// 6) Reduced per tick allocations by reusing buffers where reasonable
// 7) Keeps mic open; never restarts mic in prompt loop
// 8) Clearer device selection + logging
namespace C3AI.Voice
{
    public class WitSpeechToText : MonoBehaviour, ISpeechToTextSource
    {
        [Header("Wit")]
        [Tooltip("Prototype only. DO NOT ship server token in a client app.")]
        public string witServerAccessToken = "";
        public string witApiVersion = "20230215";

        [Header("Keyword")]
        public string keyword = "test";
        public bool keywordWordBoundary = true;

        [Header("Mic")]
        [Tooltip("Requested sample rate. On Quest/Android, 48000 is often most reliable. If you set < 24000, this script will use 48000 on device.")]
        public int requestedSampleRate = 16000;
        public int rollingBufferSeconds = 20;
        [Range(0.5f, 8f)] public float inputGain = 1.0f;

        [Header("PC Microphone Selection")]
        [Tooltip("Microphone device name to use on PC. Leave empty to use default. Ignored on Android.")]
        public string pcMicrophoneDeviceName = "";

        [Header("Probe")]
        public float keywordProbeSeconds = 3.0f;
        public float keywordProbeIntervalSeconds = 1.0f;

        [Header("Command")]
        public float postKeywordPauseSeconds = 1.0f;
        public float commandMaxSeconds = 10.0f;

        [Header("Silence Stop (Command Only)")]
        public bool enableSilenceTimeout = true;
        public float silenceTimeoutSeconds = 2.0f;
        public float silenceRmsThreshold = 0.015f;

        [Header("Prompt Mode")]
        [Tooltip("If true, continuously sends fixed length clips instead of keyword detection.")]
        public bool usePromptMode = false;
        [Tooltip("Length of each clip to send in prompt mode (seconds).")]
        public float promptClipLengthSeconds = 3.0f;
        [Tooltip("Minimum RMS level to consider audio as speech in prompt mode. Clips below this threshold are skipped.")]
        public float promptSilenceThreshold = 0.01f;

        [Header("Audio Cue")]
        public AudioSource cueAudioSource;
        [SerializeField] private AudioClip _keywordDetectedAudioClip;

        [Header("Startup")]
        [Tooltip("If true, keyword listening starts automatically on awake.")]
        public bool autostart = true;

        [Header("Logging")]
        public bool verboseLogging = true;
        [Tooltip("If true, logs probe text each time (keyword logging).")]
        public bool logProbeText = true;

        private enum Mode { Probing, PausingThenCommand, WaitingForCommandEnd, Busy, PromptMode }
        private Mode _mode = Mode.Busy;

        private string _micDevice;   // null/empty means default device
        private AudioClip _micClip;

        private int _actualSampleRate;
        private int _micClipFramesPerChannel;
        private int _micClipChannels;

        private float _nextProbeTime;

        private float _commandStartTime;
        private int _commandStartFrame;
        private float _lastNonSilentTime;

        private Coroutine _pauseThenCmdRoutine;
        private bool _sendingCommand;
        private bool _isListening;

        // Prompt mode state
        private float _nextPromptClipTime;
        private bool _sendingPromptClip;
        private int _promptNextReadFrame; // sequential cursor in ring timeline

        // Stale callback protection
        private int _probeGen;
        private int _promptGen;
        private int _cmdGen;

        // Reusable buffers to reduce GC
        private float[] _tempInterleaved;    // frames * channels
        private float[] _tempMono;           // frames
        private byte[] _wavBytes;            // 44 + frames*2

        // Small reusable buffers for wrap reads
        private float[] _chunkA;
        private float[] _chunkB;

        private bool _initialized = false;

        public bool IsListening => _isListening;
        public string CurrentMicrophoneDevice => _micDevice;

        public static string[] GetAvailableMicrophones()
        {
            return Microphone.devices ?? Array.Empty<string>();
        }

        public void SetPCMicrophoneDevice(string deviceName)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Debug.LogWarning("WitSpeechToText: SetPCMicrophoneDevice has no effect on Android.");
#else
            pcMicrophoneDeviceName = deviceName;

            if (_micClip != null)
            {
                Debug.Log("WitSpeechToText: Switching microphone to '" + (deviceName ?? "<default>") + "'...");
                StartCoroutine(RestartMicrophoneCoroutine());
            }
#endif
        }

        private IEnumerator RestartMicrophoneCoroutine()
        {
            StopListeningInternal();

            // wait a frame to ensure cleanup
            yield return null;

            EnsureRollingBuffer();
            yield return StartMicrophoneCoroutine();
        }

        private void Start()
        {
            _initialized = autostart;
            if (_initialized)
            {
                SetupBuffer();
            }
        }

        private void SetupBuffer()
        {
            EnsureRollingBuffer();
            StartCoroutine(StartMicrophoneCoroutine());
        }

        private void Update()
        {
            if (_micClip == null) return;

            if (_isListening && usePromptMode && _mode == Mode.PromptMode && Time.time >= _nextPromptClipTime && !_sendingPromptClip)
            {
                _nextPromptClipTime = Time.time + Mathf.Max(0.05f, promptClipLengthSeconds);
                StartCoroutine(SendPromptClipCoroutine());
            }

            if (_isListening && !usePromptMode && _mode == Mode.Probing && Time.time >= _nextProbeTime)
            {
                _nextProbeTime = Time.time + Mathf.Max(0.05f, keywordProbeIntervalSeconds);
                StartCoroutine(ProbeKeywordCoroutine());
            }

            if (_mode == Mode.WaitingForCommandEnd)
            {
                float elapsed = Time.time - _commandStartTime;

                if (enableSilenceTimeout)
                {
                    float rms = ComputeRecentRms(0.2f);
                    if (rms >= silenceRmsThreshold)
                        _lastNonSilentTime = Time.time;

                    if (Time.time - _lastNonSilentTime >= silenceTimeoutSeconds)
                    {
                        if (verboseLogging)
                            Debug.Log("CMD stopped due to silence.");

                        BeginSendCommandIfNeeded();
                        return;
                    }
                }

                if (elapsed >= commandMaxSeconds)
                {
                    if (verboseLogging)
                        Debug.Log("CMD stopped due to max duration.");

                    BeginSendCommandIfNeeded();
                }
            }
        }

        private void OnDisable()
        {
            StopListeningInternal();
        }

        private void StopListeningInternal()
        {
            try
            {
                _isListening = false;
                _mode = Mode.Busy;

                // invalidate all in flight callbacks
                _probeGen++;
                _promptGen++;
                _cmdGen++;

                if (_micClip != null)
                {
                    if (!string.IsNullOrEmpty(_micDevice) && Microphone.IsRecording(_micDevice))
                        Microphone.End(_micDevice);
                    else if (string.IsNullOrEmpty(_micDevice) && Microphone.IsRecording(null))
                        Microphone.End(null);
                }

                _micClip = null;
            }
            catch { }
        }

        public void StartKeywordListening()
        {
            if (_isListening || !_initialized) return;

            _isListening = true;

            if (usePromptMode)
            {
                _nextPromptClipTime = Time.time + Mathf.Max(0.05f, promptClipLengthSeconds);
                _sendingPromptClip = false;

                // start prompt cursor "now" so you do not resend old audio
                _promptNextReadFrame = Microphone.GetPosition(_micDevice);

                if (_mode == Mode.Busy && _micClip != null)
                    _mode = Mode.PromptMode;

                if (verboseLogging)
                    Debug.Log("WitSpeechToText: StartListening() in Prompt Mode");
            }
            else
            {
                _nextProbeTime = Time.time + Mathf.Max(0.05f, keywordProbeIntervalSeconds);

                if (_mode == Mode.Busy && _micClip != null && !_sendingCommand)
                    _mode = Mode.Probing;

                if (verboseLogging)
                    Debug.Log("WitSpeechToText: StartListening()");
            }
        }

        public void StopKeywordListening()
        {
            if (!_isListening || !_initialized) return;

            _isListening = false;

            // invalidate in flight prompt or probe callbacks (but leave mic running)
            _probeGen++;
            _promptGen++;

            if (verboseLogging)
                Debug.Log("WitSpeechToText: StopListening()" + (usePromptMode ? " (Prompt Mode)" : ""));
        }

        private void BeginSendCommandIfNeeded()
        {
            if (_sendingCommand) return;
            _sendingCommand = true;
            _mode = Mode.Busy;
            StartCoroutine(SendCommandCoroutine());
        }

        private void EnsureRollingBuffer()
        {
            float needKeyword = keywordProbeSeconds + postKeywordPauseSeconds + commandMaxSeconds + 2f;
            float needPrompt = promptClipLengthSeconds + 2f;
            float need = Mathf.Max(needKeyword, needPrompt);

            if (rollingBufferSeconds < need)
                rollingBufferSeconds = Mathf.CeilToInt(need);
        }

        private IEnumerator StartMicrophoneCoroutine()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Debug.Log("Has RECORD_AUDIO permission: " +
                      UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone));

            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);

                float t0 = Time.time;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                {
                    if (Time.time - t0 > 5f)
                    {
                        Debug.LogError("WitSpeechToText: Microphone permission not granted.");
                        yield break;
                    }
                    yield return null;
                }
            }
#endif

            int deviceCount = (Microphone.devices == null) ? -1 : Microphone.devices.Length;
            Debug.Log("Microphone.devices.Length=" + deviceCount);
            if (Microphone.devices != null)
            {
                for (int i = 0; i < Microphone.devices.Length; i++)
                    Debug.Log("Mic device[" + i + "]=" + Microphone.devices[i]);
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            _micDevice = null;
#else
            _micDevice = string.IsNullOrEmpty(pcMicrophoneDeviceName) ? null : pcMicrophoneDeviceName;

            if (!string.IsNullOrEmpty(_micDevice))
            {
                bool found = false;
                if (Microphone.devices != null)
                {
                    foreach (string d in Microphone.devices)
                    {
                        if (d == _micDevice) { found = true; break; }
                    }
                }

                if (!found)
                {
                    Debug.LogWarning("WitSpeechToText: Specified microphone '" + _micDevice + "' not found. Falling back to default.");
                    _micDevice = null;
                }
            }
#endif

            int rate = requestedSampleRate > 0 ? requestedSampleRate : 48000;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (rate < 24000) rate = 48000;
#endif

            _micClip = Microphone.Start(_micDevice, true, rollingBufferSeconds, rate);

            float startTime = Time.time;
            while (Microphone.GetPosition(_micDevice) <= 0)
            {
                if (Time.time - startTime > 3f)
                {
                    Debug.LogError("WitSpeechToText: Microphone did not start. devices.Length=" +
                                   ((Microphone.devices == null) ? -1 : Microphone.devices.Length));
                    _micClip = null;
                    yield break;
                }
                yield return null;
            }

            _actualSampleRate = _micClip.frequency;
            _micClipFramesPerChannel = _micClip.samples;
            _micClipChannels = _micClip.channels;

            _promptNextReadFrame = Microphone.GetPosition(_micDevice);

            if (verboseLogging)
            {
                Debug.Log("Mic started: device=" + (_micDevice ?? "<default>") +
                          " requestedRate=" + rate +
                          " actualRate=" + _actualSampleRate +
                          " buffer=" + rollingBufferSeconds + "s" +
                          " channels=" + _micClipChannels);
            }

            _isListening = _initialized;

            if (usePromptMode)
            {
                _nextPromptClipTime = Time.time + Mathf.Max(0.05f, promptClipLengthSeconds);
                _sendingPromptClip = false;

                // do not send old audio on autostart
                _promptNextReadFrame = Microphone.GetPosition(_micDevice);

                _mode = Mode.PromptMode;

                if (verboseLogging)
                    Debug.Log("WitSpeechToText ready in Prompt Mode. Listening: " + _isListening);
            }
            else
            {
                _nextProbeTime = Time.time + Mathf.Max(0.05f, keywordProbeIntervalSeconds);
                _mode = Mode.Probing;

                if (verboseLogging)
                    Debug.Log("WitSpeechToText ready. Listening: " + _isListening);
            }
        }

        private IEnumerator ProbeKeywordCoroutine()
        {
            _mode = Mode.Busy;
            int gen = ++_probeGen;

            float[] probe = ReadLastSecondsMonoWrapSafe(keywordProbeSeconds);
            if (probe == null || probe.Length == 0)
            {
                _mode = Mode.Probing;
                yield break;
            }

            ApplyGainInPlace(probe, inputGain);

            byte[] wav = BuildWavPcm16MonoReusable(probe, _actualSampleRate);

            yield return PostToWit(wav, (ok, rawJson, text) =>
            {
                if (gen != _probeGen) return; // stale

                if (logProbeText)
                    Debug.Log("Probe Text: " + (text ?? "<null>"));

                if (ok && ContainsKeyword(text, keyword, keywordWordBoundary))
                {
                    if (verboseLogging)
                        Debug.Log("Keyword detected.");

                    if (_pauseThenCmdRoutine != null)
                        StopCoroutine(_pauseThenCmdRoutine);

                    _pauseThenCmdRoutine = StartCoroutine(PauseThenCommand());

                    NotifyEventListeners(SpeechToTextEventType.ON_KEYWORD_DETECTED, text);
                }
                else
                {
                    _mode = Mode.Probing;
                }
            });
        }

        private IEnumerator SendPromptClipCoroutine()
        {
            _sendingPromptClip = true;
            int gen = ++_promptGen;

            int frames = Mathf.Max(1, Mathf.RoundToInt(promptClipLengthSeconds * _actualSampleRate));

            // Wait until enough new audio exists between cursor and current mic position
            int micPos = Microphone.GetPosition(_micDevice);
            int available = DistanceInRing(_promptNextReadFrame, micPos, _micClipFramesPerChannel);
            if (available < frames)
            {
                if (verboseLogging)
                    Debug.Log("Prompt clip waiting for enough audio. NeedFrames=" + frames + " available=" + available);

                _sendingPromptClip = false;
                yield break;
            }

            float[] clip = ReadFromStartForFramesMonoWrapSafe(_promptNextReadFrame, frames);
            _promptNextReadFrame += frames;

            if (clip == null || clip.Length == 0)
            {
                _sendingPromptClip = false;
                yield break;
            }

            ApplyGainInPlace(clip, inputGain);

            float rms = ComputeRmsFromSamples(clip);
            if (rms < promptSilenceThreshold)
            {
                if (verboseLogging)
                    Debug.Log("Prompt clip skipped (silence). RMS: " + rms.ToString("F4"));

                _sendingPromptClip = false;
                yield break;
            }

            if (verboseLogging)
                Debug.Log("Sending prompt clip (" + promptClipLengthSeconds + "s, RMS: " + rms.ToString("F4") + ")...");

            byte[] wav = BuildWavPcm16MonoReusable(clip, _actualSampleRate);

            yield return PostToWit(wav, (ok, rawJson, text) =>
            {
                if (gen != _promptGen) return; // stale

                if (ok && !string.IsNullOrWhiteSpace(text))
                {
                    if (verboseLogging)
                        Debug.Log("Prompt Text: " + text);

                    try { OnFinalCMD(text); }
                    catch (Exception e) { Debug.LogError("OnFinalCMD threw: " + e); }
                }
                else if (!ok && verboseLogging)
                {
                    Debug.LogError("Prompt clip recognition failed.");
                }

                _sendingPromptClip = false;
            });
        }

        private IEnumerator PauseThenCommand()
        {
            _mode = Mode.PausingThenCommand;

            // invalidate probe and prompt callbacks, but keep mic going
            _probeGen++;
            _promptGen++;

            if (postKeywordPauseSeconds > 0f)
                yield return new WaitForSeconds(postKeywordPauseSeconds);

            _commandStartFrame = Microphone.GetPosition(_micDevice);
            _commandStartTime = Time.time;
            _lastNonSilentTime = Time.time;
            _sendingCommand = false;

            if (cueAudioSource != null && _keywordDetectedAudioClip != null)
            {
                cueAudioSource.clip = _keywordDetectedAudioClip;
                cueAudioSource.Play();
            }

            if (verboseLogging)
                Debug.Log("CMD capture started.");

            NotifyEventListeners(SpeechToTextEventType.ON_COMMAND_LISTEN_STARTED, null);

            _mode = Mode.WaitingForCommandEnd;
        }

        private IEnumerator SendCommandCoroutine()
        {
            int gen = ++_cmdGen;

            int frames = Mathf.RoundToInt((Time.time - _commandStartTime) * _actualSampleRate);
            frames = Mathf.Clamp(frames, 1, Mathf.RoundToInt(commandMaxSeconds * _actualSampleRate));

            float[] cmd = ReadFromStartForFramesMonoWrapSafe(_commandStartFrame, frames);
            if (cmd == null || cmd.Length == 0)
            {
                _sendingCommand = false;
                _mode = Mode.Probing;
                yield break;
            }

            ApplyGainInPlace(cmd, inputGain);

            byte[] wav = BuildWavPcm16MonoReusable(cmd, _actualSampleRate);

            yield return PostToWit(wav, (ok, rawJson, text) =>
            {
                if (gen != _cmdGen) return; // stale

                if (ok)
                {
                    // Debug.Log("CMD Text: " + text);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        try { OnFinalCMD(text); }
                        catch (Exception e) { Debug.LogError("OnFinalCMD threw: " + e); }
                    }
                    else
                    {
                        NotifyEventListeners(SpeechToTextEventType.ON_EMPTY_CMD_RETURNED, null);
                        if (verboseLogging)
                            Debug.Log("CMD recognition returned empty text.");

                    }
                }
                else
                {
                    if (verboseLogging)
                        Debug.LogError("CMD recognition failed.");
                }

                _sendingCommand = false;

                // If user switched to prompt mode while this was sending, respect that.
                if (usePromptMode && _isListening)
                    _mode = Mode.PromptMode;
                else
                    _mode = Mode.Probing;
            });
        }

        protected virtual void OnFinalCMD(string finalText)
        {
            Debug.Log("OnFinalCMD: " + finalText);
            NotifyEventListeners(SpeechToTextEventType.ON_SPEECH_RECOGNIZED, finalText);
        }

        private float ComputeRecentRms(float seconds)
        {
            float[] samples = ReadLastSecondsMonoWrapSafe(seconds);
            if (samples == null || samples.Length == 0) return 0f;
            return ComputeRmsFromSamples(samples);
        }

        // ----------------- AUDIO BUFFER HELPERS (WRAP SAFE) -----------------

        private float[] ReadLastSecondsMonoWrapSafe(float seconds)
        {
            if (_micClip == null) return null;

            int frames = Mathf.Max(1, Mathf.RoundToInt(seconds * _actualSampleRate));
            int end = Microphone.GetPosition(_micDevice);
            int startFrame = end - frames;
            return ReadFromStartForFramesMonoWrapSafe(startFrame, frames);
        }

        private float[] ReadFromStartForFramesMonoWrapSafe(int startFrame, int frames)
        {
            if (_micClip == null) return null;
            frames = Mathf.Max(1, frames);

            EnsureFloatCapacity(ref _tempMono, frames);
            EnsureFloatCapacity(ref _tempInterleaved, frames * _micClipChannels);

            int channels = _micClipChannels;
            int ring = _micClipFramesPerChannel;

            int start = Mod(startFrame, ring);

            // How many frames until end of ring from "start"
            int framesToEnd = ring - start;

            if (frames <= framesToEnd)
            {
                // single contiguous read
                _micClip.GetData(_tempInterleaved, start);
                InterleavedToMono(_tempInterleaved, channels, frames, _tempMono);
                return CopyOutMono(_tempMono, frames);
            }
            else
            {
                // wrap: read tail, then head
                int aFrames = framesToEnd;
                int bFrames = frames - framesToEnd;

                EnsureFloatCapacity(ref _chunkA, aFrames * channels);
                EnsureFloatCapacity(ref _chunkB, bFrames * channels);

                _micClip.GetData(_chunkA, start);
                _micClip.GetData(_chunkB, 0);

                // Convert both chunks into _tempMono in place
                int outIndex = 0;
                InterleavedToMonoInto(_chunkA, channels, aFrames, _tempMono, ref outIndex);
                InterleavedToMonoInto(_chunkB, channels, bFrames, _tempMono, ref outIndex);

                return CopyOutMono(_tempMono, frames);
            }
        }

        private static void InterleavedToMono(float[] interleaved, int channels, int frames, float[] monoOut)
        {
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                int baseIdx = i * channels;
                for (int c = 0; c < channels; c++)
                    sum += interleaved[baseIdx + c];
                monoOut[i] = sum / channels;
            }
        }

        private static void InterleavedToMonoInto(float[] interleaved, int channels, int frames, float[] monoOut, ref int outIndex)
        {
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                int baseIdx = i * channels;
                for (int c = 0; c < channels; c++)
                    sum += interleaved[baseIdx + c];
                monoOut[outIndex++] = sum / channels;
            }
        }

        private static float[] CopyOutMono(float[] src, int frames)
        {
            // Return a right sized array (still allocates).
            // If you want zero alloc, change PostToWit to accept (float[] buffer, int length).
            float[] outArr = new float[frames];
            Array.Copy(src, 0, outArr, 0, frames);
            return outArr;
        }

        private static void EnsureFloatCapacity(ref float[] arr, int needed)
        {
            if (arr == null || arr.Length < needed)
                arr = new float[needed];
        }

        private static void ApplyGainInPlace(float[] s, float g)
        {
            if (s == null) return;
            for (int i = 0; i < s.Length; i++)
                s[i] = Mathf.Clamp(s[i] * g, -1f, 1f);
        }

        private static float ComputeRmsFromSamples(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0f;

            double sum = 0;
            for (int i = 0; i < samples.Length; i++)
                sum += samples[i] * samples[i];

            return Mathf.Sqrt((float)(sum / samples.Length));
        }

        private static int Mod(int x, int m) => (x % m + m) % m;

        private static int DistanceInRing(int from, int to, int ringSize)
        {
            int f = Mod(from, ringSize);
            int t = Mod(to, ringSize);
            if (t >= f) return t - f;
            return (ringSize - f) + t;
        }

        // ----------------- WIT -----------------

        private IEnumerator PostToWit(byte[] wav, Action<bool, string, string> done)
        {
            var req = new UnityWebRequest("https://api.wit.ai/speech?v=" + witApiVersion, "POST");
            req.uploadHandler = new UploadHandlerRaw(wav);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", "Bearer " + witServerAccessToken);
            req.SetRequestHeader("Content-Type", "audio/wav");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string body = "";
                try { body = req.downloadHandler != null ? req.downloadHandler.text : ""; } catch { }

                if (verboseLogging)
                    Debug.LogError("Wit error: " + req.error + (string.IsNullOrEmpty(body) ? "" : ("\nBody: " + body)));

                done(false, body, null);
                yield break;
            }

            string raw = req.downloadHandler.text;
            string text = ExtractPreferredFinalTextOrLastText_Robust(raw);

            done(true, raw, text);
        }

        // ----------------- JSON PARSE (ROBUST ENOUGH FOR WIT) -----------------

        // Extracts the most recent "text" that belongs to an object where is_final == true if present.
        // Otherwise returns the last "text" found.
        // This does real JSON string parsing (handles escapes) and simple object key tracking.
        private static string ExtractPreferredFinalTextOrLastText_Robust(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            string lastText = null;
            string lastFinalText = null;

            int i = 0;
            int n = raw.Length;

            // Track state within the current object
            bool inObject = false;
            int objectDepth = 0;
            string currentText = null;
            bool currentIsFinal = false;

            while (i < n)
            {
                char ch = raw[i];

                if (ch == '{')
                {
                    objectDepth++;
                    inObject = true;

                    // entering a new object level: reset only when it is a "new object" at this depth
                    // Here we reset on every '{' and apply when that object closes.
                    currentText = null;
                    currentIsFinal = false;

                    i++;
                    continue;
                }

                if (ch == '}')
                {
                    if (inObject)
                    {
                        // finalize this object
                        if (!string.IsNullOrEmpty(currentText))
                        {
                            lastText = currentText;
                            if (currentIsFinal)
                                lastFinalText = currentText;
                        }
                    }

                    objectDepth--;
                    if (objectDepth <= 0)
                    {
                        objectDepth = 0;
                        inObject = false;
                    }

                    i++;
                    continue;
                }

                // parse keys inside objects only
                if (inObject && ch == '"')
                {
                    string key = ReadJsonString(raw, ref i);
                    SkipWhitespace(raw, ref i);

                    if (i < n && raw[i] == ':')
                    {
                        i++;
                        SkipWhitespace(raw, ref i);

                        if (key == "text")
                        {
                            if (i < n && raw[i] == '"')
                            {
                                string val = ReadJsonString(raw, ref i);
                                currentText = val;
                            }
                            else
                            {
                                // non string, skip value
                                SkipJsonValue(raw, ref i);
                            }
                        }
                        else if (key == "is_final")
                        {
                            bool? b = ReadJsonBool(raw, ref i);
                            if (b.HasValue)
                                currentIsFinal = b.Value;
                            else
                                SkipJsonValue(raw, ref i);
                        }
                        else
                        {
                            // ignore other keys
                            SkipJsonValue(raw, ref i);
                        }

                        continue;
                    }
                }

                i++;
            }

            return lastFinalText ?? lastText;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            int n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == ' ' || c == '\n' || c == '\r' || c == '\t') i++;
                else break;
            }
        }

        private static string ReadJsonString(string s, ref int i)
        {
            // expects s[i] == '"'
            int n = s.Length;
            i++; // skip opening quote

            // build into char buffer via StringBuilder only if needed
            // (avoid allocations when no escapes)
            int start = i;
            bool hasEscape = false;

            while (i < n)
            {
                char c = s[i];
                if (c == '\\') { hasEscape = true; i += 2; continue; }
                if (c == '"') break;
                i++;
            }

            int end = i;
            if (i < n && s[i] == '"') i++; // skip closing quote

            if (!hasEscape)
            {
                return s.Substring(start, end - start);
            }

            // unescape
            return JsonUnescape(s, start, end);
        }

        private static string JsonUnescape(string s, int start, int end)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(end - start);

            int i = start;
            while (i < end)
            {
                char c = s[i++];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= end) break;
                char e = s[i++];

                if (e == '"') sb.Append('"');
                else if (e == '\\') sb.Append('\\');
                else if (e == '/') sb.Append('/');
                else if (e == 'b') sb.Append('\b');
                else if (e == 'f') sb.Append('\f');
                else if (e == 'n') sb.Append('\n');
                else if (e == 'r') sb.Append('\r');
                else if (e == 't') sb.Append('\t');
                else if (e == 'u')
                {
                    // unicode escape \uXXXX
                    if (i + 3 < end)
                    {
                        int code = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            char h = s[i + k];
                            int v =
                                (h >= '0' && h <= '9') ? (h - '0') :
                                (h >= 'a' && h <= 'f') ? (10 + (h - 'a')) :
                                (h >= 'A' && h <= 'F') ? (10 + (h - 'A')) :
                                0;
                            code = (code << 4) | v;
                        }
                        sb.Append((char)code);
                        i += 4;
                    }
                }
                else
                {
                    // unknown escape, keep it
                    sb.Append(e);
                }
            }

            return sb.ToString();
        }

        private static bool? ReadJsonBool(string s, ref int i)
        {
            int n = s.Length;
            if (i + 3 < n && s[i] == 't' && s[i + 1] == 'r' && s[i + 2] == 'u' && s[i + 3] == 'e')
            {
                i += 4;
                return true;
            }
            if (i + 4 < n && s[i] == 'f' && s[i + 1] == 'a' && s[i + 2] == 'l' && s[i + 3] == 's' && s[i + 4] == 'e')
            {
                i += 5;
                return false;
            }
            return null;
        }

        private static void SkipJsonValue(string s, ref int i)
        {
            // Skips a JSON value starting at s[i].
            // Handles strings, numbers, true/false/null, objects, arrays.
            int n = s.Length;
            SkipWhitespace(s, ref i);
            if (i >= n) return;

            char c = s[i];

            if (c == '"')
            {
                ReadJsonString(s, ref i);
                return;
            }

            if (c == '{')
            {
                int depth = 0;
                while (i < n)
                {
                    char ch = s[i++];
                    if (ch == '"') { ReadJsonString(s, ref i); continue; }
                    if (ch == '{') depth++;
                    else if (ch == '}')
                    {
                        depth--;
                        if (depth <= 0) break;
                    }
                }
                return;
            }

            if (c == '[')
            {
                int depth = 0;
                while (i < n)
                {
                    char ch = s[i++];
                    if (ch == '"') { ReadJsonString(s, ref i); continue; }
                    if (ch == '[') depth++;
                    else if (ch == ']')
                    {
                        depth--;
                        if (depth <= 0) break;
                    }
                    else if (ch == '{')
                    {
                        // enter object
                        i--;
                        SkipJsonValue(s, ref i);
                    }
                }
                return;
            }

            // number, true, false, null, or unknown: read until delimiter
            while (i < n)
            {
                char ch = s[i];
                if (ch == ',' || ch == '}' || ch == ']' || ch == '\n' || ch == '\r' || ch == '\t' || ch == ' ')
                    break;
                i++;
            }
        }

        private static bool ContainsKeyword(string t, string k, bool boundary)
        {
            if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(k)) return false;

            t = t.ToLowerInvariant();
            k = k.ToLowerInvariant();

            int i = t.IndexOf(k, StringComparison.Ordinal);
            if (i < 0) return false;
            if (!boundary) return true;

            bool left = i == 0 || !char.IsLetterOrDigit(t[i - 1]);
            bool right = i + k.Length >= t.Length || !char.IsLetterOrDigit(t[i + k.Length]);
            return left && right;
        }

        // ----------------- WAV (REUSABLE) -----------------

        private byte[] BuildWavPcm16MonoReusable(float[] samples, int rate)
        {
            int dataBytes = samples.Length * 2;
            int totalBytes = 44 + dataBytes;

            if (_wavBytes == null || _wavBytes.Length != totalBytes)
                _wavBytes = new byte[totalBytes];

            byte[] b = _wavBytes;

            WriteAscii(b, 0, "RIFF");
            WriteInt32(b, 4, 36 + dataBytes);
            WriteAscii(b, 8, "WAVEfmt ");
            WriteInt32(b, 16, 16);
            WriteInt16(b, 20, 1);
            WriteInt16(b, 22, 1);
            WriteInt32(b, 24, rate);
            WriteInt32(b, 28, rate * 2);
            WriteInt16(b, 32, 2);
            WriteInt16(b, 34, 16);
            WriteAscii(b, 36, "data");
            WriteInt32(b, 40, dataBytes);

            int o = 44;
            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)(Mathf.Clamp(samples[i], -1f, 1f) * 32767);
                WriteInt16(b, o, s);
                o += 2;
            }

            return b;
        }

        private static void WriteAscii(byte[] b, int o, string s)
        {
            for (int i = 0; i < s.Length; i++) b[o + i] = (byte)s[i];
        }

        private static void WriteInt16(byte[] b, int o, short v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        private static void WriteInt32(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        // ----------------- EVENTS -----------------

        public void Initialize()
        {
            _initialized = true;
            SetupBuffer();
            StartKeywordListening();
        }

        public GameObject GetGameObject()
        {
            return this == null ? null : this.gameObject;
        }

        private InterfaceEventManager<SpeechToTextEventData> _eventManager =
            new InterfaceEventManager<SpeechToTextEventData>();

        public bool SubscribeToEvents(IEventListener<SpeechToTextEventData> listenerToSubscribe)
        {
            Debug.Log("Subscribing listener " + listenerToSubscribe.GetGameObject().name + " to SpeechToText events.");
            return _eventManager.AddListener(listenerToSubscribe);
        }

        public bool UnsubscribeFromEvents(IEventListener<SpeechToTextEventData> listenerToUnsubscribe)
        {
            return _eventManager.RemoveListener(listenerToUnsubscribe);
        }

        private void NotifyEventListeners(SpeechToTextEventType eventType, string text)
        {
            Debug.Log("Notifying listeners of event " + eventType + " with text: " + text);
            var eventData = new SpeechToTextEventData(eventType, this, text);
            _eventManager.RaiseEvent(eventData);
        }
    }
}
