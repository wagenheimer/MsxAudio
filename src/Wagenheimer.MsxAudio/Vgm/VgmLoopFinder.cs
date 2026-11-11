using System;

namespace Wagenheimer.MsxAudio.Vgm;

/// <summary>
/// Descobre o ponto exato de loop de um VGM comparando a sequência de escritas de registrador.
/// </summary>
/// <remarks>
/// O VGM gravado pelo openMSX não traz a informação de loop (o driver de som do jogo é que
/// decide repetir). A repetição aparece no log como uma sequência de escritas <b>idêntica</b>
/// ao trecho anterior. Procurar essa repetição dá o loop com precisão de amostra — é isso que
/// elimina o "estalo" na emenda que o corte por tempo de uma gravação separada produzia.
/// </remarks>
public static class VgmLoopFinder
{
    /// <summary>
    /// Encontra o loop do VGM.
    /// </summary>
    /// <param name="vgm">Arquivo lido.</param>
    /// <param name="minSpanSeconds">
    /// Quanto tempo a repetição precisa se manter para ser aceita (25 s por padrão). Evita
    /// confundir um compasso repetido com o loop da música.
    /// </param>
    /// <param name="log">Diagnóstico opcional (candidatos avaliados).</param>
    /// <returns>Início e duração do loop em segundos, ou <c>null</c> se não houver.</returns>
    public static (double Start, double Duration)? Find(
        VgmFile vgm,
        double minSpanSeconds = 25.0,
        Action<string>? log = null)
    {
        var events = vgm.Events;
        int n = events.Count;
        if (n < 200) return null;

        // Assinatura compacta de cada escrita (para comparar rápido).
        int[] signature = new int[n];
        long[] times = new long[n];
        for (int i = 0; i < n; i++)
        {
            VgmEvent e = events[i];
            signature[i] = (int)e.Kind | (e.P0 << 8) | (e.P1 << 16) | (e.P2 << 24);
            times[i] = e.Sample;
        }

        long minSpan = (long)(minSpanSeconds * 44100.0);
        long minPeriod = (long)(4 * 44100.0);
        int maxStart = n / 3;

        for (int start = 0; start < maxStart; start += 40)
        {
            for (int k = 50; start + k < n; k++)
            {
                if (signature[start] != signature[start + k])
                    continue;

                int run = 0;
                while (start + run + k < n && signature[start + run] == signature[start + run + k])
                    run++;

                if (run < 2) continue;

                long span = times[start + run - 1] - times[start];
                long period = times[start + k] - times[start];
                if (period <= 0) continue;

                // Loop de verdade: a sequência se repete por pelo menos um período inteiro.
                // Se a captura terminou antes, aceita quando o trecho repetido cobre ~o período
                // (é o caso de músicas longas gravadas por pouco tempo além de um loop).
                bool fullPeriod = run >= k;
                bool reachedEnd = start + run + k >= n;
                bool accepted = fullPeriod
                    ? span >= minSpan
                    : reachedEnd && span >= period * 0.9 && period > minPeriod;
                if (!accepted) continue;

                log?.Invoke(
                    $"candidato start={times[start] / 44100.0:F3}s period={period / 44100.0:F3}s run={run} span={span / 44100.0:F2}s idx={start},{k}");
                return (times[start] / 44100.0, period / 44100.0);
            }
        }

        return null;
    }
}
