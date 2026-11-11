using System;

namespace Wagenheimer.MsxAudio.Mixing;

/// <summary>Reverb estéreo curto (combs + allpass) usado pelo perfil "remaster".</summary>
public sealed class StereoReverb
{
    private static readonly int[] CombDelays = { 1116, 1188, 1277, 1356 };
    private static readonly int[] AllPassDelays = { 556, 441 };

    private readonly float[][] _combsL = new float[4][];
    private readonly float[][] _combsR = new float[4][];
    private readonly int[] _combIndexL = new int[4];
    private readonly int[] _combIndexR = new int[4];
    private readonly float[] _dampL = new float[4];
    private readonly float[] _dampR = new float[4];

    private readonly float[][] _allPassL = new float[2][];
    private readonly float[][] _allPassR = new float[2][];
    private readonly int[] _allPassIndexL = new int[2];
    private readonly int[] _allPassIndexR = new int[2];

    /// <summary>Cria o reverb para uma taxa de amostragem.</summary>
    public StereoReverb(int sampleRate)
    {
        double scale = sampleRate / 44100.0;
        for (int i = 0; i < 4; i++)
        {
            _combsL[i] = new float[Math.Max(4, (int)(CombDelays[i] * scale))];
            _combsR[i] = new float[Math.Max(4, (int)((CombDelays[i] + 23) * scale))];
        }

        for (int i = 0; i < 2; i++)
        {
            _allPassL[i] = new float[Math.Max(4, (int)(AllPassDelays[i] * scale))];
            _allPassR[i] = new float[Math.Max(4, (int)((AllPassDelays[i] + 23) * scale))];
        }
    }

    /// <summary>Mistura o reverb nos buffers (in place).</summary>
    public void Process(float[] left, float[] right, int count, float mix)
    {
        const float feedback = 0.78f;
        const float damping = 0.25f;
        const float allPassFeedback = 0.5f;

        for (int n = 0; n < count; n++)
        {
            float inL = left[n];
            float inR = right[n];
            float wetL = 0f;
            float wetR = 0f;

            for (int c = 0; c < 4; c++)
            {
                wetL += ProcessComb(_combsL[c], ref _combIndexL[c], ref _dampL[c], inL, feedback, damping);
                wetR += ProcessComb(_combsR[c], ref _combIndexR[c], ref _dampR[c], inR, feedback, damping);
            }

            for (int a = 0; a < 2; a++)
            {
                wetL = ProcessAllPass(_allPassL[a], ref _allPassIndexL[a], wetL, allPassFeedback);
                wetR = ProcessAllPass(_allPassR[a], ref _allPassIndexR[a], wetR, allPassFeedback);
            }

            left[n] = inL * (1f - mix) + wetL * 0.25f * mix;
            right[n] = inR * (1f - mix) + wetR * 0.25f * mix;
        }
    }

    private static float ProcessComb(float[] buffer, ref int index, ref float damp, float input, float feedback, float damping)
    {
        float output = buffer[index];
        damp = output * (1f - damping) + damp * damping;
        buffer[index] = input + damp * feedback;
        index = (index + 1) % buffer.Length;
        return output;
    }

    private static float ProcessAllPass(float[] buffer, ref int index, float input, float feedback)
    {
        float buffered = buffer[index];
        float output = -input + buffered;
        buffer[index] = input + buffered * feedback;
        index = (index + 1) % buffer.Length;
        return output;
    }
}
