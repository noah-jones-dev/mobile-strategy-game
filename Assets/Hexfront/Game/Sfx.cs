using System;
using UnityEngine;

namespace Hexfront
{
    /// <summary>
    /// Sound effects synthesised at start-up (no audio files needed): short blips, gunshots and
    /// cartoon explosions made from sine waves and filtered noise.
    /// </summary>
    public sealed class Sfx : MonoBehaviour
    {
        public enum Id { Click, Order, Error, Ready, Move, Shot, Cannon, Explosion, Capture, Build, Coins, Victory, Defeat }

        static Sfx _instance;
        AudioSource _source;
        AudioClip[] _clips;

        public static bool Muted
        {
            get => PlayerPrefs.GetInt("hf_mute", 0) == 1;
            set { PlayerPrefs.SetInt("hf_mute", value ? 1 : 0); PlayerPrefs.Save(); }
        }

        public static void Play(Id id, float volume = 1f)
        {
            if (_instance == null || Muted) return;
            var clip = _instance._clips[(int)id];
            if (clip != null) _instance._source.PlayOneShot(clip, volume);
        }

        void Awake()
        {
            _instance = this;
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            int n = Enum.GetValues(typeof(Id)).Length;
            _clips = new AudioClip[n];
            _clips[(int)Id.Click] = Tone("click", 0.05f, 880, 880, 0.25f, 0);
            _clips[(int)Id.Order] = Tone("order", 0.09f, 520, 780, 0.3f, 0);
            _clips[(int)Id.Error] = Tone("error", 0.16f, 220, 160, 0.35f, 0);
            _clips[(int)Id.Ready] = Tone("ready", 0.22f, 440, 880, 0.35f, 0);
            _clips[(int)Id.Move] = Tone("move", 0.12f, 140, 110, 0.18f, 0.5f);
            _clips[(int)Id.Shot] = Tone("shot", 0.10f, 900, 200, 0.28f, 0.7f);
            _clips[(int)Id.Cannon] = Tone("cannon", 0.28f, 160, 50, 0.5f, 0.6f);
            _clips[(int)Id.Explosion] = Tone("explosion", 0.55f, 110, 35, 0.6f, 0.9f);
            _clips[(int)Id.Capture] = Tone("capture", 0.3f, 660, 990, 0.3f, 0);
            _clips[(int)Id.Build] = Tone("build", 0.2f, 300, 420, 0.3f, 0.3f);
            _clips[(int)Id.Coins] = Tone("coins", 0.14f, 1200, 1600, 0.2f, 0);
            _clips[(int)Id.Victory] = Tone("victory", 0.9f, 523, 1046, 0.35f, 0);
            _clips[(int)Id.Defeat] = Tone("defeat", 0.9f, 330, 110, 0.35f, 0);
        }

        /// <summary>A pitch sweep from f0 to f1 with a fast-decaying envelope, optionally mixed with noise.</summary>
        static AudioClip Tone(string name, float seconds, float f0, float f1, float gain, float noise)
        {
            const int rate = 22050;
            int count = Mathf.Max(1, (int)(seconds * rate));
            var data = new float[count];
            float phase = 0f, low = 0f;
            uint seed = 12345u + (uint)name.Length * 7919u;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count;
                float freq = Mathf.Lerp(f0, f1, t);
                phase += 2f * Mathf.PI * freq / rate;
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                float white = (seed & 0xFFFF) / 32768f - 1f;
                low += (white - low) * 0.25f; // crude low-pass makes the noise rumble instead of hiss
                float sample = Mathf.Sin(phase) * (1f - noise) + low * noise * 1.8f;
                float envelope = Mathf.Min(1f, i / (0.004f * rate)) * Mathf.Pow(1f - t, 2.2f);
                data[i] = Mathf.Clamp(sample * envelope * gain, -1f, 1f);
            }
            var clip = AudioClip.Create(name, count, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
