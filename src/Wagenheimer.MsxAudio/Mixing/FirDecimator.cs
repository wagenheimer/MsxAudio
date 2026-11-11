using System;

namespace Wagenheimer.MsxAudio.Mixing;

/// <summary>
/// Filtro FIR de decimação com estado (sinc com janela Blackman), usado para tirar o
/// serrilhado de amostrar os chips numa taxa alta e baixar para a taxa de saída.
/// </summary>
/// <remarks>
/// O fator é inteiro (2, 4, 8…): a cada <see cref="Factor"/> amostras de entrada sai uma de
/// saída. O estado (histórico) é mantido entre chamadas, então dá para usar tanto no render
/// offline quanto no streaming em tempo real sem descontinuidade.
/// </remarks>
public sealed class FirDecimator
{
    private readonly int _factor;
    private readonly float[] _taps;
    private readonly int _half;
    private readonly float[] _historyL;
    private readonly float[] _historyR;
    private float[] _workL = Array.Empty<float>();
    private float[] _workR = Array.Empty<float>();

    /// <summary>Cria o decimador.</summary>
    /// <param name="factor">Fator de decimação (1 = sem decimação).</param>
    /// <param name="tapsPerPhase">Quantidade de coeficientes por fase (maior = corte mais seco).</param>
    public FirDecimator(int factor, int tapsPerPhase = 12)
    {
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor));

        _factor = factor;
        int tapCount = factor * Math.Max(4, tapsPerPhase) + 1;
        _half = tapCount / 2;
        // corte em 0,45 do Nyquist de saída (passa tudo até ~21,6 kHz em 48 kHz)
        _taps = DesignLowPass(tapCount, 0.45 / factor);
        _historyL = new float[tapCount - 1];
        _historyR = new float[tapCount - 1];
    }

    /// <summary>Fator de decimação.</summary>
    public int Factor => _factor;

    /// <summary>Processa um bloco: <paramref name="countIn"/> amostras entram, <paramref name="countOut"/> saem.</summary>
    public void Process(float[] inputL, float[] inputR, int countIn, float[] outputL, float[] outputR, int countOut)
    {
        if (_factor == 1)
        {
            Array.Copy(inputL, outputL, Math.Min(countIn, countOut));
            Array.Copy(inputR, outputR, Math.Min(countIn, countOut));
            return;
        }

        int historyLength = _historyL.Length;
        int workLength = historyLength + countIn;
        if (_workL.Length < workLength)
        {
            _workL = new float[workLength];
            _workR = new float[workLength];
        }

        Array.Copy(_historyL, 0, _workL, 0, historyLength);
        Array.Copy(_historyR, 0, _workR, 0, historyLength);
        Array.Copy(inputL, 0, _workL, historyLength, countIn);
        Array.Copy(inputR, 0, _workR, historyLength, countIn);

        for (int k = 0; k < countOut; k++)
        {
            int center = historyLength + k * _factor;
            int start = center - _half;
            float sumL = 0f;
            float sumR = 0f;
            for (int j = 0; j < _taps.Length; j++)
            {
                int p = start + j;
                if (p < 0 || p >= workLength) continue;
                float h = _taps[j];
                sumL += _workL[p] * h;
                sumR += _workR[p] * h;
            }

            outputL[k] = sumL;
            outputR[k] = sumR;
        }

        Array.Copy(_workL, workLength - historyLength, _historyL, 0, historyLength);
        Array.Copy(_workR, workLength - historyLength, _historyR, 0, historyLength);
    }

    /// <summary>Zera o histórico (reinício de reprodução).</summary>
    public void Reset()
    {
        Array.Clear(_historyL);
        Array.Clear(_historyR);
    }

    private static float[] DesignLowPass(int tapCount, double cutoff)
    {
        var taps = new float[tapCount];
        int middle = tapCount / 2;
        double sum = 0;
        for (int i = 0; i < tapCount; i++)
        {
            int n = i - middle;
            double sinc = n == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * n) / (Math.PI * n);
            double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (tapCount - 1)) +
                            0.08 * Math.Cos(4 * Math.PI * i / (tapCount - 1));
            taps[i] = (float)(sinc * window);
            sum += taps[i];
        }

        for (int i = 0; i < tapCount; i++) taps[i] = (float)(taps[i] / sum);
        return taps;
    }
}
