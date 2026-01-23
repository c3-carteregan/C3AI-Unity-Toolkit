using C3AI.Events;
using UnityEngine;

namespace C3AI.Voice
{

    public interface ITextToSpeechSource : IEventSource<TextToSpeechEventData>
    {
        void SpeakText(string text);
        void Stop();
    }
    public enum TextToSpeechEventType
    {
        ON_SPEECH_STARTED,
        ON_SPEECH_FINISHED,
        ON_SPEECH_INTERUPTED,
    }
    public class TextToSpeechEventData : IEventData
    {
        public readonly TextToSpeechEventType EventType;
        public readonly string Text;
        public readonly ITextToSpeechSource Source;
        public TextToSpeechEventData(TextToSpeechEventType eventType, ITextToSpeechSource source, string text)
        {
            EventType = eventType;
            Source = source;
            Text = text;
        }
    }
}