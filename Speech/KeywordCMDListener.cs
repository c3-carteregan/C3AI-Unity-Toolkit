using C3AI.Events;
using System;
using System.Collections;
using UnityEngine;

namespace C3AI.Voice
{

    /// <summary>
    /// Handles keyword detection and command listening using any ISpeechToTextProvider.
    /// Separates the keyword/command logic from the actual speech-to-text backend.
    /// </summary>
    public class KeywordCmdListener : MonoBehaviour, IEventSource<KeywordCmdListenerEventData>
    {
        [Header("Microphone")]
        [Tooltip("Reference to MicrophoneUtilities component. If not set, will try to find or create one.")]
        public MicrophoneUtilities microphoneUtilities;

        [Header("Keyword")]
        public string keyword = "test";
        public bool keywordWordBoundary = true;
        [Tooltip("Array of additional keywords that can trigger command listening.")]
        public string[] additionalKeywords;

        [Header("Probe")]
        [Tooltip("How many seconds of audio to check for keywords.")]
        public float keywordProbeSeconds = 3.0f;
        [Tooltip("How often to probe for keywords (seconds).")]
        public float keywordProbeIntervalSeconds = 1.0f;

        [Header("Command")]
        [Tooltip("Pause after keyword detection before starting command capture.")]
        public float postKeywordPauseSeconds = 1.0f;
        [Tooltip("Maximum command recording duration.")]
        public float commandMaxSeconds = 10.0f;

        [Header("Silence Stop (Command Only)")]
        public bool enableSilenceTimeout = true;
        public float silenceTimeoutSeconds = 2.0f;
        public float silenceRmsThreshold = 0.015f;

        [Header("Continuous Mode (Skip Keyword Detection)")]
        [Tooltip("If true, continuously transcribes audio without waiting for a keyword.")]
        public bool useContinuousMode = false;
        [Tooltip("Length of each audio clip to transcribe in continuous mode (seconds).")]
        public float continuousClipLengthSeconds = 3.0f;
        [Tooltip("Minimum RMS level to consider audio as speech. Clips below this are skipped.")]
        public float continuousSilenceThreshold = 0.01f;

        [Header("Audio Cue")]
        public AudioSource cueAudioSource;
        [SerializeField] private AudioClip _keywordDetectedAudioClip;

        [Header("Startup")]
        [Tooltip("If true, listening starts automatically on initialization.")]
        public bool autostart = true;

        [Header("Logging")]
        public bool verboseLogging = true;
        [Tooltip("If true, logs probe text each time (keyword probing).")]
        public bool logProbeText = true;

        private enum Mode { Probing, PausingThenCommand, WaitingForCommandEnd, Busy, Continuous }
        private Mode _mode = Mode.Busy;

        private float _nextProbeTime;
        private float _commandStartTime;
        private int _commandStartFrame;
        private float _lastNonSilentTime;

        private Coroutine _pauseThenCmdRoutine;
        private bool _sendingCommand;
        private bool _isListening;

        // Continuous mode state
        private float _nextContinuousClipTime;
        private bool _sendingContinuousClip;
        private int _continuousNextReadFrame;

        // Stale callback protection
        private int _probeGen;
        private int _continuousGen;
        private int _cmdGen;

        private bool _initialized = false;

        private ISpeechToTextSource _sttSource;

        public bool IsListening => _isListening;
        public bool IsInitialized => _initialized;
        public bool IsReady => _initialized && microphoneUtilities != null && microphoneUtilities.IsInitialized
                               && _sttSource.IsReady;
        public string CurrentMicrophoneDevice => microphoneUtilities?.CurrentMicrophoneDevice;

        // ==================== PUBLIC API ====================

        private void Awake()
        {
            _sttSource = GetComponent<ISpeechToTextSource>();
        }

        public void Initialize()
        {
            if (_initialized) return;
            
            _initialized = true;
            SetupMicrophone();
        }

        public void StartKeywordListening()
        {
            if (_isListening || !_initialized) return;

            _isListening = true;

            if (useContinuousMode)
            {
                _nextContinuousClipTime = Time.time + Mathf.Max(0.05f, continuousClipLengthSeconds);
                _sendingContinuousClip = false;
                _continuousNextReadFrame = microphoneUtilities.GetCurrentMicPosition();

                if (_mode == Mode.Busy && microphoneUtilities.IsInitialized)
                    _mode = Mode.Continuous;

                if (verboseLogging)
                    Debug.Log("KeywordCMDListener: StartListening() in Continuous Mode");

                NotifyEventListeners(KeywordCmdListenerEventType.ON_START_KEYWORD_LISTENING);
            }
            else
            {
                _nextProbeTime = Time.time + Mathf.Max(0.05f, keywordProbeIntervalSeconds);

                if (_mode == Mode.Busy && microphoneUtilities.IsInitialized && !_sendingCommand)
                    _mode = Mode.Probing;

                if (verboseLogging)
                    Debug.Log("KeywordCMDListener: StartListening()");
            }
            
        }

        public void StopKeywordListening()
        {
            if (!_isListening || !_initialized) return;

            _isListening = false;

            // Invalidate in-flight callbacks
            _probeGen++;
            _continuousGen++;

            if (verboseLogging)
                Debug.Log("KeywordCMDListener: StopListening()" + (useContinuousMode ? " (Continuous Mode)" : ""));

            NotifyEventListeners(KeywordCmdListenerEventType.ON_END_KEYWORD_LISTENING);
        }

        public void StartCmdListening(string detectedKeyword)
        {
            if (!_initialized) return;

            if (_pauseThenCmdRoutine != null)
                StopCoroutine(_pauseThenCmdRoutine);

            _pauseThenCmdRoutine = StartCoroutine(PauseThenCommand());
        }

        /// <summary>
        /// Transcribes audio samples to text. Delegates to the configured provider.
        /// </summary>
        public IEnumerator TranscribeAudio(float[] samples, int sampleRate, Action<bool, string> callback)
        {
            if (_sttSource == null)
            {
                Debug.LogError("KeywordCMDListener: No speech-to-text provider configured!");
                callback?.Invoke(false, null);
                yield break;
            }

            yield return _sttSource.TranscribeAudio(samples, sampleRate, callback);
        }

        // ==================== UNITY LIFECYCLE ====================

        private void Start()
        {
            if (autostart)
            {
                Initialize();
            }
        }

        private void Update()
        {
            if (microphoneUtilities == null || !microphoneUtilities.IsInitialized) return;
            if (_sttSource == null || !_sttSource.IsReady) return;

            // Continuous mode
            if (_isListening && useContinuousMode && _mode == Mode.Continuous && 
                Time.time >= _nextContinuousClipTime && !_sendingContinuousClip)
            {
                _nextContinuousClipTime = Time.time + Mathf.Max(0.05f, continuousClipLengthSeconds);
                StartCoroutine(SendContinuousClipCoroutine());
            }

            // Keyword probing
            if (_isListening && !useContinuousMode && _mode == Mode.Probing && Time.time >= _nextProbeTime)
            {
                _nextProbeTime = Time.time + Mathf.Max(0.05f, keywordProbeIntervalSeconds);
                StartCoroutine(ProbeKeywordCoroutine());
            }

            // Command recording with silence detection
            if (_mode == Mode.WaitingForCommandEnd)
            {
                float elapsed = Time.time - _commandStartTime;

                if (enableSilenceTimeout)
                {
                    float rms = microphoneUtilities.ComputeRecentRms(0.2f);
                    if (rms >= silenceRmsThreshold)
                        _lastNonSilentTime = Time.time;

                    if (Time.time - _lastNonSilentTime >= silenceTimeoutSeconds)
                    {
                        if (verboseLogging)
                            Debug.Log("KeywordCMDListener: CMD stopped due to silence.");

                        BeginSendCommandIfNeeded();
                        return;
                    }
                }

                if (elapsed >= commandMaxSeconds)
                {
                    if (verboseLogging)
                        Debug.Log("KeywordCMDListener: CMD stopped due to max duration.");

                    BeginSendCommandIfNeeded();
                }
            }
        }

        private void OnDisable()
        {
            StopListeningInternal();
        }

        // ==================== PRIVATE IMPLEMENTATION ====================

        private void SetupMicrophone()
        {
            EnsureMicrophoneUtilities();
            microphoneUtilities.Initialize(OnMicrophoneInitialized);
        }

        private void EnsureMicrophoneUtilities()
        {
            if (microphoneUtilities == null)
            {
                microphoneUtilities = GetComponent<MicrophoneUtilities>();
                if (microphoneUtilities == null)
                {
                    microphoneUtilities = gameObject.AddComponent<MicrophoneUtilities>();
                }
            }
        }

        private void OnMicrophoneInitialized()
        {
            if (verboseLogging)
                Debug.Log("KeywordCMDListener: Microphone initialized.");

            _isListening = autostart;

            if (useContinuousMode)
            {
                _nextContinuousClipTime = Time.time + Mathf.Max(0.05f, continuousClipLengthSeconds);
                _sendingContinuousClip = false;
                _continuousNextReadFrame = microphoneUtilities.GetCurrentMicPosition();
                _mode = Mode.Continuous;

                if (verboseLogging)
                    Debug.Log("KeywordCMDListener ready in Continuous Mode. Listening: " + _isListening);
            }
            else
            {
                _nextProbeTime = Time.time + Mathf.Max(0.05f, keywordProbeIntervalSeconds);
                _mode = Mode.Probing;

                if (verboseLogging)
                    Debug.Log("KeywordCMDListener ready. Listening: " + _isListening);
            }
        }

        private void StopListeningInternal()
        {
            _isListening = false;
            _mode = Mode.Busy;

            _probeGen++;
            _continuousGen++;
            _cmdGen++;
        }

        private void BeginSendCommandIfNeeded()
        {
            if (_sendingCommand) return;
            _sendingCommand = true;
            _mode = Mode.Busy;
            StartCoroutine(SendCommandCoroutine());
        }

        // ==================== KEYWORD PROBING ====================

        private IEnumerator ProbeKeywordCoroutine()
        {
            _mode = Mode.Busy;
            int gen = ++_probeGen;

            float[] probe = microphoneUtilities.ReadLastSeconds(keywordProbeSeconds);
            if (probe == null || probe.Length == 0)
            {
                _mode = Mode.Probing;
                yield break;
            }

            string text = null;
            bool success = false;

            yield return _sttSource.TranscribeAudio(probe, microphoneUtilities.ActualSampleRate, 
                (ok, transcribedText) =>
                {
                    success = ok;
                    text = transcribedText;
                });

            if (gen != _probeGen) yield break; // Stale

            if (logProbeText)
                Debug.Log("KeywordCMDListener Probe Text: " + (text ?? "<null>"));

            if (success && CheckForKeyword(text, out string matchedKeyword))
            {
                if (verboseLogging)
                    Debug.Log("KeywordCMDListener: Keyword detected: " + matchedKeyword);

                StartCmdListening(matchedKeyword);
            }
            else
            {
                _mode = Mode.Probing;
            }
        }

        private bool CheckForKeyword(string text, out string matchedKeyword)
        {
            matchedKeyword = null;

            // Check primary keyword
            if (KeywordUtilities.ContainsKeyword(text, keyword, keywordWordBoundary))
            {
                matchedKeyword = keyword;
                return true;
            }

            // Check additional keywords
            if (additionalKeywords != null)
            {
                foreach (string kw in additionalKeywords)
                {
                    if (!string.IsNullOrEmpty(kw) && KeywordUtilities.ContainsKeyword(text, kw, keywordWordBoundary))
                    {
                        matchedKeyword = kw;
                        return true;
                    }
                }
            }

            return false;
        }

        // ==================== COMMAND CAPTURE ====================

        private IEnumerator PauseThenCommand()
        {
            _mode = Mode.PausingThenCommand;

            _probeGen++;
            _continuousGen++;

            if (postKeywordPauseSeconds > 0f)
                yield return new WaitForSeconds(postKeywordPauseSeconds);

            _commandStartFrame = microphoneUtilities.GetCurrentMicPosition();
            _commandStartTime = Time.time;
            _lastNonSilentTime = Time.time;
            _sendingCommand = false;

            if (cueAudioSource != null && _keywordDetectedAudioClip != null)
            {
                cueAudioSource.clip = _keywordDetectedAudioClip;
                cueAudioSource.Play();
            }

            if (verboseLogging)
                Debug.Log("KeywordCMDListener: CMD capture started.");

            NotifyEventListeners(KeywordCmdListenerEventType.ON_START_CMD_LISTENING, null);

            _mode = Mode.WaitingForCommandEnd;
        }

        private IEnumerator SendCommandCoroutine()
        {
            int gen = ++_cmdGen;

            int sampleRate = microphoneUtilities.ActualSampleRate;
            int frames = Mathf.RoundToInt((Time.time - _commandStartTime) * sampleRate);
            frames = Mathf.Clamp(frames, 1, Mathf.RoundToInt(commandMaxSeconds * sampleRate));

            float[] cmd = microphoneUtilities.ReadFromFrame(_commandStartFrame, frames);
            if (cmd == null || cmd.Length == 0)
            {
                _sendingCommand = false;
                _mode = Mode.Probing;
                yield break;
            }

            string text = null;
            bool success = false;

            yield return _sttSource.TranscribeAudio(cmd, sampleRate, 
                (ok, transcribedText) =>
                {
                    success = ok;
                    text = transcribedText;
                });

            if (gen != _cmdGen) yield break; // Stale

            if (success)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { OnFinalCMD(text); }
                    catch (Exception e) { Debug.LogError("KeywordCMDListener OnFinalCMD threw: " + e); }
                }
                else
                {
                    NotifyEventListeners(KeywordCmdListenerEventType.ON_EMPTY_CMD_HEARD, null);
                    if (verboseLogging)
                        Debug.Log("KeywordCMDListener: CMD recognition returned empty text.");
                }
            }
            else
            {
                if (verboseLogging)
                    Debug.LogError("KeywordCMDListener: CMD recognition failed.");
            }

            _sendingCommand = false;

            if (useContinuousMode && _isListening)
                _mode = Mode.Continuous;
            else
                _mode = Mode.Probing;
        }

        // ==================== CONTINUOUS MODE ====================

        private IEnumerator SendContinuousClipCoroutine()
        {
            _sendingContinuousClip = true;
            int gen = ++_continuousGen;

            int sampleRate = microphoneUtilities.ActualSampleRate;
            int frames = Mathf.Max(1, Mathf.RoundToInt(continuousClipLengthSeconds * sampleRate));

            int micPos = microphoneUtilities.GetCurrentMicPosition();
            int available = microphoneUtilities.GetDistanceInRing(_continuousNextReadFrame, micPos);
            if (available < frames)
            {
                if (verboseLogging)
                    Debug.Log("KeywordCMDListener: Continuous clip waiting for enough audio.");

                _sendingContinuousClip = false;
                yield break;
            }

            float[] clip = microphoneUtilities.ReadFromFrame(_continuousNextReadFrame, frames);
            _continuousNextReadFrame += frames;

            if (clip == null || clip.Length == 0)
            {
                _sendingContinuousClip = false;
                yield break;
            }

            float rms = MicrophoneUtilities.ComputeRmsFromSamples(clip);
            if (rms < continuousSilenceThreshold)
            {
                if (verboseLogging)
                    Debug.Log("KeywordCMDListener: Continuous clip skipped (silence). RMS: " + rms.ToString("F4"));

                _sendingContinuousClip = false;
                yield break;
            }

            if (verboseLogging)
                Debug.Log($"KeywordCMDListener: Sending continuous clip ({continuousClipLengthSeconds}s, RMS: {rms:F4})...");

            string text = null;
            bool success = false;

            yield return _sttSource.TranscribeAudio(clip, sampleRate, 
                (ok, transcribedText) =>
                {
                    success = ok;
                    text = transcribedText;
                });

            if (gen != _continuousGen) yield break; // Stale

            if (success && !string.IsNullOrWhiteSpace(text))
            {
                if (verboseLogging)
                    Debug.Log("KeywordCMDListener Continuous Text: " + text);

                try { OnFinalCMD(text); }
                catch (Exception e) { Debug.LogError("KeywordCMDListener OnFinalCMD threw: " + e); }
            }
            else if (!success && verboseLogging)
            {
                Debug.LogError("KeywordCMDListener: Continuous clip recognition failed.");
            }

            _sendingContinuousClip = false;
        }

        // ==================== CALLBACKS ====================

        protected virtual void OnFinalCMD(string finalText)
        {
            Debug.Log("KeywordCMDListener OnFinalCMD: " + finalText);
            NotifyEventListeners(KeywordCmdListenerEventType.ON_CMD_HEARD, finalText);
        }

        // ==================== EVENTS ====================

        public GameObject GetGameObject()
        {
            return this == null ? null : this.gameObject;
        }

        private InterfaceEventManager<KeywordCmdListenerEventData> _keywordCmdEventManager = new InterfaceEventManager<KeywordCmdListenerEventData>();

     

        private void NotifyEventListeners(KeywordCmdListenerEventType eventType, string message = null)
        {
            if (verboseLogging)
                Debug.Log("KeywordCMDListener: Event " + eventType);
          
            var eventData = new KeywordCmdListenerEventData(eventType,this,_sttSource,message);
            _keywordCmdEventManager.RaiseEvent(eventData);
        }

        public bool SubscribeToEvents(IEventListener<KeywordCmdListenerEventData> listenerToSubscribe)
        {
           return _keywordCmdEventManager.AddListener(listenerToSubscribe);
        }

        public bool UnsubscribeFromEvents(IEventListener<KeywordCmdListenerEventData> listenerToUnsubscribe)
        {
            return _keywordCmdEventManager.RemoveListener(listenerToUnsubscribe);
        }
    }

    public enum KeywordCmdListenerEventType
    {
        ON_START_KEYWORD_LISTENING,
        ON_END_KEYWORD_LISTENING,
        ON_KEYWORD_HEARD,
        ON_LISTENING_ERROR,
        ON_START_CMD_LISTENING,
        ON_STOP_CMD_LISTENING,
        ON_CMD_HEARD,
        ON_EMPTY_CMD_HEARD,
    }
    public class KeywordCmdListenerEventData : IEventData
    {
        public KeywordCmdListenerEventType EventType;
        public KeywordCmdListener Listener;
        public ISpeechToTextSource SttSource;
        public String Message;
        public KeywordCmdListenerEventData(KeywordCmdListenerEventType eventType, KeywordCmdListener listener, ISpeechToTextSource sttSource, string message = null)
        {
            EventType = eventType;
            Listener = listener;
            SttSource = sttSource;
            Message = message;
        }
    }
}
