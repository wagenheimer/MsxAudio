using System;

namespace Wagenheimer.MsxAudio.Mixing;

/// <summary>
/// Perfil de mixagem do áudio do MSX. O perfil define <b>como</b> PSG e SCC são combinados —
/// a fonte (VGM) é sempre a mesma, então isto escolhe entre "exatamente o original" e versões
/// melhoradas (estéreo, filtros, reverb).
/// </summary>
public sealed class AudioProfile
{
    /// <summary>Nome do perfil (usado nas opções e na linha de comando).</summary>
    public string Name { get; init; } = "original";

    /// <summary>Fator de sobreamostragem da renderização interna (1, 2, 4 ou 8).</summary>
    public int Oversample { get; init; } = 4;

    /// <summary>Ganho aplicado ao PSG (relativo ao SCC — preserva o balanço do chip).</summary>
    public float PsgGain { get; init; } = 1f;

    /// <summary>Ganho aplicado ao SCC.</summary>
    public float SccGain { get; init; } = 1f;

    /// <summary>Panorama dos 3 canais do PSG (-1 = esquerda, 0 = centro, +1 = direita).</summary>
    public float[] PsgPan { get; init; } = new float[3];

    /// <summary>Panorama dos 5 canais do SCC.</summary>
    public float[] SccPan { get; init; } = new float[5];

    /// <summary>Corte do passa-alta em Hz (0 = desligado). Remove o offset DC do chip.</summary>
    public float HighPassHz { get; init; } = 12f;

    /// <summary>Corte do passa-baixa em Hz (0 = desligado). Aproxima o filtro da saída do MSX.</summary>
    public float LowPassHz { get; init; }

    /// <summary>Mistura de reverb (0 = seco, 1 = só reverb).</summary>
    public float ReverbMix { get; init; }

    /// <summary>Normaliza o pico para este valor (0 = mantém o nível do chip, autêntico).</summary>
    public float NormalizePeak { get; init; }

    /// <summary>Ganho mestre aplicado à soma dos chips (antes de virar PCM).</summary>
    public float MasterGain { get; init; } = DefaultMasterGain;

    /// <summary>Ganho que leva a soma máxima teórica dos chips para ~1.0.</summary>
    /// <remarks>
    /// Calibrado contra a captura do openMSX: o nível do render ficava 3,4 dB abaixo da
    /// gravação de referência (mesma forma espectral, só ganho). 1/27000 = 1,48x o valor
    /// anterior, que era 1/40000.
    /// </remarks>
    public const float DefaultMasterGain = 1f / 27000f;

    /// <summary>Fiel ao hardware: mono, sem filtros além do corte de DC.</summary>
    public static AudioProfile Original => new()
    {
        Name = "original",
        Oversample = 4,
        HighPassHz = 12f,
    };

    /// <summary>Fiel ao chip, mas com os canais espalhados em estéreo (ouve-se "maior").</summary>
    public static AudioProfile Stereo => new()
    {
        Name = "stereo",
        Oversample = 4,
        PsgPan = new[] { -0.30f, 0f, 0.30f },
        SccPan = new[] { -0.55f, -0.25f, 0f, 0.25f, 0.55f },
        HighPassHz = 20f,
        LowPassHz = 16000f,
    };

    /// <summary>Master: estéreo mais aberto, sobreamostragem maior, filtros suaves e um reverb curto.</summary>
    public static AudioProfile Remaster => new()
    {
        Name = "remaster",
        Oversample = 8,
        PsgPan = new[] { -0.40f, 0f, 0.40f },
        SccPan = new[] { -0.70f, -0.35f, 0f, 0.35f, 0.70f },
        HighPassHz = 25f,
        LowPassHz = 17000f,
        ReverbMix = 0.18f,
        NormalizePeak = 0.89f,
    };

    /// <summary>Perfis prontos.</summary>
    public static AudioProfile[] All => new[] { Original, Stereo, Remaster };

    /// <summary>Cópia do perfil com outra sobreamostragem (efeitos curtos podem usar menos).</summary>
    public AudioProfile WithOversample(int oversample) => new()
    {
        Name = Name,
        Oversample = Math.Clamp(oversample, 1, 16),
        PsgGain = PsgGain,
        SccGain = SccGain,
        PsgPan = PsgPan,
        SccPan = SccPan,
        HighPassHz = HighPassHz,
        LowPassHz = LowPassHz,
        ReverbMix = ReverbMix,
        NormalizePeak = NormalizePeak,
        MasterGain = MasterGain,
    };

    /// <summary>Busca um perfil pelo nome (padrão: original).</summary>
    public static AudioProfile ByName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "stereo" or "estéreo" => Stereo,
        "remaster" or "master" => Remaster,
        _ => Original,
    };
}
