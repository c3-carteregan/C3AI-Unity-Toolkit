using C3AI.Events;
using C3AI.Voice;
using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

// AndroidTTS.cs
// Uses Android's built-in TextToSpeech via AndroidJavaObject.
// Drop on a GameObject, call Speak("hello").
// Requires an Android TTS engine installed/enabled on the device.

namespace C3AI.Voice
{


    public class AndroidTTS : MonoBehaviour, ITextToSpeechSource
    {
        [Header("Settings")]
        [Tooltip("If true, logs extra details.")]
        public bool verboseLogs = true;

        [Tooltip("Language tag, e.g. en-US, en-GB, de-DE. Leave empty to use device default.")]
        public string languageTag = "en-US";

        [Tooltip("Speech rate. 1.0 is normal.")]
        [Range(0.1f, 3f)] public float speechRate = 1.0f;

        [Tooltip("Pitch. 1.0 is normal.")]
        [Range(0.1f, 2f)] public float pitch = 1.0f;

        private bool _ready;
        private bool _initAttempted;

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject _tts;
    private AndroidJavaObject _activity;
#endif

        public bool IsReady => _ready;

        private void Awake()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Request mic is NOT needed for TTS; no special permissions required.
        InitIfNeeded();
#endif
        }

        private void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
        Shutdown();
#endif
        }

        public void InitIfNeeded()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_ready || _initAttempted) return;
        _initAttempted = true;

        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            {
                _activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            }

            if (_activity == null)
            {
                LogE("AndroidTTS: currentActivity is null.");
                return;
            }

            // Create TTS on UI thread
            _activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                try
                {
                    var ttsClass = new AndroidJavaClass("android.speech.tts.TextToSpeech");

                    // Listener proxy
                    var listener = new OnInitListenerProxy(status =>
                    {
                        // TextToSpeech.SUCCESS == 0
                        if (status == 0)
                        {
                            _ready = true;
                            Log("AndroidTTS: OnInit SUCCESS");

                            try
                            {
                                _tts.Call<int>("setSpeechRate", speechRate);
                                _tts.Call<int>("setPitch", pitch);

                                if (!string.IsNullOrWhiteSpace(languageTag))
                                {
                                    // Locale.forLanguageTag requires API 21+, which is fine for most headsets
                                    var localeClass = new AndroidJavaClass("java.util.Locale");
                                    var locale = localeClass.CallStatic<AndroidJavaObject>("forLanguageTag", languageTag);

                                    int langResult = _tts.Call<int>("setLanguage", locale);
                                    // LANG_MISSING_DATA = -1, LANG_NOT_SUPPORTED = -2
                                    if (langResult < 0)
                                    {
                                        LogW("AndroidTTS: setLanguage failed (missing data or not supported). Using device default. code=" + langResult);
                                    }
                                    else
                                    {
                                        Log("AndroidTTS: Language set to " + languageTag + " result=" + langResult);
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                LogW("AndroidTTS: Post-init config error: " + e.Message);
                            }
                        }
                        else
                        {
                            _ready = false;
                            LogE("AndroidTTS: OnInit FAILED. status=" + status + " (device may not have a TTS engine)");
                        }
                    });

                    // new TextToSpeech(context, listener)
                    _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", _activity, listener);

                    Log("AndroidTTS: TextToSpeech constructor called");
                }
                catch (Exception e)
                {
                    _ready = false;
                    LogE("AndroidTTS: UI-thread init exception: " + e.Message);
                }
            }));
        }
        catch (Exception e)
        {
            _ready = false;
            LogE("AndroidTTS: Init exception: " + e.Message);
        }
#endif
        }

        public void SpeakText(string text)
        {
            Speak(text, true);
        }

        public void Speak(string text, bool flushQueue = true)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!_ready)
        {
            LogW("AndroidTTS: Speak called before ready. Attempting init.");
            InitIfNeeded();
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            LogW("AndroidTTS: Speak ignored (empty text).");
            return;
        }

        try
        {
            _activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                try
                {
                    // Queue modes: QUEUE_FLUSH = 0, QUEUE_ADD = 1
                    int queueMode = flushQueue ? 0 : 1;

                    // speak(String text, int queueMode, Bundle params, String utteranceId) API 21+
                    AndroidJavaObject bundle = null;

                    // Give a unique utterance id
                    string utteranceId = "utt_" + Guid.NewGuid().ToString("N");

                    int result = _tts.Call<int>("speak", text, queueMode, bundle, utteranceId);
                    if (result != 0)
                    {
                        // TextToSpeech.ERROR == -1 in older APIs, but speak() returns SUCCESS=0 or ERROR=-1 commonly.
                        LogW("AndroidTTS: speak() returned non-zero: " + result);
                    }
                    else
                    {
                        Log("AndroidTTS: Speaking: " + text);
                    }
                }
                catch (Exception e)
                {
                    LogE("AndroidTTS: speak exception: " + e.Message);
                }
            }));
        }
        catch (Exception e)
        {
            LogE("AndroidTTS: Speak wrapper exception: " + e.Message);
        }
#else
            Debug.Log("AndroidTTS: Speak called (non-Android build). Text: " + text);
#endif
        }

        public void Stop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_tts == null) return;

        try
        {
            _activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                try { _tts.Call("stop"); }
                catch (Exception e) { LogW("AndroidTTS: stop exception: " + e.Message); }
            }));
        }
        catch (Exception e)
        {
            LogW("AndroidTTS: Stop wrapper exception: " + e.Message);
        }
#endif
        }

        public void Shutdown()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_tts == null) return;

        try
        {
            _activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                try
                {
                    _tts.Call("stop");
                    _tts.Call("shutdown");
                }
                catch (Exception e)
                {
                    LogW("AndroidTTS: shutdown exception: " + e.Message);
                }
            }));
        }
        catch (Exception e)
        {
            LogW("AndroidTTS: Shutdown wrapper exception: " + e.Message);
        }

        _tts = null;
        _ready = false;
        _initAttempted = false;
#endif
        }

        private void Log(string msg)
        {
            if (verboseLogs) Debug.Log(msg);
        }

        private void LogW(string msg)
        {
            Debug.LogWarning(msg);
        }

        private void LogE(string msg)
        {
            Debug.LogError(msg);
        }

        public GameObject GetGameObject()
        {
            throw new NotImplementedException();
        }

        public bool SubscribeToEvents(IEventListener<TextToSpeechEventData> listenerToSubscribe)
        {
            throw new NotImplementedException();
        }

        public bool UnsubscribeFromEvents(IEventListener<TextToSpeechEventData> listenerToUnsubscribe)
        {
            throw new NotImplementedException();
        }



#if UNITY_ANDROID && !UNITY_EDITOR
    // Proxy to implement android.speech.tts.TextToSpeech$OnInitListener in C#
    private class OnInitListenerProxy : AndroidJavaProxy
    {
        private readonly Action<int> _onInit;

        public OnInitListenerProxy(Action<int> onInit)
            : base("android.speech.tts.TextToSpeech$OnInitListener")
        {
            _onInit = onInit;
        }

        // Called by Java
        public void onInit(int status)
        {
            _onInit?.Invoke(status);
        }
    }
#endif
    }
}
