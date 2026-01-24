using C3AI.Events;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace C3AI.Voice
{
    /// <summary>
    /// Wit.ai speech-to-text provider. Converts audio to text using the Wit.ai API.
    /// Use with KeywordCMDListener for keyword detection and command listening.
    /// This is a transcription-only provider.
    /// </summary>
    public class WitSpeechToText : MonoBehaviour, ISpeechToTextSource
    {
        [Header("Wit Configuration")]
        [Tooltip("Prototype only. DO NOT ship server token in a client app.")]
        public string witServerAccessToken = "";
        public string witApiVersion = "20230215";

        [Header("Logging")]
        public bool verboseLogging = true;

        private bool _initialized = false;

        public bool IsReady => _initialized && !string.IsNullOrEmpty(witServerAccessToken);

        // ==================== INITIALIZATION ====================

        public void Initialize()
        {
            _initialized = true;

            if (string.IsNullOrEmpty(witServerAccessToken))
            {
                Debug.LogWarning("WitSpeechToText: No server access token configured!");
            }
            else if (verboseLogging)
            {
                Debug.Log("WitSpeechToText: Initialized.");
            }
        }

        private void Awake()
        {
            Initialize();
        }

        // ==================== TRANSCRIPTION ====================

        /// <summary>
        /// Transcribes audio samples to text using Wit.ai.
        /// </summary>
        public IEnumerator TranscribeAudio(float[] samples, int sampleRate, Action<bool, string> callback)
        {
            if (samples == null || samples.Length == 0)
            {
                callback?.Invoke(false, null);
                yield break;
            }

            if (string.IsNullOrEmpty(witServerAccessToken))
            {
                Debug.LogError("WitSpeechToText: No server access token configured!");
                callback?.Invoke(false, null);
                yield break;
            }

            byte[] wav = AudioUtilities.BuildWavPcm16Mono(samples, sampleRate);

            yield return PostToWit(wav, (success, rawJson, text) =>
            {
                callback?.Invoke(success, text);
            });
        }

        /// <summary>
        /// Transcribes WAV audio bytes to text using Wit.ai.
        /// </summary>
        public IEnumerator TranscribeWav(byte[] wavBytes, Action<bool, string> callback)
        {
            if (wavBytes == null || wavBytes.Length == 0)
            {
                callback?.Invoke(false, null);
                yield break;
            }

            if (string.IsNullOrEmpty(witServerAccessToken))
            {
                Debug.LogError("WitSpeechToText: No server access token configured!");
                callback?.Invoke(false, null);
                yield break;
            }

            yield return PostToWit(wavBytes, (success, rawJson, text) =>
            {
                callback?.Invoke(success, text);
            });
        }

        // ==================== WIT API ====================

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
                    Debug.LogError("WitSpeechToText error: " + req.error + 
                        (string.IsNullOrEmpty(body) ? "" : ("\nBody: " + body)));

                done(false, body, null);
                yield break;
            }

            string raw = req.downloadHandler.text;
            string text = ExtractPreferredFinalTextOrLastText(raw);

            if (verboseLogging && !string.IsNullOrEmpty(text))
                Debug.Log("WitSpeechToText transcribed: " + text);

            done(true, raw, text);
        }

        // ==================== WIT JSON PARSING ====================

        /// <summary>
        /// Extracts the best text from Wit's streaming response format.
        /// Prefers text from objects where is_final == true.
        /// </summary>
        private static string ExtractPreferredFinalTextOrLastText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            string lastText = null;
            string lastFinalText = null;

            int i = 0;
            int n = raw.Length;

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
                    currentText = null;
                    currentIsFinal = false;
                    i++;
                    continue;
                }

                if (ch == '}')
                {
                    if (inObject)
                    {
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

                if (inObject && ch == '"')
                {
                    string key = JsonUtilities.ReadString(raw, ref i);
                    JsonUtilities.SkipWhitespace(raw, ref i);

                    if (i < n && raw[i] == ':')
                    {
                        i++;
                        JsonUtilities.SkipWhitespace(raw, ref i);

                        if (key == "text")
                        {
                            if (i < n && raw[i] == '"')
                                currentText = JsonUtilities.ReadString(raw, ref i);
                            else
                                JsonUtilities.SkipValue(raw, ref i);
                        }
                        else if (key == "is_final")
                        {
                            bool? b = JsonUtilities.ReadBool(raw, ref i);
                            if (b.HasValue)
                                currentIsFinal = b.Value;
                            else
                                JsonUtilities.SkipValue(raw, ref i);
                        }
                        else
                        {
                            JsonUtilities.SkipValue(raw, ref i);
                        }

                        continue;
                    }
                }

                i++;
            }

            return lastFinalText ?? lastText;
        }

        // ==================== EVENTS (MINIMAL) ====================

        public GameObject GetGameObject()
        {
            return this == null ? null : this.gameObject;
        }

        // Pure providers don't raise events - KeywordCMDListener handles that
        public bool SubscribeToEvents(IEventListener<SpeechToTextEventData> listenerToSubscribe) => false;
        public bool UnsubscribeFromEvents(IEventListener<SpeechToTextEventData> listenerToUnsubscribe) => false;
    }
}
