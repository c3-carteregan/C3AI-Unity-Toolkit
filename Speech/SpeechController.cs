using C3AI.Events;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace C3AI.Voice
{
    public enum SpeechControllerEventType
    {
        ON_TTS_START,
        ON_TTS_END,
        ON_VOICE_CMD_HEARD,
    }
    public class SpeechControllerEventData : IEventData
    {
        public readonly SpeechControllerEventType EventType;
        public readonly SpeechController Source;
        public readonly string Message;
        public SpeechControllerEventData(SpeechControllerEventType eventType, SpeechController source, string message = null)
        {
            EventType = eventType;
            Source = source;
            Message = message;
        }
    }
    public class SpeechController : MonoBehaviour, IEventSource<SpeechControllerEventData>, IEventListener<KeywordCmdListenerEventData>, IEventListener<TextToSpeechEventData>
    {
        private ITextToSpeechSource _ttsSource;
        private KeywordCmdListener _keywordListener;

        [SerializeField] private Button _micButton;
        [SerializeField] private TextMeshProUGUI _micButtonText;

        private string _defaultMicButtonText;

        private void Awake()
        {
            _ttsSource = GetComponent<ITextToSpeechSource>();

            if (_ttsSource == null)
            {
                Debug.LogWarning("No ITextToSpeechSource implementation found on SpeechController GameObject.");
            }
            else
            {
                _ttsSource.SubscribeToEvents(this);
            }
            _keywordListener = GetComponent<KeywordCmdListener>();
            if (_keywordListener == null)
            {
                Debug.LogWarning("Speech Controller requires a Keyword Listener");
               
            }
            else
            {
                _keywordListener.SubscribeToEvents(this);
            }
                

            if (_micButton != null)
            {
                _micButton.onClick.AddListener(OnMicButtonClicked);
            }
            if (_micButtonText != null)
            {
                _defaultMicButtonText = _micButtonText.text;
            }
        }
        private void OnMicButtonClicked()
        {
            if (_keywordListener == null)
            {
                Debug.LogWarning("Speech Controller cannot begin keyword listening without a Keyword Listener");
                return;
            }
            _keywordListener?.StartCmdListening("Button click");
        }
        public void Initialize()
        {
            _keywordListener?.Initialize();
        }

        public void BeginKeywordListening()
        {
            if(_keywordListener == null)
            {
                Debug.LogWarning("Speech Controller cannot begin keyword listening without a Keyword Listener");
                return;
            }
            EnableMicButton();
            _keywordListener.StartKeywordListening();
        }
        public void StopKeywordListening(string userMessage = "Please wait...")
        {
            if (_keywordListener == null)
            {
                Debug.LogWarning("Speech Controller cannot stop keyword listening without a Keyword Listener");
                return;
            }
            DisableMicButton(userMessage);
            _keywordListener.StopKeywordListening();
        }

        public void SpeakText(string textToSpeak)
        {
            if(_ttsSource == null)
            {
                Debug.LogWarning("No ITextToSpeechSource implementation found on SpeechController GameObject.");
                return;
            }
            _ttsSource.SpeakText(textToSpeak);
        }

        public GameObject GetGameObject()
        {
            return this == null ? null : this.gameObject;
        }

        private void EnableMicButton()
        {
            if(_micButton == null)
            {
                Debug.LogWarning("No mic button assigned to Speech Controller.");
                return;
            }
            _micButtonText.text = _defaultMicButtonText;
            _micButton.interactable = true;
        }
        private void DisableMicButton(string message)
        {
            if (_micButton == null)
            {
                Debug.LogWarning("No mic button assigned to Speech Controller.");
                return;
            }
            _micButtonText.text = message;
            _micButton.interactable = false;
        }

        public void OnEventOccurred(TextToSpeechEventData eventData)
        {
            switch (eventData.EventType)
            {
                case TextToSpeechEventType.ON_SPEECH_STARTED:
                    StopKeywordListening();
                    NotifyEventListeners(SpeechControllerEventType.ON_TTS_START, eventData.Text);
                    break;
                case TextToSpeechEventType.ON_SPEECH_FINISHED:
                    BeginKeywordListening();
                    NotifyEventListeners(SpeechControllerEventType.ON_TTS_END, eventData.Text);
                    break;
                default:
                    break;
            }
        }

        public void OnEventOccurred(KeywordCmdListenerEventData eventData)
        {
            switch (eventData.EventType)
            {
                case KeywordCmdListenerEventType.ON_START_KEYWORD_LISTENING:
                    break;
                case KeywordCmdListenerEventType.ON_END_KEYWORD_LISTENING:
                    break;
                case KeywordCmdListenerEventType.ON_KEYWORD_HEARD:
                    break;
                case KeywordCmdListenerEventType.ON_LISTENING_ERROR:
                    break;
                case KeywordCmdListenerEventType.ON_START_CMD_LISTENING:
                    Debug.Log("Speech controller heard keyword);");
                    DisableMicButton("Listening for prompt...");
                    break;
                case KeywordCmdListenerEventType.ON_STOP_CMD_LISTENING:
                    break;
                case KeywordCmdListenerEventType.ON_CMD_HEARD:
                    // DisableMicButton("Waiting for response...");
                    EnableMicButton();
                    NotifyEventListeners(SpeechControllerEventType.ON_VOICE_CMD_HEARD, eventData.Message);
                    break;
                case KeywordCmdListenerEventType.ON_EMPTY_CMD_HEARD:
                    EnableMicButton();
                    break;
                default:
                    break;
            }
        }
        private InterfaceEventManager<SpeechControllerEventData> _eventManager = new InterfaceEventManager<SpeechControllerEventData>();
        public bool SubscribeToEvents(IEventListener<SpeechControllerEventData> listenerToSubscribe)
        {
            return _eventManager.AddListener(listenerToSubscribe);
        }

        public bool UnsubscribeFromEvents(IEventListener<SpeechControllerEventData> listenerToUnsubscribe)
        {
            return _eventManager.RemoveListener(listenerToUnsubscribe);
        }

        private void NotifyEventListeners(SpeechControllerEventType eventType, string message = null)
        {
            var eventData = new SpeechControllerEventData(eventType, this, message);
            _eventManager.RaiseEvent(eventData);
        }
    }
}
