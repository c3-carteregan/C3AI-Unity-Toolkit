using System;
using System.Collections;
using UnityEngine;

namespace C3AI.Voice
{
    /// <summary>
    /// Utility class for microphone recording operations.
    /// Handles microphone initialization, looping recordings, and silence detection.
    /// </summary>
    public class MicrophoneUtilities : MonoBehaviour
    {
        [Header("Mic Settings")]
        [Tooltip("Requested sample rate. On Quest/Android, 48000 is often most reliable.")]
        public int requestedSampleRate = 16000;
        
        [Tooltip("Rolling buffer length in seconds.")]
        public int rollingBufferSeconds = 20;
        
        [Range(0.5f, 8f)]
        public float inputGain = 1.0f;

        [Header("PC Microphone Selection")]
        [Tooltip("Microphone device name to use on PC. Leave empty to use default. Ignored on Android.")]
        public string pcMicrophoneDeviceName = "";

        [Header("Silence Detection Defaults")]
        [Tooltip("Default RMS threshold below which audio is considered silence.")]
        public float defaultSilenceRmsThreshold = 0.015f;
        
        [Tooltip("Default duration of silence required to trigger callback (seconds).")]
        public float defaultSilenceDurationSeconds = 2.0f;

        [Header("Logging")]
        public bool verboseLogging = true;

        // Microphone state
        private string _micDevice;
        private AudioClip _micClip;
        private int _actualSampleRate;
        private int _micClipFramesPerChannel;
        private int _micClipChannels;
        private bool _micInitialized;

        // Loop state
        private bool _loopRunning;
        private Coroutine _loopCoroutine;
        private float _loopSeconds;
        private Action<float[]> _loopCallback;
        private int _loopNextReadFrame;

        // Silence detection state
        private bool _silenceListening;
        private Coroutine _silenceCoroutine;

        // Reusable buffers to reduce GC
        private float[] _tempInterleaved;
        private float[] _tempMono;
        private float[] _chunkA;
        private float[] _chunkB;

        public bool IsInitialized => _micInitialized;
        public bool IsLoopRunning => _loopRunning;
        public bool IsSilenceListening => _silenceListening;
        public string CurrentMicrophoneDevice => _micDevice;
        public int ActualSampleRate => _actualSampleRate;

        /// <summary>
        /// Gets the list of available microphone devices.
        /// </summary>
        public static string[] GetAvailableMicrophones()
        {
            return Microphone.devices ?? Array.Empty<string>();
        }

        /// <summary>
        /// Sets the PC microphone device to use. Has no effect on Android.
        /// If the microphone is already running, it will be restarted with the new device.
        /// </summary>
        public void SetPCMicrophoneDevice(string deviceName)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Debug.LogWarning("MicrophoneUtilities: SetPCMicrophoneDevice has no effect on Android.");
#else
            pcMicrophoneDeviceName = deviceName;

            if (_micClip != null)
            {
                if (verboseLogging)
                    Debug.Log("MicrophoneUtilities: Switching microphone to '" + (deviceName ?? "<default>") + "'...");
                StartCoroutine(RestartMicrophoneCoroutine());
            }
#endif
        }

        /// <summary>
        /// Initializes the microphone. Must be called before using recording methods.
        /// </summary>
        public void Initialize(Action onComplete = null)
        {
            if (_micInitialized)
            {
                onComplete?.Invoke();
                return;
            }

            EnsureRollingBuffer();
            StartCoroutine(InitializeMicrophoneCoroutine(onComplete));
        }

        /// <summary>
        /// Stops all microphone operations and releases resources.
        /// </summary>
        public void Shutdown()
        {
            StopMicrophoneLoop();
            StopListenUntilSilence();
            StopMicrophoneInternal();
        }

        /// <summary>
        /// Starts a looping microphone recording. Records for the specified duration,
        /// then calls the callback with the recorded audio samples (mono, normalized).
        /// Repeats until StopMicrophoneLoop() is called.
        /// </summary>
        /// <param name="seconds">Duration of each recording loop in seconds.</param>
        /// <param name="callback">Called with the recorded audio samples after each loop.</param>
        public void StartMicrophoneLoop(float seconds, Action<float[]> callback)
        {
            if (!_micInitialized)
            {
                Debug.LogError("MicrophoneUtilities: Cannot start loop - microphone not initialized. Call Initialize() first.");
                return;
            }

            if (_loopRunning)
            {
                if (verboseLogging)
                    Debug.Log("MicrophoneUtilities: Stopping existing loop before starting new one.");
                StopMicrophoneLoop();
            }

            _loopSeconds = Mathf.Max(0.1f, seconds);
            _loopCallback = callback;
            _loopRunning = true;
            _loopNextReadFrame = Microphone.GetPosition(_micDevice);

            _loopCoroutine = StartCoroutine(MicrophoneLoopCoroutine());

            if (verboseLogging)
                Debug.Log($"MicrophoneUtilities: Started microphone loop ({_loopSeconds}s intervals)");
        }

        /// <summary>
        /// Stops the microphone recording loop.
        /// </summary>
        public void StopMicrophoneLoop()
        {
            if (!_loopRunning) return;

            _loopRunning = false;
            _loopCallback = null;

            if (_loopCoroutine != null)
            {
                StopCoroutine(_loopCoroutine);
                _loopCoroutine = null;
            }

            if (verboseLogging)
                Debug.Log("MicrophoneUtilities: Stopped microphone loop");
        }

        /// <summary>
        /// Listens to the microphone until silence is detected or max time is reached.
        /// Calls the callback with the recorded audio samples.
        /// </summary>
        /// <param name="maxTime">Maximum recording duration in seconds.</param>
        /// <param name="callback">Called with the recorded audio samples when complete.</param>
        public void ListenUntilSilence(float maxTime, Action<float[]> callback)
        {
            ListenUntilSilence(maxTime, defaultSilenceRmsThreshold, defaultSilenceDurationSeconds, callback);
        }

        /// <summary>
        /// Listens to the microphone until silence is detected or max time is reached.
        /// Calls the callback with the recorded audio samples.
        /// </summary>
        /// <param name="maxTime">Maximum recording duration in seconds.</param>
        /// <param name="silenceThreshold">RMS threshold below which audio is considered silence.</param>
        /// <param name="silenceDuration">Duration of continuous silence required to stop recording.</param>
        /// <param name="callback">Called with the recorded audio samples when complete.</param>
        public void ListenUntilSilence(float maxTime, float silenceThreshold, float silenceDuration, Action<float[]> callback)
        {
            if (!_micInitialized)
            {
                Debug.LogError("MicrophoneUtilities: Cannot listen - microphone not initialized. Call Initialize() first.");
                return;
            }

            if (_silenceListening)
            {
                if (verboseLogging)
                    Debug.Log("MicrophoneUtilities: Stopping existing silence listener before starting new one.");
                StopListenUntilSilence();
            }

            _silenceListening = true;
            _silenceCoroutine = StartCoroutine(ListenUntilSilenceCoroutine(maxTime, silenceThreshold, silenceDuration, callback));

            if (verboseLogging)
                Debug.Log($"MicrophoneUtilities: Started listening until silence (max {maxTime}s, threshold {silenceThreshold}, duration {silenceDuration}s)");
        }

        /// <summary>
        /// Stops the current ListenUntilSilence operation without calling the callback.
        /// </summary>
        public void StopListenUntilSilence()
        {
            if (!_silenceListening) return;

            _silenceListening = false;

            if (_silenceCoroutine != null)
            {
                StopCoroutine(_silenceCoroutine);
                _silenceCoroutine = null;
            }

            if (verboseLogging)
                Debug.Log("MicrophoneUtilities: Stopped silence listener");
        }

        /// <summary>
        /// Reads the last N seconds of audio from the microphone buffer.
        /// </summary>
        public float[] ReadLastSeconds(float seconds)
        {
            return ReadLastSecondsMonoWrapSafe(seconds);
        }

        /// <summary>
        /// Computes the RMS (root mean square) of the last N seconds of audio.
        /// Useful for checking current audio levels.
        /// </summary>
        public float ComputeRecentRms(float seconds)
        {
            float[] samples = ReadLastSecondsMonoWrapSafe(seconds);
            if (samples == null || samples.Length == 0) return 0f;
            return ComputeRmsFromSamples(samples);
        }

        // ==================== PRIVATE IMPLEMENTATION ====================

        private void OnDisable()
        {
            Shutdown();
        }

        private void EnsureRollingBuffer()
        {
            float need = Mathf.Max(10f, rollingBufferSeconds);
            if (rollingBufferSeconds < need)
                rollingBufferSeconds = Mathf.CeilToInt(need);
        }

        private IEnumerator RestartMicrophoneCoroutine()
        {
            StopMicrophoneInternal();
            yield return null;
            EnsureRollingBuffer();
            yield return InitializeMicrophoneCoroutine(null);
        }

        private void StopMicrophoneInternal()
        {
            try
            {
                _micInitialized = false;

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

        private IEnumerator InitializeMicrophoneCoroutine(Action onComplete)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (verboseLogging)
                Debug.Log("MicrophoneUtilities: Has RECORD_AUDIO permission: " +
                          UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone));

            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);

                float t0 = Time.time;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                {
                    if (Time.time - t0 > 5f)
                    {
                        Debug.LogError("MicrophoneUtilities: Microphone permission not granted.");
                        yield break;
                    }
                    yield return null;
                }
            }
#endif

            int deviceCount = (Microphone.devices == null) ? -1 : Microphone.devices.Length;
            if (verboseLogging)
            {
                Debug.Log("MicrophoneUtilities: Microphone.devices.Length=" + deviceCount);
                if (Microphone.devices != null)
                {
                    for (int i = 0; i < Microphone.devices.Length; i++)
                        Debug.Log("MicrophoneUtilities: Mic device[" + i + "]=" + Microphone.devices[i]);
                }
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
                    Debug.LogWarning("MicrophoneUtilities: Specified microphone '" + _micDevice + "' not found. Falling back to default.");
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
                    Debug.LogError("MicrophoneUtilities: Microphone did not start. devices.Length=" +
                                   ((Microphone.devices == null) ? -1 : Microphone.devices.Length));
                    _micClip = null;
                    yield break;
                }
                yield return null;
            }

            _actualSampleRate = _micClip.frequency;
            _micClipFramesPerChannel = _micClip.samples;
            _micClipChannels = _micClip.channels;
            _micInitialized = true;

            if (verboseLogging)
            {
                Debug.Log("MicrophoneUtilities: Mic started: device=" + (_micDevice ?? "<default>") +
                          " requestedRate=" + rate +
                          " actualRate=" + _actualSampleRate +
                          " buffer=" + rollingBufferSeconds + "s" +
                          " channels=" + _micClipChannels);
            }

            onComplete?.Invoke();
        }

        private IEnumerator MicrophoneLoopCoroutine()
        {
            while (_loopRunning && _micClip != null)
            {
                int frames = Mathf.Max(1, Mathf.RoundToInt(_loopSeconds * _actualSampleRate));

                // Wait until enough new audio exists
                int micPos = Microphone.GetPosition(_micDevice);
                int available = DistanceInRing(_loopNextReadFrame, micPos, _micClipFramesPerChannel);

                if (available < frames)
                {
                    yield return null;
                    continue;
                }

                float[] samples = ReadFromStartForFramesMonoWrapSafe(_loopNextReadFrame, frames);
                _loopNextReadFrame = (_loopNextReadFrame + frames) % _micClipFramesPerChannel;

                if (samples != null && samples.Length > 0)
                {
                    ApplyGainInPlace(samples, inputGain);

                    try
                    {
                        _loopCallback?.Invoke(samples);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("MicrophoneUtilities: Loop callback threw: " + e);
                    }
                }

                yield return null;
            }
        }

        private IEnumerator ListenUntilSilenceCoroutine(float maxTime, float silenceThreshold, float silenceDuration, Action<float[]> callback)
        {
            int startFrame = Microphone.GetPosition(_micDevice);
            float startTime = Time.time;
            float lastNonSilentTime = startTime;

            while (_silenceListening && _micClip != null)
            {
                float elapsed = Time.time - startTime;

                // Check for silence
                float rms = ComputeRecentRms(0.2f);
                if (rms >= silenceThreshold)
                {
                    lastNonSilentTime = Time.time;
                }

                bool silenceTriggered = (Time.time - lastNonSilentTime) >= silenceDuration;
                bool maxTimeReached = elapsed >= maxTime;

                if (silenceTriggered || maxTimeReached)
                {
                    if (verboseLogging)
                    {
                        if (silenceTriggered)
                            Debug.Log("MicrophoneUtilities: Silence detected, stopping recording.");
                        else
                            Debug.Log("MicrophoneUtilities: Max time reached, stopping recording.");
                    }

                    // Calculate frames recorded
                    int currentFrame = Microphone.GetPosition(_micDevice);
                    int frames = DistanceInRing(startFrame, currentFrame, _micClipFramesPerChannel);
                    frames = Mathf.Clamp(frames, 1, Mathf.RoundToInt(maxTime * _actualSampleRate));

                    float[] samples = ReadFromStartForFramesMonoWrapSafe(startFrame, frames);
                    _silenceListening = false;

                    if (samples != null && samples.Length > 0)
                    {
                        ApplyGainInPlace(samples, inputGain);

                        try
                        {
                            callback?.Invoke(samples);
                        }
                        catch (Exception e)
                        {
                            Debug.LogError("MicrophoneUtilities: Silence callback threw: " + e);
                        }
                    }

                    yield break;
                }

                yield return null;
            }
        }

        // ==================== AUDIO BUFFER HELPERS (WRAP SAFE) ====================

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

        /// <summary>
        /// Computes the RMS (root mean square) of the given samples.
        /// </summary>
        public static float ComputeRmsFromSamples(float[] samples)
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

        /// <summary>
        /// Gets the current microphone position in the buffer.
        /// </summary>
        public int GetCurrentMicPosition()
        {
            if (_micClip == null) return 0;
            return Microphone.GetPosition(_micDevice);
        }

        /// <summary>
        /// Reads audio from a specific start frame for a number of frames.
        /// </summary>
        public float[] ReadFromFrame(int startFrame, int frames)
        {
            return ReadFromStartForFramesMonoWrapSafe(startFrame, frames);
        }

        /// <summary>
        /// Calculates the distance between two positions in the ring buffer.
        /// </summary>
        public int GetDistanceInRing(int from, int to)
        {
            if (_micClip == null) return 0;
            return DistanceInRing(from, to, _micClipFramesPerChannel);
        }
    }
}
