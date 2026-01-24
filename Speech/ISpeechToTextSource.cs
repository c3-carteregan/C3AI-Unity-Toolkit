using C3AI.Events;
using System;
using System.Collections;
using UnityEngine;

namespace C3AI.Voice
{
    /// <summary>
    /// Interface for speech-to-text sources.
    /// Can be implemented as a pure transcription provider (just TranscribeAudio)
    /// or as a full keyword/command listener (all methods).
    /// </summary>
    public interface ISpeechToTextSource : IEventSource<SpeechToTextEventData>
    {
        /// <summary>
        /// Initialize the speech-to-text source.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Whether the source is ready to accept requests.
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// Transcribes audio samples to text.
        /// </summary>
        /// <param name="samples">Mono audio samples (normalized -1 to 1).</param>
        /// <param name="sampleRate">Sample rate of the audio.</param>
        /// <param name="callback">Called with (success, transcribedText) when complete.</param>
        /// <returns>Coroutine enumerator for yielding.</returns>
        IEnumerator TranscribeAudio(float[] samples, int sampleRate, Action<bool, string> callback);
    }

    public enum SpeechToTextEventType
    {
        ON_SPEECH_CONVERTED,
        ON_SPEECH_CONVERSION_ERROR
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
