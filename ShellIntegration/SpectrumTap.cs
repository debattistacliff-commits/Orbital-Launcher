using NAudio.Dsp;
using NAudio.Wave;

namespace DesktopOrbit.ShellIntegration;

/// <summary>
/// Sits in the radio's audio chain and turns the sound into a few frequency bands (bass on the left, treble on the right)
/// plus an overall level, which the ring draws as lights around the clock.
/// </summary>
public sealed class SpectrumTap
{
    public const int BandCount = 36;
    private const int FftSize = 1024; // 2^10
    private const int FftPower = 10;

    private readonly float[] _bands = new float[BandCount];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly object _gate = new();
    private int _filled;
    private float _level;
    private int _sampleRate = 44100;

    public SpectrumTap()
    {
        for (var i = 0; i < FftSize; i++)
        {
            _window[i] = (float)FastFourierTransform.HammingWindow(i, FftSize);
        }
    }

    /// <summary>Overall loudness 0–1.</summary>
    public float Level => _level;

    public void CopyBands(float[] target)
    {
        lock (_gate)
        {
            Array.Copy(_bands, target, BandCount);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_bands);
            _level = 0;
            _filled = 0;
        }
    }

    public ISampleProvider Attach(ISampleProvider source)
    {
        _sampleRate = source.WaveFormat.SampleRate;
        return new Tap(source, this);
    }

    private void Add(float[] buffer, int offset, int count, int channels)
    {
        for (var i = offset; i + channels - 1 < offset + count; i += channels)
        {
            var mono = 0f;
            for (var c = 0; c < channels; c++)
            {
                mono += buffer[i + c];
            }

            mono /= channels;
            _fft[_filled].X = mono * _window[_filled];
            _fft[_filled].Y = 0;
            if (++_filled == FftSize)
            {
                _filled = 0;
                Analyse();
            }
        }
    }

    private void Analyse()
    {
        FastFourierTransform.FFT(true, FftPower, _fft);

        // Bands spaced on a log scale from 40 Hz to 14 kHz, like a graphic equaliser.
        var hzPerBin = (double)_sampleRate / FftSize;
        var total = 0f;
        lock (_gate)
        {
            for (var band = 0; band < BandCount; band++)
            {
                var lowHz = 40 * Math.Pow(14000.0 / 40, (double)band / BandCount);
                var highHz = 40 * Math.Pow(14000.0 / 40, (double)(band + 1) / BandCount);
                int low = Math.Max(1, (int)(lowHz / hzPerBin)), high = Math.Max(low + 1, (int)(highHz / hzPerBin));
                var peak = 0.0;
                for (var bin = low; bin < high && bin < FftSize / 2; bin++)
                {
                    peak = Math.Max(peak, Math.Sqrt(_fft[bin].X * _fft[bin].X + _fft[bin].Y * _fft[bin].Y));
                }

                // Decibels mapped to 0–1, with a gentle boost for treble which is naturally quieter.
                var db = 20 * Math.Log10(peak + 1e-9) + band * 0.35;
                var value = (float)Math.Clamp((db + 62) / 52, 0, 1);
                _bands[band] = value;
                total += value;
            }

            _level = total / BandCount;
        }
    }

    private sealed class Tap(ISampleProvider source, SpectrumTap owner) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            owner.Add(buffer, offset, read, source.WaveFormat.Channels);
            return read;
        }
    }
}
