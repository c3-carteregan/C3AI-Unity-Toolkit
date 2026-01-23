using C3AI.Events;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

// WitSpeechUploader.cs
// Keyword probe -> pause -> command capture with optional silence timeout.
// Prefers is_final transcript, otherwise last text.
// Calls OnFinalCMD(finalText) exactly once per command session.
// Logs Keyword detection + probe text (keyword logging is back).
// Uses coroutine-based mic start (fixes Quest main-thread busy-wait issues).
// Uses default mic device on Android/Quest for reliability.
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

        [Header("Audio Cue")]
        public AudioSource cueAudioSource;
        [SerializeField] private AudioClip _keywordDetectedAudioClip;

        [Header("Startup")]
        [Tooltip("If true, keyword listening starts automatically on awake.")]
        public bool autostart = true;

        [Header("Logging")]
        [Tooltip("If false, only CMD mode logs + final CMD text are logged (probe text logging still happens if enabled below).")]
        public bool verboseLogging = true;

        [Tooltip("If true, logs probe text each time (keyword logging).")]
        public bool logProbeText = true;

        private enum Mode { Probing, PausingThenCommand, WaitingForCommandEnd, Busy }
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
        private bool _sendingCommand; // guard against double-send
        private bool _isListening;

        /// <summary>
        /// Returns true if the keyword listening loop is currently active.
        /// </summary>
        public bool IsListening => _isListening;

        /// <summary>
        /// Returns the currently selected microphone device name, or null if using default.
        /// </summary>
        public string CurrentMicrophoneDevice => _micDevice;

        /// <summary>
        /// Returns an array of available microphone device names.
        /// </summary>
        public static string[] GetAvailableMicrophones()
        {
            return Microphone.devices ?? Array.Empty<string>();
        }

        /// <summary>
        /// Sets the microphone device to use on PC. Has no effect on Android.
        /// Call this before Start() or restart the microphone after calling.
        /// </summary>
        /// <param name="deviceName">The device name, or null/empty for default.</param>
        public void SetPCMicrophoneDevice(string deviceName)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Debug.LogWarning("WitSpeechToText: SetPCMicrophoneDevice has no effect on Android.");
#else
            pcMicrophoneDeviceName = deviceName;
            
            // If mic is already running, restart it with the new device
            if (_micClip != null)
            {
                Debug.Log($"WitSpeechToText: Switching microphone to '{deviceName ?? "<default>"}'...");
                StartCoroutine(RestartMicrophoneCoroutine());
            }
#endif
        }

        private IEnumerator RestartMicrophoneCoroutine()
        {
            // Stop current microphone
            if (_micClip != null)
            {
                if (!string.IsNullOrEmpty(_micDevice) && Microphone.IsRecording(_micDevice))
                    Microphone.End(_micDevice);
                else if (string.IsNullOrEmpty(_micDevice) && Microphone.IsRecording(null))
                    Microphone.End(null);
                
                _micClip = null;
            }

            _mode = Mode.Busy;
            
            // Wait a frame to ensure cleanup
            yield return null;
            
            // Start with new device
            yield return StartMicrophoneCoroutine();
        }

        private void Start()
        {
            EnsureRollingBuffer();
            StartCoroutine(StartMicrophoneCoroutine());
        }

        private void Update()
        {
            if (_micClip == null) return;

            if (_isListening && _mode == Mode.Probing && Time.time >= _nextProbeTime)
            {
                _nextProbeTime = Time.time + keywordProbeIntervalSeconds;
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
            try
            {
                if (!string.IsNullOrEmpty(_micDevice) && Microphone.IsRecording(_micDevice))
                    Microphone.End(_micDevice);
                else if (string.IsNullOrEmpty(_micDevice) && Microphone.IsRecording(null))
                    Microphone.End(null);
            }
            catch { }
        }

        /// <summary>
        /// Starts the keyword listening loop. The microphone will continue recording in the background.
        /// </summary>
        public void StartKeywordListening()
        {
            if (_isListening) return;

            _isListening = true;
            _nextProbeTime = Time.time + Mathf.Max(0.05f, keywordProbeIntervalSeconds);

            if (_mode == Mode.Busy && _micClip != null && !_sendingCommand)
                _mode = Mode.Probing;

            if (verboseLogging)
                Debug.Log("WitSpeechToText: StartListening()");
        }

        /// <summary>
        /// Stops the keyword listening loop. The microphone will continue recording in the background.
        /// </summary>
        public void StopKeywordListening()
        {
            if (!_isListening) return;

            _isListening = false;

            if (verboseLogging)
                Debug.Log("WitSpeechToText: StopListening()");
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
            float need = keywordProbeSeconds + postKeywordPauseSeconds + commandMaxSeconds + 2f;
            if (rollingBufferSeconds < need)
                rollingBufferSeconds = Mathf.CeilToInt(need);
        }

        private IEnumerator StartMicrophoneCoroutine()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Permission diagnostics
        Debug.Log("Has RECORD_AUDIO permission: " +
                  UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone));

        // Request if needed
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);

            float t0 = Time.time;
            while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                if (Time.time - t0 > 5f)
                {
                    Debug.LogError("WitSpeechUploader: Microphone permission not granted.");
                    yield break;
                }
                yield return null;
            }
        }
#endif

            // Device list diagnostics (helpful on Quest)
            int deviceCount = (Microphone.devices == null) ? -1 : Microphone.devices.Length;
            Debug.Log("Microphone.devices.Length=" + deviceCount);
            if (Microphone.devices != null)
            {
                for (int i = 0; i < Microphone.devices.Length; i++)
                    Debug.Log("Mic device[" + i + "]=" + Microphone.devices[i]);
            }

            // On Android/Quest, always use default mic (null). On PC, use selected device if specified.
#if UNITY_ANDROID && !UNITY_EDITOR
            _micDevice = null;
#else
            // On PC: use specified device name, or null/empty for default
            _micDevice = string.IsNullOrEmpty(pcMicrophoneDeviceName) ? null : pcMicrophoneDeviceName;
            
            // Validate that the device exists
            if (!string.IsNullOrEmpty(_micDevice))
            {
                bool deviceFound = false;
                if (Microphone.devices != null)
                {
                    foreach (string device in Microphone.devices)
                    {
                        if (device == _micDevice)
                        {
                            deviceFound = true;
                            break;
                        }
                    }
                }
                
                if (!deviceFound)
                {
                    Debug.LogWarning($"WitSpeechToText: Specified microphone '{_micDevice}' not found. Falling back to default.");
                    _micDevice = null;
                }
            }
#endif

            int rate = requestedSampleRate > 0 ? requestedSampleRate : 48000;

#if UNITY_ANDROID && !UNITY_EDITOR
        // Many Android XR devices (including Quest) behave best with 48k.
        if (rate < 24000) rate = 48000;
#endif

            _micClip = Microphone.Start(_micDevice, true, rollingBufferSeconds, rate);

            float startTime = Time.time;
            while (Microphone.GetPosition(_micDevice) <= 0)
            {
                if (Time.time - startTime > 3f)
                {
                    Debug.LogError("WitSpeechUploader: Microphone did not start. devices.Length=" +
                                   ((Microphone.devices == null) ? -1 : Microphone.devices.Length));
                    _micClip = null;
                    yield break;
                }
                yield return null; // IMPORTANT: yield to avoid Quest deadlock
            }

            _actualSampleRate = _micClip.frequency;
            _micClipFramesPerChannel = _micClip.samples;
            _micClipChannels = _micClip.channels;

            if (verboseLogging)
            {
                Debug.Log("Mic started: device=" + (_micDevice ?? "<default>") +
                          " requestedRate=" + rate +
                          " actualRate=" + _actualSampleRate +
                          " buffer=" + rollingBufferSeconds + "s" +
                          " channels=" + _micClipChannels);
            }

            _nextProbeTime = Time.time + Mathf.Max(0.05f, keywordProbeIntervalSeconds);
            _mode = Mode.Probing;
            _isListening = autostart;

            if (verboseLogging)
                Debug.Log("WitSpeechToText ready. Listening: " + _isListening);
        }

        private IEnumerator ProbeKeywordCoroutine()
        {
            _mode = Mode.Busy;

            float[] probe = ReadLastSecondsMono(keywordProbeSeconds);
            ApplyGainInPlace(probe, inputGain);

            yield return PostToWit(BuildWavPcm16Mono(probe, _actualSampleRate), (ok, rawJson, text) =>
            {
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

        private IEnumerator PauseThenCommand()
        {
            _mode = Mode.PausingThenCommand;

            if (postKeywordPauseSeconds > 0f)
                yield return new WaitForSeconds(postKeywordPauseSeconds);

            _commandStartFrame = Microphone.GetPosition(_micDevice);
            _commandStartTime = Time.time;
            _lastNonSilentTime = Time.time;
            _sendingCommand = false;

            if (cueAudioSource != null)
            {
                cueAudioSource.clip = _keywordDetectedAudioClip;
                cueAudioSource.Play();
            }
                

            if (verboseLogging)
                Debug.Log("CMD capture started.");

            _mode = Mode.WaitingForCommandEnd;
        }

        private IEnumerator SendCommandCoroutine()
        {
            int frames = Mathf.RoundToInt((Time.time - _commandStartTime) * _actualSampleRate);
            frames = Mathf.Clamp(frames, 1, Mathf.RoundToInt(commandMaxSeconds * _actualSampleRate));

            float[] cmd = ReadFromStartForFramesMono(_commandStartFrame, frames);
            ApplyGainInPlace(cmd, inputGain);

            yield return PostToWit(BuildWavPcm16Mono(cmd, _actualSampleRate), (ok, rawJson, text) =>
            {
                if (ok)
                {
                    Debug.Log("CMD Text: " + text);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        try { OnFinalCMD(text); }
                        catch (Exception e) { Debug.LogError("OnFinalCMD threw: " + e); }
                    }
                }
                else
                {
                    if (verboseLogging)
                        Debug.LogError("CMD recognition failed.");
                }

                _sendingCommand = false;
                _mode = Mode.Probing;
            });
        }

        // Override/subscribe in your own code by adding a component that derives from this,
        // or change this to an event if you prefer.
        protected virtual void OnFinalCMD(string finalText)
        {
            // Intentionally empty. Override in a subclass or modify to invoke an event.
            Debug.Log("OnFinalCMD: " + finalText);
            NotifyEventListeners(SpeechToTextEventType.ON_SPEECH_RECOGNIZED, finalText);
           // FindObjectOfType<RamblrChat>().SendChatMessage(finalText);
        }

        private float ComputeRecentRms(float seconds)
        {
            float[] samples = ReadLastSecondsMono(seconds);
            if (samples == null || samples.Length == 0) return 0f;

            double sum = 0;
            for (int i = 0; i < samples.Length; i++)
                sum += samples[i] * samples[i];

            return Mathf.Sqrt((float)(sum / samples.Length));
        }

        // ----------------- AUDIO BUFFER HELPERS -----------------

        private float[] ReadLastSecondsMono(float seconds)
        {
            int frames = Mathf.RoundToInt(seconds * _actualSampleRate);
            int end = Microphone.GetPosition(_micDevice);
            return ReadFromStartForFramesMono(end - frames, frames);
        }

        private float[] ReadFromStartForFramesMono(int startFrame, int frames)
        {
            if (_micClip == null) return null;

            int channels = _micClipChannels;
            int start = Mod(startFrame, _micClipFramesPerChannel);

            float[] mono = new float[frames];
            float[] temp = new float[frames * channels];

            _micClip.GetData(temp, start);

            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                    sum += temp[i * channels + c];
                mono[i] = sum / channels;
            }
            return mono;
        }

        private static int Mod(int x, int m) => (x % m + m) % m;

        private static void ApplyGainInPlace(float[] s, float g)
        {
            if (s == null) return;
            for (int i = 0; i < s.Length; i++)
                s[i] = Mathf.Clamp(s[i] * g, -1f, 1f);
        }

        // ----------------- WIT -----------------

        private IEnumerator PostToWit(byte[] wav, Action<bool, string, string> done)
        {
            var req = new UnityWebRequest($"https://api.wit.ai/speech?v={witApiVersion}", "POST");
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
            string text = ExtractPreferredFinalTextOrLastText(raw);

            done(true, raw, text);
        }

        // ----------------- JSON PARSE -----------------

        private static string ExtractPreferredFinalTextOrLastText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            string lastText = null;
            string lastFinal = null;

            int idx = 0;
            while ((idx = raw.IndexOf("\"text\"", idx, StringComparison.Ordinal)) >= 0)
            {
                int colon = raw.IndexOf(':', idx);
                if (colon < 0) break;

                int q1 = raw.IndexOf('"', colon + 1);
                if (q1 < 0) break;
                q1 += 1;

                int q2 = raw.IndexOf('"', q1);
                if (q2 < 0) break;

                string text = raw.Substring(q1, q2 - q1);
                lastText = text;

                int objStart = raw.LastIndexOf('{', idx);
                int objEnd = raw.IndexOf('}', idx);
                if (objStart >= 0 && objEnd > objStart)
                {
                    string obj = raw.Substring(objStart, objEnd - objStart);
                    if (obj.Contains("\"is_final\": true", StringComparison.Ordinal))
                        lastFinal = text;
                }

                idx = q2;
            }

            return lastFinal ?? lastText;
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

        // ----------------- WAV -----------------

        private static byte[] BuildWavPcm16Mono(float[] samples, int rate)
        {
            int data = samples.Length * 2;
            byte[] b = new byte[44 + data];

            WriteAscii(b, 0, "RIFF");
            WriteInt32(b, 4, 36 + data);
            WriteAscii(b, 8, "WAVEfmt ");
            WriteInt32(b, 16, 16);
            WriteInt16(b, 20, 1);
            WriteInt16(b, 22, 1);
            WriteInt32(b, 24, rate);
            WriteInt32(b, 28, rate * 2);
            WriteInt16(b, 32, 2);
            WriteInt16(b, 34, 16);
            WriteAscii(b, 36, "data");
            WriteInt32(b, 40, data);

            int o = 44;
            foreach (float f in samples)
            {
                short s = (short)(Mathf.Clamp(f, -1f, 1f) * 32767);
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

        public void Initialize()
        {
           
        }

        public GameObject GetGameObject()
        {
            return this == null ? null : this.gameObject;
        }
        private InterfaceEventManager<SpeechToTextEventData> _eventManager =
            new InterfaceEventManager<SpeechToTextEventData>();
        public bool SubscribeToEvents(IEventListener<SpeechToTextEventData> listenerToSubscribe)
        {
            Debug.Log($"Subscribing listener {listenerToSubscribe.GetGameObject().name} to SpeechToText events.______________________________________________________");
            return _eventManager.AddListener(listenerToSubscribe);
          
        }

        public bool UnsubscribeFromEvents(IEventListener<SpeechToTextEventData> listenerToUnsubscribe)
        {
            return _eventManager.RemoveListener(listenerToUnsubscribe);
            
        }
        private void NotifyEventListeners(SpeechToTextEventType eventType, string text)
        {
            Debug.Log($"Notifying listeners of event {eventType} with text: {text}");
            var eventData = new SpeechToTextEventData(eventType, this, text);
            _eventManager.RaiseEvent(eventData);    
        }
    }

}