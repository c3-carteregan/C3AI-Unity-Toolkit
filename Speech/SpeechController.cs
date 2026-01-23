
using C3AI.Events;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace C3AI.Voice
{
    public class SpeechController : MonoBehaviour, IEventListener<SpeechToTextEventData>, IEventSource<SpeechToTextEventData>, IEventListener<TextToSpeechEventData>
    {
        private ITextToSpeechSource _ttsSource;
        private ISpeechToTextSource _sttSource;

        [SerializeField] private Button _micButton;
        [SerializeField] private TextMeshProUGUI _micButtonText;

        private string _defaultMicButtonText;

        private void Awake()
        {

            _ttsSource = GetComponent<ITextToSpeechSource>();
            _sttSource = GetComponent<ISpeechToTextSource>();
            if (_sttSource == null)
            {
                Debug.LogWarning("No ISpeechToTextSource implementation found on SpeechController GameObject.");
            }
            else
            {
              //  _sttSource.Initialize();
                _sttSource.SubscribeToEvents(this);
            }
            if (_ttsSource == null)
            {
                Debug.LogWarning("No ITextToSpeechSource implementation found on SpeechController GameObject.");
            }
            else
            {
                _ttsSource.SubscribeToEvents(this);
            }

            if(_micButton != null)
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
            _sttSource.StartCmdListening("Button click");
        }
        public void Initialize()
            {
            _sttSource.Initialize();
        }

        public void BeginKeywordListening()
        {
            EnableMicButton();
            _sttSource.StartKeywordListening();
        }
        public void StopKeywordListening()
        {
            DisableMicButton("Please wait...");
            _sttSource.StopKeywordListening();
        }

        public void SpeakText(string textToSpeak)
        {
            Debug.Log("Attempt to say " + textToSpeak);
            _ttsSource.SpeakText(textToSpeak);
        }

        public GameObject GetGameObject()
        {
            return this == null ? null : this.gameObject;
        }

        private void EnableMicButton()
        {
            _micButtonText.text = _defaultMicButtonText;
            _micButton.interactable = true;
        }
        private void DisableMicButton(string message)
        {
            _micButtonText.text = message;
            _micButton.interactable = false;
        }

        public void OnEventOccurred(SpeechToTextEventData eventData)
        {
            switch (eventData.EventType)
            {
                case SpeechToTextEventType.ON_KEYWORD_DETECTED:
                    break;
                case SpeechToTextEventType.ON_SPEECH_RECOGNIZED:
                    DisableMicButton("Waiting for response...");
                    NotifySTTEventListeners(eventData.EventType, eventData.Text);
                    break;
                case SpeechToTextEventType.ON_COMMAND_LISTEN_STARTED:
                    DisableMicButton("Listening for prompt...");
                    break;
                case SpeechToTextEventType.ON_EMPTY_CMD_RETURNED:
                  EnableMicButton();
                    break;
                default:
                    break;
            }
        }
        private InterfaceEventManager<SpeechToTextEventData> _sttEventManager =
            new InterfaceEventManager<SpeechToTextEventData>();
        public bool SubscribeToEvents(IEventListener<SpeechToTextEventData> listenerToSubscribe)
        {
            return _sttEventManager.AddListener(listenerToSubscribe);
        }

        public bool UnsubscribeFromEvents(IEventListener<SpeechToTextEventData> listenerToUnsubscribe)
        {
            return _sttEventManager.RemoveListener(listenerToUnsubscribe);
        }

        private void NotifySTTEventListeners(SpeechToTextEventType eventType, string text)
        {
            var eventData = new SpeechToTextEventData(eventType, _sttSource, text);
            _sttEventManager.RaiseEvent(eventData);
        }

        public void OnEventOccurred(TextToSpeechEventData eventData)
        {
            switch (eventData.EventType)
            {
                case TextToSpeechEventType.ON_SPEECH_STARTED:
                    StopKeywordListening();
                    break;
                case TextToSpeechEventType.ON_SPEECH_FINISHED:
                    BeginKeywordListening();
                    break;
                default:
                    break;
            }
        }
    }
}
