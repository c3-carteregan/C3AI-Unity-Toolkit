using C3AI.Events;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace C3AI.Voice
{
    [RequireComponent(typeof(AudioSource))]
    public class WitTextToSpeech : MonoBehaviour, ITextToSpeechSource
    {
        /// <summary>
        /// Invoked when the AudioSource finishes playing the current speech clip.
        /// </summary>
        public event Action OnSpeechComplete;
        public enum WitVoice
        {
            Default,
            Rebecca,
            Charlie,
            Cooper,
            Vampire,
            Prospector,
            Cody,
            Remi,
            Cam,
            Connor,
            Railey,
            Cael,
            Carl,
            Rubie,
            Overconfident,
            Disaffected,
            Hollywood,
            Trendy,
            CartoonBaby,
            Surfer,
            SouthernAccent,
            KenyanAccent,
            CartoonKid,
            CartoonVillain,
            Rosie,
            Colin,
            Pirate,
            BritishButler,
            Whimsical,
            Wizard,
            CockneyAccent
        }

        [Header("Wit")]
        [Tooltip("Prototype only. DO NOT ship server token in a client app.")]
        public string witServerAccessToken = "";
        public string witApiVersion = "20240304";
        [Tooltip("Optional. Default omits voice from the request.")]
        public WitVoice voice = WitVoice.Default;

        [Header("Audio")]
        public AudioType audioType = AudioType.WAV;

        [Header("Debug")]
        public bool logErrorBody = true;

        private const string SynthesizeEndpoint = "https://api.wit.ai/synthesize";
        private AudioSource _audioSource;
        private Coroutine _speakRoutine;

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
        }

        public void SpeakText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            Stop();
            _speakRoutine = StartCoroutine(SpeakRoutine(text));
        }

        public void Stop()
        {
            if (_speakRoutine != null)
            {
                StopCoroutine(_speakRoutine);
                _speakRoutine = null;
            }

            if (_audioSource != null)
                _audioSource.Stop();
        }

        private IEnumerator SpeakRoutine(string text)
        {
            if (string.IsNullOrWhiteSpace(witServerAccessToken))
            {
                Debug.LogError("WitTextToSpeech: Missing server access token.");
                yield break;
            }

         

            string url = $"{SynthesizeEndpoint}?v={witApiVersion}";
            string jsonBody = BuildJsonBody(text);
            byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(jsonBody);

            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyBytes);
                req.downloadHandler = new DownloadHandlerAudioClip(url, audioType);
                req.SetRequestHeader("Authorization", "Bearer " + witServerAccessToken);
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", GetAcceptHeader(audioType));

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("WitTextToSpeech: " + req.error);
                    if (logErrorBody)
                        yield return RequestErrorDetails(url, jsonBody);
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null)
                {
                    Debug.LogError("WitTextToSpeech: Failed to decode audio clip.");
                    yield break;
                }

                Debug.Log("Play speech clip.");
                _audioSource.clip = clip;
                _audioSource.Play();
                NotifyEventListeners(TextToSpeechEventType.ON_SPEECH_STARTED, text);
            }

            // Wait for the audio to finish playing
            while (_audioSource != null && _audioSource.isPlaying)
            {
                yield return null;
            }

            _speakRoutine = null;
            OnSpeechComplete?.Invoke();
            NotifyEventListeners(TextToSpeechEventType.ON_SPEECH_FINISHED, text);
        }

        private string BuildJsonBody(string text)
        {
            string voiceName = GetVoiceName(voice);

            // Escape text for JSON
            string escapedText = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

            if (!string.IsNullOrWhiteSpace(voiceName))
            {
                string escapedVoice = voiceName.Replace("\\", "\\\\").Replace("\"", "\\\"");
                return $"{{\"q\":\"{escapedText}\",\"voice\":\"{escapedVoice}\"}}";
            }

            return $"{{\"q\":\"{escapedText}\"}}";
        }

        private static string GetVoiceName(WitVoice voice)
        {
            switch (voice)
            {
                case WitVoice.Rebecca: return "wit$Rebecca";
                case WitVoice.Charlie: return "wit$Charlie";
                case WitVoice.Cooper: return "wit$Cooper";
                case WitVoice.Vampire: return "wit$Vampire";
                case WitVoice.Prospector: return "wit$Prospector";
                case WitVoice.Cody: return "wit$Cody";
                case WitVoice.Remi: return "wit$Remi";
                case WitVoice.Cam: return "wit$Cam";
                case WitVoice.Connor: return "wit$Connor";
                case WitVoice.Railey: return "wit$Railey";
                case WitVoice.Cael: return "wit$Cael";
                case WitVoice.Carl: return "wit$Carl";
                case WitVoice.Rubie: return "wit$Rubie";
                case WitVoice.Overconfident: return "wit$Overconfident";
                case WitVoice.Disaffected: return "wit$Disaffected";
                case WitVoice.Hollywood: return "wit$Hollywood";
                case WitVoice.Trendy: return "wit$Trendy";
                case WitVoice.CartoonBaby: return "wit$Cartoon Baby";
                case WitVoice.Surfer: return "wit$Surfer";
                case WitVoice.SouthernAccent: return "wit$Southern Accent";
                case WitVoice.KenyanAccent: return "wit$Kenyan Accent";
                case WitVoice.CartoonKid: return "wit$Cartoon Kid";
                case WitVoice.CartoonVillain: return "wit$Cartoon Villain";
                case WitVoice.Rosie: return "wit$Rosie";
                case WitVoice.Colin: return "wit$Colin";
                case WitVoice.Pirate: return "wit$Pirate";
                case WitVoice.BritishButler: return "wit$British Butler";
                case WitVoice.Whimsical: return "wit$Whimsical";
                case WitVoice.Wizard: return "wit$Wizard";
                case WitVoice.CockneyAccent: return "wit$Cockney Accent";
                default: return null;
            }
        }

        private static string GetAcceptHeader(AudioType type)
        {
            switch (type)
            {
                case AudioType.MPEG:
                    return "audio/mpeg";
                case AudioType.OGGVORBIS:
                    return "audio/ogg";
                case AudioType.WAV:
                default:
                    return "audio/wav";
            }
        }

        private IEnumerator RequestErrorDetails(string url, string jsonBody)
        {
            byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(jsonBody);

            using (var errReq = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                errReq.uploadHandler = new UploadHandlerRaw(bodyBytes);
                errReq.downloadHandler = new DownloadHandlerBuffer();
                errReq.SetRequestHeader("Authorization", "Bearer " + witServerAccessToken);
                errReq.SetRequestHeader("Content-Type", "application/json");
                errReq.SetRequestHeader("Accept", "application/json");

                yield return errReq.SendWebRequest();

                string details = errReq.downloadHandler != null ? errReq.downloadHandler.text : "";
                if (!string.IsNullOrEmpty(details))
                {
                    Debug.LogError("WitTextToSpeech error body: " + details);
                }
            }
        }

        public GameObject GetGameObject()
        {
            return this == null ? null : this.gameObject;   
        }
        private InterfaceEventManager<TextToSpeechEventData> _ttsEventManager =
            new InterfaceEventManager<TextToSpeechEventData>();
        public bool SubscribeToEvents(IEventListener<TextToSpeechEventData> listenerToSubscribe)
        {
         return  _ttsEventManager.AddListener(listenerToSubscribe);
        }

        public bool UnsubscribeFromEvents(IEventListener<TextToSpeechEventData> listenerToUnsubscribe)
        {
           return _ttsEventManager.RemoveListener(listenerToUnsubscribe);
        }

        private void NotifyEventListeners(TextToSpeechEventType eventType, string text)
        {
            TextToSpeechEventData eventData = new TextToSpeechEventData(eventType, this, text);
            _ttsEventManager.RaiseEvent(eventData);
        }
    }

}