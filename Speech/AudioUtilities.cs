using System;
using UnityEngine;

namespace C3AI.Voice
{
    /// <summary>
    /// Static utility class for audio operations like encoding, format conversion, and analysis.
    /// </summary>
    public static class AudioUtilities
    {
        // ==================== WAV ENCODING ====================

        /// <summary>
        /// Builds a WAV file (PCM 16-bit mono) from audio samples.
        /// </summary>
        /// <param name="samples">Mono audio samples (normalized -1 to 1).</param>
        /// <param name="sampleRate">Sample rate of the audio.</param>
        /// <returns>WAV file as byte array.</returns>
        public static byte[] BuildWavPcm16Mono(float[] samples, int sampleRate)
        {
            if (samples == null || samples.Length == 0)
                throw new ArgumentException("Samples array cannot be null or empty", nameof(samples));
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive", nameof(sampleRate));

            int dataBytes = samples.Length * 2;
            int totalBytes = 44 + dataBytes;
            byte[] buffer = new byte[totalBytes];

            // RIFF header
            WriteAscii(buffer, 0, "RIFF");
            WriteInt32(buffer, 4, 36 + dataBytes);
            WriteAscii(buffer, 8, "WAVEfmt ");

            // Format chunk
            WriteInt32(buffer, 16, 16);              // Chunk size
            WriteInt16(buffer, 20, 1);               // Audio format (PCM)
            WriteInt16(buffer, 22, 1);               // Num channels (mono)
            WriteInt32(buffer, 24, sampleRate);      // Sample rate
            WriteInt32(buffer, 28, sampleRate * 2);  // Byte rate
            WriteInt16(buffer, 32, 2);               // Block align
            WriteInt16(buffer, 34, 16);              // Bits per sample

            // Data chunk
            WriteAscii(buffer, 36, "data");
            WriteInt32(buffer, 40, dataBytes);

            // Write samples
            int offset = 44;
            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)(Mathf.Clamp(samples[i], -1f, 1f) * 32767);
                WriteInt16(buffer, offset, s);
                offset += 2;
            }

            return buffer;
        }

        /// <summary>
        /// Builds a WAV file (PCM 16-bit stereo) from audio samples.
        /// </summary>
        /// <param name="samples">Interleaved stereo audio samples (normalized -1 to 1).</param>
        /// <param name="sampleRate">Sample rate of the audio.</param>
        /// <returns>WAV file as byte array.</returns>
        public static byte[] BuildWavPcm16Stereo(float[] samples, int sampleRate)
        {
            if (samples == null || samples.Length == 0)
                throw new ArgumentException("Samples array cannot be null or empty", nameof(samples));
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive", nameof(sampleRate));

            int dataBytes = samples.Length * 2;
            int totalBytes = 44 + dataBytes;
            byte[] buffer = new byte[totalBytes];

            // RIFF header
            WriteAscii(buffer, 0, "RIFF");
            WriteInt32(buffer, 4, 36 + dataBytes);
            WriteAscii(buffer, 8, "WAVEfmt ");

            // Format chunk
            WriteInt32(buffer, 16, 16);              // Chunk size
            WriteInt16(buffer, 20, 1);               // Audio format (PCM)
            WriteInt16(buffer, 22, 2);               // Num channels (stereo)
            WriteInt32(buffer, 24, sampleRate);      // Sample rate
            WriteInt32(buffer, 28, sampleRate * 4);  // Byte rate (sampleRate * channels * bytesPerSample)
            WriteInt16(buffer, 32, 4);               // Block align (channels * bytesPerSample)
            WriteInt16(buffer, 34, 16);              // Bits per sample

            // Data chunk
            WriteAscii(buffer, 36, "data");
            WriteInt32(buffer, 40, dataBytes);

            // Write samples
            int offset = 44;
            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)(Mathf.Clamp(samples[i], -1f, 1f) * 32767);
                WriteInt16(buffer, offset, s);
                offset += 2;
            }

            return buffer;
        }

        // ==================== AUDIO ANALYSIS ====================

        /// <summary>
        /// Computes the RMS (root mean square) of audio samples.
        /// Useful for measuring audio loudness/energy.
        /// </summary>
        /// <param name="samples">Audio samples.</param>
        /// <returns>RMS value (0 to 1 for normalized audio).</returns>
        public static float ComputeRms(float[] samples)
        {
            if (samples == null || samples.Length == 0) 
                return 0f;

            double sum = 0;
            for (int i = 0; i < samples.Length; i++)
                sum += samples[i] * samples[i];

            return Mathf.Sqrt((float)(sum / samples.Length));
        }

        /// <summary>
        /// Computes the peak amplitude of audio samples.
        /// </summary>
        /// <param name="samples">Audio samples.</param>
        /// <returns>Maximum absolute sample value.</returns>
        public static float ComputePeak(float[] samples)
        {
            if (samples == null || samples.Length == 0)
                return 0f;

            float peak = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Mathf.Abs(samples[i]);
                if (abs > peak) peak = abs;
            }

            return peak;
        }

        /// <summary>
        /// Checks if audio samples are below a silence threshold.
        /// </summary>
        /// <param name="samples">Audio samples.</param>
        /// <param name="rmsThreshold">RMS threshold below which audio is considered silent.</param>
        /// <returns>True if the audio is silent.</returns>
        public static bool IsSilent(float[] samples, float rmsThreshold = 0.01f)
        {
            return ComputeRms(samples) < rmsThreshold;
        }

        // ==================== AUDIO PROCESSING ====================

        /// <summary>
        /// Applies gain to audio samples in place.
        /// </summary>
        /// <param name="samples">Audio samples to modify.</param>
        /// <param name="gain">Gain multiplier (1.0 = no change).</param>
        public static void ApplyGain(float[] samples, float gain)
        {
            if (samples == null) return;
            for (int i = 0; i < samples.Length; i++)
                samples[i] = Mathf.Clamp(samples[i] * gain, -1f, 1f);
        }

        /// <summary>
        /// Normalizes audio samples to a target peak level.
        /// </summary>
        /// <param name="samples">Audio samples to normalize.</param>
        /// <param name="targetPeak">Target peak level (default 1.0).</param>
        public static void Normalize(float[] samples, float targetPeak = 1f)
        {
            if (samples == null || samples.Length == 0) return;

            float currentPeak = ComputePeak(samples);
            if (currentPeak <= 0f) return;

            float gain = targetPeak / currentPeak;
            ApplyGain(samples, gain);
        }

        /// <summary>
        /// Converts stereo interleaved samples to mono by averaging channels.
        /// </summary>
        /// <param name="stereoSamples">Interleaved stereo samples (L, R, L, R, ...).</param>
        /// <returns>Mono samples.</returns>
        public static float[] StereoToMono(float[] stereoSamples)
        {
            if (stereoSamples == null || stereoSamples.Length == 0)
                return Array.Empty<float>();

            int monoLength = stereoSamples.Length / 2;
            float[] mono = new float[monoLength];

            for (int i = 0; i < monoLength; i++)
            {
                mono[i] = (stereoSamples[i * 2] + stereoSamples[i * 2 + 1]) * 0.5f;
            }

            return mono;
        }

        /// <summary>
        /// Converts mono samples to stereo by duplicating to both channels.
        /// </summary>
        /// <param name="monoSamples">Mono samples.</param>
        /// <returns>Interleaved stereo samples.</returns>
        public static float[] MonoToStereo(float[] monoSamples)
        {
            if (monoSamples == null || monoSamples.Length == 0)
                return Array.Empty<float>();

            float[] stereo = new float[monoSamples.Length * 2];

            for (int i = 0; i < monoSamples.Length; i++)
            {
                stereo[i * 2] = monoSamples[i];
                stereo[i * 2 + 1] = monoSamples[i];
            }

            return stereo;
        }

        // ==================== PRIVATE HELPERS ====================

        private static void WriteAscii(byte[] b, int o, string s)
        {
            for (int i = 0; i < s.Length; i++)
                b[o + i] = (byte)s[i];
        }

        private static void WriteInt16(byte[] b, int o, short v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        private static void WriteInt32(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }
    }
}
