using C3AI.Events;
using UnityEngine;

namespace C3AI.Voice
{
	public interface ISpeechToTextSource : IEventSource<SpeechToTextEventData>
	{
		void Initialize();
        void StartKeywordListening();
        void StopKeywordListening();
    }
    public enum SpeechToTextEventType
    {
        ON_KEYWORD_DETECTED,
        ON_SPEECH_RECOGNIZED,
    }
    public class SpeechToTextEventData : IEventData
    {
        public readonly SpeechToTextEventType EventType;
        public readonly string Text;
        public readonly ISpeechToTextSource Source;
        public SpeechToTextEventData(SpeechToTextEventType eventType, ISpeechToTextSource source, string text)
        {
            EventType = eventType;
            Source = source;
            Text = text;
        }
    }
}
